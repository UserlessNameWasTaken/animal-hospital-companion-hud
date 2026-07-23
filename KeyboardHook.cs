using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace AnimalHospitalOverlay;

public sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;

    private readonly HookProc _callback;
    private IntPtr _hook;

    public event Func<Key, bool>? KeyPressed;
    public event Action<Key, bool>? KeyChanged;

    public KeyboardHook()
    {
        _callback = HookCallback;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero)
            return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(module?.ModuleName), 0);
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message == WmKeyDown || message == WmSysKeyDown
                          || message == WmKeyUp || message == WmSysKeyUp))
        {
            var virtualKey = Marshal.ReadInt32(data);
            var key = KeyInterop.KeyFromVirtualKey(virtualKey);
            var isDown = message == WmKeyDown || message == WmSysKeyDown;
            KeyChanged?.Invoke(key, isDown);
            if (isDown && KeyPressed?.Invoke(key) == true)
                return new IntPtr(1);
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    public static bool IsRobloxForeground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return false;

        GetWindowThreadProcessId(window, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Contains("Roblox", StringComparison.OrdinalIgnoreCase)
                   || process.MainWindowTitle.Contains("Roblox", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
