using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace AnimalHospitalOverlay;

public partial class MainWindow : Window
{
    private enum CommandStage { Idle, Root, RoomAction, Direction }

    private readonly StateStore _store = new();
    private readonly KeyboardHook _keyboard = new();
    private readonly RawMouseInput _rawMouse = new();
    private readonly MovementTracker _movementTracker = new();
    private readonly TraversalLogger _traversalLogger = new();
    private readonly VisionProbeService _visionProbe = new();
    private readonly DispatcherTimer _displayTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _commandTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly Stopwatch _shiftWatch = new();
    private readonly Stopwatch _smallCoffeeWatch = new();
    private readonly Stopwatch _tallCoffeeWatch = new();
    private readonly Dictionary<int, Button> _roomButtons;
    private OverlayState _state;
    private CommandStage _commandStage;
    private int _selectedRoom;
    private bool _testMode;
    private DateTime _lastMovementUpdate = DateTime.UtcNow;
    private volatile bool _robloxForeground;
    private bool _calibrationEnabled;
    private int? _highlightedRoom;
    private DateTime _suppressTrackingUntil = DateTime.MinValue;
    private bool _hudHidden;

    public MainWindow()
    {
        InitializeComponent();

        _roomButtons = new Dictionary<int, Button>
        {
            [1] = Room1, [2] = Room2, [3] = Room3, [4] = Room4,
            [5] = Room5, [6] = Room6, [7] = Room7, [8] = Room8
        };

        _state = _store.Load();
        NormalizeState();
        _movementTracker.MouseSensitivity = _state.RobloxMouseSensitivity;
        SensitivitySlider.Value = _state.RobloxMouseSensitivity;
        RefreshAll();

        _displayTimer.Tick += (_, _) =>
        {
            RefreshTimers();
            RefreshTracking();
        };
        _displayTimer.Start();
        _commandTimer.Tick += (_, _) => CancelCommand();

        _keyboard.KeyPressed += HandleGlobalKey;
        _keyboard.KeyChanged += HandleKeyChanged;
        _keyboard.Start();
        _visionProbe.StatusChanged += status =>
            Dispatcher.BeginInvoke(() => VisionCandidateText.Text = status);
        _visionProbe.CandidateReceived += candidate =>
            Dispatcher.BeginInvoke(() =>
            {
                VisionCandidateText.Text =
                    $"Vision candidate: {candidate.Location} • {candidate.Confidence:P0} • {candidate.ProcessingMilliseconds:0} ms";
                VisionCandidateText.Foreground = candidate.Confidence >= 0.7
                    ? new SolidColorBrush(Color.FromRgb(100, 214, 154))
                    : new SolidColorBrush(Color.FromRgb(243, 201, 105));
            });
        SourceInitialized += (_, _) =>
        {
            _rawMouse.Moved += HandleMouseMoved;
            _rawMouse.Attach(this);
        };
        Closed += (_, _) =>
        {
            _keyboard.Dispose();
            _rawMouse.Dispose();
            _visionProbe.Dispose();
            _store.Save(_state);
        };
    }

    private void NormalizeState()
    {
        if (_state.CurrentShiftNumber < 1)
            _state.CurrentShiftNumber = 1;
        if (_state.RobloxMouseSensitivity is < 0.1 or > 4)
            _state.RobloxMouseSensitivity = 0.2;

        foreach (var number in Enumerable.Range(1, 8))
        {
            if (_state.Rooms.All(room => room.Number != number))
                _state.Rooms.Add(new RoomState { Number = number });
        }
    }

    private bool HandleGlobalKey(Key key)
    {
        var active = KeyboardHook.IsRobloxForeground() || _testMode;
        if (!active)
        {
            if (_commandStage != CommandStage.Idle)
                Dispatcher.BeginInvoke(CancelCommand);
            return false;
        }

        if (_commandStage == CommandStage.Idle)
        {
            if (key != Key.End)
                return false;
            Dispatcher.Invoke(() => SetCommandStage(CommandStage.Root));
            return true;
        }

        Dispatcher.Invoke(() => ProcessCommandKey(key));
        return true;
    }

