using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AnimalHospitalOverlay;

/// <summary>
/// Receives relative mouse motion through WM_INPUT. Roblox uses raw/locked
/// mouse input during camera control, so ordinary screen-coordinate mouse hooks
/// only report useful movement while a menu releases the cursor.
/// </summary>
public sealed class RawMouseInput : IDisposable
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const uint RimTypeMouse = 0;

    private HwndSource? _source;
    private IntPtr _window;

    public event Action<int, int>? Moved;

    public static bool IsSystemCursorVisible()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && (info.Flags & 0x00000001) != 0;
    }

    public void Attach(Window window)
    {
        _window = new WindowInteropHelper(window).Handle;
        if (_window == IntPtr.Zero)
            throw new InvalidOperationException("The overlay window handle is not available.");

        _source = HwndSource.FromHwnd(_window);
        _source?.AddHook(WindowProcedure);

        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = 0x01,
                Usage = 0x02,
                Flags = RidevInputSink,
                Target = _window
            }
        };
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new InvalidOperationException("Could not register raw mouse input.");
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmInput)
            return IntPtr.Zero;

        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, headerSize);
        if (size == 0)
            return IntPtr.Zero;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RidInput, buffer, ref size, headerSize) != size)
                return IntPtr.Zero;

            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != RimTypeMouse)
                return IntPtr.Zero;

            // RAWMOUSE begins immediately after the architecture-sized header.
            // lLastX/lLastY are at byte offsets 12 and 16 within RAWMOUSE.
            var mouse = IntPtr.Add(buffer, Marshal.SizeOf<RawInputHeader>());
            var dx = Marshal.ReadInt32(mouse, 12);
            var dy = Marshal.ReadInt32(mouse, 16);
            if (Math.Abs(dx) < 5000 && Math.Abs(dy) < 5000)
                Moved?.Invoke(dx, dy);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source?.RemoveHook(WindowProcedure);
        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = 0x01,
                Usage = 0x02,
                Flags = RidevRemove,
                Target = IntPtr.Zero
            }
        };
        RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>());
        _source = null;
        _window = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public Point ScreenPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        RawInputDevice[] devices, uint deviceCount, uint deviceSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(
        IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo cursorInfo);
}