    private void HandleKeyChanged(Key key, bool isDown)
    {
        if (key == Key.Escape && isDown && _robloxForeground)
            _suppressTrackingUntil = DateTime.UtcNow.AddSeconds(1);
        if (_movementTracker.Enabled && _robloxForeground)
            _movementTracker.SetKey(key, isDown);
        else if (!isDown)
            _movementTracker.SetKey(key, false);
    }

    private void HandleMouseMoved(int dx, int dy)
    {
        if (_movementTracker.Enabled && _robloxForeground)
            _movementTracker.AddMouseDelta(dx);
    }

    private void ProcessCommandKey(Key key)
    {
        RestartCommandTimeout();

        if (key == Key.Escape)
        {
            CancelCommand();
            return;
        }

        switch (_commandStage)
        {
            case CommandStage.Root:
                var roomNumber = GetNumberKey(key);
                if (roomNumber != 0)
                {
                    _selectedRoom = roomNumber;
                    SetCommandStage(CommandStage.RoomAction);
                }
                else if (key == Key.OemMinus) { StartCoffee(false); CancelCommand(); }
                else if (key == Key.OemPlus)
                {
                    if (StartCoffee(true))
                        CancelCommand();
                }
                else if (key == Key.Oem5) { ToggleShift(); CancelCommand(); }
                else if (key == Key.Oem6) { ResetShiftHud(); CancelCommand(); }
                else if (key == Key.Delete) { ClearAllRoomStates(); CancelCommand(); }
                else if (key == Key.PageUp) { ToggleTallCoffee(); CancelCommand(); }
                else if (key == Key.Back) { StartNewRun(); CancelCommand(); }
                else if (key == Key.H) { ToggleHudVisibility(); CancelCommand(); }
                else if (key == Key.J) { SetTrackingAnchor("Hall Center"); CancelCommand(); }
                else if (key == Key.R) { SetTrackingAnchor("Reception"); CancelCommand(); }
                else if (key == Key.O) { SetTrackingAnchor("Office"); CancelCommand(); }
                else if (key == Key.M) { SetTrackingAnchor("Medical Bay"); CancelCommand(); }
                else if (key == Key.E) { SetTrackingAnchor("Emergency Bay"); CancelCommand(); }
                else if (key == Key.C)
                {
                    if (TrySelectCurrentRoom())
                        SetCommandStage(CommandStage.RoomAction);
                    else
                    {
                        GuideTitle.Text = "CURRENT ROOM UNCERTAIN";
                        GuideText.Text = "Set an anchor or wait for tracking confidence, then try C again";
                    }
                }
                else if (key == Key.K) { ToggleCalibration(); CancelCommand(); }
                else if (key == Key.D) { SetCommandStage(CommandStage.Direction); }
                else ShowInvalidKey();
                break;

            case CommandStage.RoomAction:
                if (key == Key.PageUp) { SetRoomPatient(_selectedRoom, RoomPatientState.Safe); CancelCommand(); }
                else if (key == Key.PageDown) { SetRoomPatient(_selectedRoom, RoomPatientState.Anomaly); CancelCommand(); }
                else if (key == Key.Delete) { SetRoomPatient(_selectedRoom, RoomPatientState.Neutral); CancelCommand(); }
                else if (key == Key.OemMinus) { SetRoomEvent(_selectedRoom, RoomEventState.Clear); CancelCommand(); }
                else if (key == Key.OemPlus) { SetRoomEvent(_selectedRoom, RoomEventState.Active); CancelCommand(); }
                else if (key == Key.Back) { ClearRoom(_selectedRoom); CancelCommand(); }
                else if (key == Key.Oem5) { SetTrackingAnchor($"Room {_selectedRoom}"); CancelCommand(); }
                else ShowInvalidKey();
                break;

            case CommandStage.Direction:
                if (key is Key.N or Key.Up) { SetHeading(0); CancelCommand(); }
                else if (key is Key.E or Key.Right) { SetHeading(90); CancelCommand(); }
                else if (key is Key.S or Key.Down) { SetHeading(180); CancelCommand(); }
                else if (key is Key.W or Key.Left) { SetHeading(270); CancelCommand(); }
                else ShowInvalidKey();
                break;
        }
    }

    private static int GetNumberKey(Key key)
    {
        return key switch
        {
            >= Key.D1 and <= Key.D8 => key - Key.D0,
            >= Key.NumPad1 and <= Key.NumPad8 => key - Key.NumPad0,
            _ => 0
        };
    }

    private void SetCommandStage(CommandStage stage)
    {
        _commandStage = stage;
        GuidePanel.Visibility = Visibility.Visible;
        RestartCommandTimeout();

        (GuideTitle.Text, GuideText.Text) = stage switch
        {
            CommandStage.Root => ("END • COMMAND MODE",
                "H Hide HUD  •  C Current room  •  1–8 Room  •  D Direction  •  Del Clear rooms  •  J/R/O/M/E Anchors"),
            CommandStage.RoomAction => ($"ROOM {_selectedRoom}",
                "\\ Anchor here  •  PgUp Safe  •  PgDown Anomaly  •  Del Clear patient  •  = Event  •  - Clear event  •  Backspace Clear room"),
            CommandStage.Direction => ("RESET FACING",
                "↑ or N North  •  → or E East  •  ↓ or S South  •  ← or W West  •  Esc Cancel"),
            _ => ("", "")
        };
    }

    private void RestartCommandTimeout()
    {
        _commandTimer.Stop();
        _commandTimer.Start();
    }

    private void ShowInvalidKey()
    {
        GuideTitle.Text = "KEY NOT ASSIGNED";
        GuideTitle.Foreground = new SolidColorBrush(Color.FromRgb(242, 121, 121));
    }

    private void CancelCommand()
    {
        _commandTimer.Stop();
        _commandStage = CommandStage.Idle;
        GuidePanel.Visibility = Visibility.Visible;
        GuideTitle.Text = "KEYBINDS";
        GuideText.Text = "End  Commands";
        GuideTitle.Foreground = new SolidColorBrush(Color.FromRgb(243, 201, 105));
    }

    private void SetRoomPatient(int number, RoomPatientState patientState)
    {
        _state.Rooms.First(room => room.Number == number).Patient = patientState;
        RefreshRoom(number);
        _store.Save(_state);
    }

    private void SetRoomEvent(int number, RoomEventState eventState)
    {
        _state.Rooms.First(room => room.Number == number).Event = eventState;
        RefreshRoom(number);
        _store.Save(_state);
    }

    private void ClearRoom(int number)
    {
        var room = _state.Rooms.First(item => item.Number == number);
        room.Patient = RoomPatientState.Neutral;
        room.Event = RoomEventState.Clear;
        RefreshRoom(number);
        _store.Save(_state);
    }

    private void RefreshRoom(int number)
    {
        var room = _state.Rooms.First(item => item.Number == number);
        var state = room.Patient;
        var button = _roomButtons[number];
        var patientLabel = state == RoomPatientState.Neutral ? "CLEAR" : state.ToString().ToUpperInvariant();
        button.Content = room.Event == RoomEventState.Active
            ? $"{number}\n{patientLabel} !"
            : $"{number}\n{patientLabel}";
        button.BorderBrush = room.Event == RoomEventState.Active
            ? new SolidColorBrush(Color.FromRgb(243, 201, 105))
            : new SolidColorBrush(Color.FromRgb(86, 97, 115));
        button.BorderThickness = room.Event == RoomEventState.Active
            ? new Thickness(3)
            : new Thickness(1);
        button.Background = state switch
        {
            RoomPatientState.Safe => new SolidColorBrush(Color.FromRgb(31, 112, 73)),
            RoomPatientState.Anomaly => new SolidColorBrush(Color.FromRgb(150, 47, 51)),
            _ => new SolidColorBrush(Color.FromRgb(38, 48, 62))
        };
        button.Effect = _highlightedRoom == number
            ? new DropShadowEffect
            {
                Color = Color.FromRgb(89, 205, 255),
                BlurRadius = 16,
                ShadowDepth = 0,
                Opacity = 0.95
            }
            : null;
    }

    private void ToggleShift()
    {
        if (_shiftWatch.IsRunning)
        {
            _shiftWatch.Stop();
            if (_shiftWatch.Elapsed >= TimeSpan.FromSeconds(1))
            {
                _state.ShiftHistory.Insert(0, new ShiftRecord
                {
                    CompletedAt = DateTime.Now,
                    Duration = _shiftWatch.Elapsed
                });
                if (_state.ShiftHistory.Count > 100)
                    _state.ShiftHistory.RemoveRange(100, _state.ShiftHistory.Count - 100);
            }
            _shiftWatch.Reset();
            ClearRooms();
            _state.CurrentShiftNumber++;
        }
        else
        {
            _shiftWatch.Restart();
        }

        RefreshAll();
        _store.Save(_state);
    }

    private bool StartCoffee(bool tall)
    {
        if (tall)
        {
            if (!_state.TallCoffeeUnlocked)
            {
                GuideTitle.Text = "TALL MACHINE LOCKED";
                GuideText.Text = "Esc, then End → PgUp to unlock it";
                return false;
            }
            _tallCoffeeWatch.Restart();
        }
        else
        {
            _smallCoffeeWatch.Restart();
        }
        RefreshTimers();
        return true;
    }

    private void ToggleTallCoffee()
    {
        _state.TallCoffeeUnlocked = !_state.TallCoffeeUnlocked;
        if (!_state.TallCoffeeUnlocked)
            _tallCoffeeWatch.Reset();
        TallCoffeeRow.Visibility = _state.TallCoffeeUnlocked ? Visibility.Visible : Visibility.Collapsed;
        _store.Save(_state);
    }

    private void ResetShiftHud()
    {
        _shiftWatch.Reset();
        _smallCoffeeWatch.Reset();
        _tallCoffeeWatch.Reset();
        ClearRooms();
        RefreshAll();
        _store.Save(_state);
    }

    private void StartNewRun()
    {
        _shiftWatch.Reset();
        _smallCoffeeWatch.Reset();
        _tallCoffeeWatch.Reset();
        _state.ShiftHistory.Clear();
        _state.CurrentShiftNumber = 1;
        ClearRooms();
        RefreshAll();
        _store.Save(_state);
    }

    private void ClearRooms()
    {
        foreach (var room in _state.Rooms)
        {
            room.Patient = RoomPatientState.Neutral;
            room.Event = RoomEventState.Clear;
        }
    }

    private void ClearAllRoomStates()
    {
        ClearRooms();
        RefreshAll();
        _store.Save(_state);
    }

    private void RefreshTimers()
    {
        ShiftTimerText.Text = FormatDuration(_shiftWatch.Elapsed, true);
        ShiftButton.Content = _shiftWatch.IsRunning ? "FINISH" : "START";
        RefreshCoffee(_smallCoffeeWatch, TimeSpan.FromSeconds(180), SmallCoffeeText);
        RefreshCoffee(_tallCoffeeWatch, TimeSpan.FromSeconds(300), TallCoffeeText);
    }

    private void SetTrackingAnchor(string location)
    {
        var completed = _movementTracker.AnchorAt(location);
        if (_calibrationEnabled && completed is not null)
            _traversalLogger.Append(completed);
        CalibrationText.Text = _calibrationEnabled
            ? $"Recording • {_traversalLogger.Count} completed • anchor: {location}"
            : "Calibration inactive";
        RefreshTracking();
    }

    private void SetHeading(double degrees)
    {
        _movementTracker.SetHeading(degrees);
        _lastMovementUpdate = DateTime.UtcNow;
        RefreshTracking();
    }

    private void ToggleHudVisibility()
    {
        _hudHidden = !_hudHidden;
        if (_hudHidden)
            Hide();
        else
        {
            Show();
            Topmost = true;
        }
    }

    private void RefreshTracking()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastMovementUpdate;
        _lastMovementUpdate = now;
        _robloxForeground = KeyboardHook.IsRobloxForeground();
        var menuOpen = _robloxForeground &&
                       (RawMouseInput.IsSystemCursorVisible() || now < _suppressTrackingUntil);
        _movementTracker.Update(elapsed, _robloxForeground && !menuOpen);

        if (!_movementTracker.Enabled)
        {
            TrackingLocationText.Text = "Tracking disabled";
            TrackingConfidenceText.Text = "0%";
            TrackingFacingText.Text = "Enable TRACK, then set an anchor";
            SetHighlightedRoom(null);
            return;
        }

        var snapshot = _movementTracker.Snapshot();
        TrackingLocationText.Text = snapshot.Location;
        TrackingConfidenceText.Text = $"{snapshot.Confidence}%";
        TrackingConfidenceText.Foreground = snapshot.Confidence switch
        {
            >= 70 => new SolidColorBrush(Color.FromRgb(100, 214, 154)),
            >= 35 => new SolidColorBrush(Color.FromRgb(243, 201, 105)),
            _ => new SolidColorBrush(Color.FromRgb(242, 121, 121))
        };
        TrackingFacingText.Text = menuOpen
            ? "MENU OPEN • tracking paused"
            : $"Facing {snapshot.Facing} • anchor: {_movementTracker.Anchor}";
        SetHighlightedRoom(snapshot.Confidence >= 30 && snapshot.Location == snapshot.Nearest
            && snapshot.Nearest.StartsWith("Room ")
            && int.TryParse(snapshot.Nearest.AsSpan(5), out var roomNumber)
                ? roomNumber
                : null);
    }

    private bool TrySelectCurrentRoom()
    {
        if (!_movementTracker.Enabled)
            return false;
        var snapshot = _movementTracker.Snapshot();
        if (snapshot.Confidence < 30 || snapshot.Location != snapshot.Nearest
            || !snapshot.Nearest.StartsWith("Room ")
            || !int.TryParse(snapshot.Nearest.AsSpan(5), out var roomNumber))
            return false;
        _selectedRoom = roomNumber;
        return true;
    }

    private void SetHighlightedRoom(int? roomNumber)
    {
        if (_highlightedRoom == roomNumber)
            return;
        var previous = _highlightedRoom;
        _highlightedRoom = roomNumber;
        if (previous is not null)
            RefreshRoom(previous.Value);
        if (_highlightedRoom is not null)
            RefreshRoom(_highlightedRoom.Value);
    }

    private static void RefreshCoffee(Stopwatch watch, TimeSpan cooldown, TextBlock label)
    {
        var remaining = cooldown - watch.Elapsed;
        if (!watch.IsRunning || remaining <= TimeSpan.Zero)
        {
            if (watch.IsRunning)
                watch.Stop();
            label.Text = "READY";
            label.Foreground = new SolidColorBrush(Color.FromRgb(100, 214, 154));
        }
        else
        {
            label.Text = FormatDuration(remaining, false);
            label.Foreground = new SolidColorBrush(Color.FromRgb(243, 201, 105));
        }
    }

    private static string FormatDuration(TimeSpan duration, bool tenths)
    {
        var hours = (int)duration.TotalHours;
        return hours > 0
            ? $"{hours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : tenths
                ? $"{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds / 100}"
                : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private void RefreshAll()
    {
        foreach (var room in Enumerable.Range(1, 8))
            RefreshRoom(room);
        TallCoffeeRow.Visibility = _state.TallCoffeeUnlocked ? Visibility.Visible : Visibility.Collapsed;
        ShiftNumberText.Text = $"SHIFT {_state.CurrentShiftNumber}";
        HistoryText.Text = _state.ShiftHistory.Count == 0
            ? "No completed shifts yet."
            : string.Join("   •   ", _state.ShiftHistory.Take(3)
                .Select(record => $"{record.Duration:mm\\:ss}"));
        RefreshTimers();
    }

    private void Room_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(button.Tag?.ToString(), out var number))
            return;
        var room = _state.Rooms.First(item => item.Number == number);
        var next = room.Patient switch
        {
            RoomPatientState.Neutral => RoomPatientState.Safe,
            RoomPatientState.Safe => RoomPatientState.Anomaly,
            _ => RoomPatientState.Neutral
        };
        SetRoomPatient(number, next);
    }

    private void Shift_Click(object sender, RoutedEventArgs e) => ToggleShift();
    private void TestMode_Click(object sender, RoutedEventArgs e)
    {
        _testMode = !_testMode;
        TestModeButton.Content = _testMode ? "TEST: ON" : "TEST: OFF";
        TestModeButton.Foreground = _testMode
            ? new SolidColorBrush(Color.FromRgb(243, 201, 105))
            : new SolidColorBrush(Color.FromRgb(234, 240, 247));
    }
    private void Tracking_Click(object sender, RoutedEventArgs e)
    {
        _movementTracker.Enabled = !_movementTracker.Enabled;
        TrackingButton.Content = _movementTracker.Enabled ? "TRACK: ON" : "TRACK: OFF";
        TrackingButton.Foreground = _movementTracker.Enabled
            ? new SolidColorBrush(Color.FromRgb(100, 214, 154))
            : new SolidColorBrush(Color.FromRgb(234, 240, 247));
        _lastMovementUpdate = DateTime.UtcNow;
        RefreshTracking();
    }
    private void Calibration_Click(object sender, RoutedEventArgs e) => ToggleCalibration();

    private void Vision_Click(object sender, RoutedEventArgs e)
    {
        if (_visionProbe.IsRunning)
        {
            _visionProbe.Stop();
            VisionButton.Content = "VISION: OFF";
            VisionButton.Foreground = new SolidColorBrush(Color.FromRgb(234, 240, 247));
        }
        else if (_visionProbe.Start())
        {
            VisionButton.Content = "VISION: ON";
            VisionButton.Foreground = new SolidColorBrush(Color.FromRgb(100, 214, 154));
        }
    }

    private void ToggleCalibration()
    {
        _calibrationEnabled = !_calibrationEnabled;
        if (_calibrationEnabled)
            _movementTracker.Enabled = true;
        CalibrationButton.Content = _calibrationEnabled ? "CAL: ON" : "CAL: OFF";
        CalibrationButton.Foreground = _calibrationEnabled
            ? new SolidColorBrush(Color.FromRgb(243, 201, 105))
            : new SolidColorBrush(Color.FromRgb(234, 240, 247));
        TrackingButton.Content = _movementTracker.Enabled ? "TRACK: ON" : "TRACK: OFF";
        CalibrationText.Text = _calibrationEnabled
            ? $"Recording • {_traversalLogger.Count} completed • set start anchor"
            : $"Calibration inactive • session has {_traversalLogger.Count}";
        RefreshTracking();
    }
    private void Sensitivity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
            return;
        var value = Math.Round(e.NewValue, 1);
        _movementTracker.MouseSensitivity = value;
        SensitivityText.Text = value.ToString("0.0");
        if (_state is not null)
        {
            _state.RobloxMouseSensitivity = value;
            _store.Save(_state);
        }
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
