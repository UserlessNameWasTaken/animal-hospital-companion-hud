using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AnimalHospitalTeam.Shared;

namespace AnimalHospitalTeam.Client;

public partial class MainWindow : Window
{
    private enum CommandStage { Idle, Root, RoomAction }

    private readonly TeamConnectionService _connection = new();
    private readonly KeyboardHook _keyboard = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _commandTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly Dictionary<int, Button> _roomButtons;
    private TeamState _state = new();
    private RoomActionMode _roomMode = RoomActionMode.Safe;
    private CommandStage _commandStage;
    private int _selectedRoom;
    private bool _hudHidden;

    public MainWindow()
    {
        InitializeComponent();
        _roomButtons = new()
        {
            [1] = Room1, [2] = Room2, [3] = Room3, [4] = Room4,
            [5] = Room5, [6] = Room6, [7] = Room7, [8] = Room8
        };
        _connection.StatusChanged += status => Dispatcher.BeginInvoke(() => StatusText.Text = status);
        _connection.ConnectionChanged += connected => Dispatcher.BeginInvoke(() =>
        {
            ConnectionDot.Fill = new SolidColorBrush(connected
                ? Color.FromRgb(100, 214, 154)
                : Color.FromRgb(225, 107, 107));
        });
        _connection.StateReceived += state => Dispatcher.BeginInvoke(() =>
        {
            _state = state;
            RefreshState();
        });
        _timer.Tick += (_, _) => RefreshTimers();
        _timer.Start();
        _commandTimer.Tick += (_, _) => CancelCommand();
        _keyboard.KeyPressed += HandleGlobalKey;
        _keyboard.Start();
        Closed += (_, _) =>
        {
            _keyboard.Dispose();
            _connection.Dispose();
        };
        Loaded += async (_, _) => await AutoJoinFromArgumentsAsync();
        SetRoomMode(RoomActionMode.Safe);
    }

    private async Task AutoJoinFromArgumentsAsync()
    {
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        string? Read(string key)
        {
            var index = Array.IndexOf(arguments, key);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
        var name = Read("--name");
        var room = Read("--room");
        var secret = Read("--secret");
        var server = Read("--server");
        if (name is null) return;
        if (int.TryParse(Read("--slot"), out var slot))
        {
            var area = SystemParameters.WorkArea;
            Width = Math.Min(800, area.Width / 2);
            Height = Math.Min(500, area.Height / 2);
            Left = slot % 2 == 0 ? area.Left : area.Left + area.Width / 2;
            Top = slot < 2 ? area.Top : area.Top + area.Height / 2;
        }
        NameBox.Text = name;
        if (room is null || secret is null || server is null) return;
        RoomCodeBox.Text = room;
        SecretBox.Text = secret;
        ServerBox.Text = server;
        await JoinAsync();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateName()) return;
        try
        {
            var created = await _connection.CreateRoomAsync(ServerBox.Text);
            RoomCodeBox.Text = created.Code;
            SecretBox.Text = created.Secret;
            await JoinAsync();
        }
        catch (Exception ex) { StatusText.Text = $"Create failed: {ex.Message}"; }
    }

    private async void Join_Click(object sender, RoutedEventArgs e) => await JoinAsync();
    private void Leave_Click(object sender, RoutedEventArgs e) => _connection.Disconnect();

    private async Task JoinAsync()
    {
        if (!ValidateName()) return;
        try
        {
            await _connection.ConnectAsync(ServerBox.Text, RoomCodeBox.Text.Trim(),
                SecretBox.Text.Trim(), NameBox.Text.Trim());
        }
        catch (Exception ex) { StatusText.Text = $"Join failed: {ex.Message}"; }
    }

    private bool ValidateName()
    {
        if (NameBox.Text.Trim().Length is >= 1 and <= 24) return true;
        StatusText.Text = "Enter a name from 1–24 characters.";
        return false;
    }

    private async void Room_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(button.Tag?.ToString(), out var number)) return;
        await ApplyRoomAction(number);
    }

    private async Task ApplyRoomAction(int number)
    {
        var room = _state.Rooms.FirstOrDefault(r => r.Number == number);
        if (room is null) return;
        switch (_roomMode)
        {
            case RoomActionMode.Safe:
                await Send("set_patient", number, PatientState.Safe.ToString());
                break;
            case RoomActionMode.Anomaly:
                await Send("set_patient", number, PatientState.Anomaly.ToString());
                break;
            case RoomActionMode.Clear:
                await Send("clear_room", number);
                break;
            case RoomActionMode.Event:
                var next = room.Event == EventState.Clear ? EventState.Active : EventState.Clear;
                await Send("set_event", number, next.ToString());
                break;
        }
    }

    private async void Room_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(button.Tag?.ToString(), out var number)) return;
        var room = _state.Rooms.First(r => r.Number == number);
        var next = room.Event == EventState.Clear ? EventState.Active : EventState.Clear;
        await Send("set_event", number, next.ToString());
        e.Handled = true;
    }

    private async void Shift_Click(object sender, RoutedEventArgs e) => await Send("toggle_shift");
    private async void SmallCoffee_Click(object sender, RoutedEventArgs e) => await Send("small_coffee");
    private async void TallCoffee_Click(object sender, RoutedEventArgs e) => await Send("tall_coffee");
    private async void TallToggle_Click(object sender, RoutedEventArgs e) => await Send("toggle_tall");
    private async void ClearRooms_Click(object sender, RoutedEventArgs e) => await Send("clear_rooms");

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } &&
            Enum.TryParse<RoomActionMode>(tag, out var mode))
            SetRoomMode(mode);
    }

    private void SetRoomMode(RoomActionMode mode)
    {
        _roomMode = mode;
        var selected = new SolidColorBrush(Color.FromRgb(49, 105, 145));
        var normal = new SolidColorBrush(Color.FromRgb(38, 48, 62));
        SafeModeButton.Background = mode == RoomActionMode.Safe ? selected : normal;
        AnomalyModeButton.Background = mode == RoomActionMode.Anomaly ? selected : normal;
        ClearModeButton.Background = mode == RoomActionMode.Clear ? selected : normal;
        EventModeButton.Background = mode == RoomActionMode.Event ? selected : normal;
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        switch (e.Key)
        {
            case Key.S: SetRoomMode(RoomActionMode.Safe); e.Handled = true; return;
            case Key.A: SetRoomMode(RoomActionMode.Anomaly); e.Handled = true; return;
            case Key.C: SetRoomMode(RoomActionMode.Clear); e.Handled = true; return;
            case Key.E: SetRoomMode(RoomActionMode.Event); e.Handled = true; return;
        }

        var room = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 1, Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3, Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5, Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7, Key.D8 or Key.NumPad8 => 8,
            _ => 0
        };
        if (room > 0)
        {
            e.Handled = true;
            await ApplyRoomAction(room);
        }
    }

    private void Topmost_Changed(object sender, RoutedEventArgs e) =>
        Topmost = TopmostCheck.IsChecked == true;

    private bool HandleGlobalKey(Key key)
    {
        var testingInHud = IsActive && Keyboard.FocusedElement is not TextBox;
        if (!KeyboardHook.IsRobloxForeground() && !testingInHud)
        {
            if (_commandStage != CommandStage.Idle) Dispatcher.BeginInvoke(CancelCommand);
            return false;
        }

        if (_commandStage == CommandStage.Idle)
        {
            if (key != Key.End) return false;
            Dispatcher.BeginInvoke(() => SetCommandStage(CommandStage.Root));
            return true;
        }

        Dispatcher.BeginInvoke(() => ProcessCommandKey(key));
        return true;
    }

    private async void ProcessCommandKey(Key key)
    {
        RestartCommandTimeout();
        if (key == Key.Escape)
        {
            CancelCommand();
            return;
        }

        if (_commandStage == CommandStage.Root)
        {
            var room = GetNumberKey(key);
            if (room != 0)
            {
                _selectedRoom = room;
                SetCommandStage(CommandStage.RoomAction);
            }
            else if (key == Key.OemMinus) { await Send("small_coffee"); CancelCommand(); }
            else if (key == Key.OemPlus) { await Send("tall_coffee"); CancelCommand(); }
            else if (key == Key.Oem5) { await Send("toggle_shift"); CancelCommand(); }
            else if (key == Key.PageUp) { await Send("toggle_tall"); CancelCommand(); }
            else if (key == Key.Delete) { await Send("clear_rooms"); CancelCommand(); }
            else if (key == Key.Oem6) { await Send("reset_hud"); CancelCommand(); }
            else if (key == Key.Back) { await Send("new_run"); CancelCommand(); }
            else if (key == Key.H) { ToggleHudVisibility(); CancelCommand(); }
            else ShowInvalidKey();
            return;
        }

        if (_commandStage == CommandStage.RoomAction)
        {
            if (key == Key.PageUp)
                await Send("set_patient", _selectedRoom, PatientState.Safe.ToString());
            else if (key == Key.PageDown)
                await Send("set_patient", _selectedRoom, PatientState.Anomaly.ToString());
            else if (key == Key.Delete)
                await Send("set_patient", _selectedRoom, PatientState.Neutral.ToString());
            else if (key == Key.OemPlus)
                await Send("set_event", _selectedRoom, EventState.Active.ToString());
            else if (key == Key.OemMinus)
                await Send("set_event", _selectedRoom, EventState.Clear.ToString());
            else if (key == Key.Back)
                await Send("clear_room", _selectedRoom);
            else
            {
                ShowInvalidKey();
                return;
            }
            CancelCommand();
        }
    }

    private void SetCommandStage(CommandStage stage)
    {
        _commandStage = stage;
        GuidePanel.Visibility = Visibility.Visible;
        RestartCommandTimeout();
        (GuideTitle.Text, GuideText.Text) = stage switch
        {
            CommandStage.Root => ("END · TEAM COMMANDS",
                "1–8 Room  ·  -/= Coffee  ·  \\ Shift  ·  PgUp Tall lock  ·  Del Clear rooms  ·  ] Reset HUD  ·  Backspace New run  ·  H Hide"),
            CommandStage.RoomAction => ($"ROOM {_selectedRoom}",
                "PgUp Safe  ·  PgDown Anomaly  ·  Del Clear patient  ·  = Event  ·  - Clear event  ·  Backspace Clear room"),
            _ => ("", "")
        };
        GuideTitle.Foreground = new SolidColorBrush(Color.FromRgb(243, 201, 105));
    }

    private void RestartCommandTimeout()
    {
        _commandTimer.Stop();
        _commandTimer.Start();
    }

    private void CancelCommand()
    {
        _commandTimer.Stop();
        _commandStage = CommandStage.Idle;
        GuidePanel.Visibility = Visibility.Collapsed;
        GuideTitle.Foreground = new SolidColorBrush(Color.FromRgb(243, 201, 105));
    }

    private void ShowInvalidKey()
    {
        GuideTitle.Text = "KEY NOT ASSIGNED";
        GuideTitle.Foreground = new SolidColorBrush(Color.FromRgb(242, 121, 121));
    }

    private void ToggleHudVisibility()
    {
        _hudHidden = !_hudHidden;
        if (_hudHidden) Hide();
        else
        {
            Show();
            if (Topmost) Activate();
        }
    }

    private static int GetNumberKey(Key key) => key switch
    {
        >= Key.D1 and <= Key.D8 => key - Key.D0,
        >= Key.NumPad1 and <= Key.NumPad8 => key - Key.NumPad0,
        _ => 0
    };

    private Task Send(string type, int? room = null, string? value = null) =>
        _connection.SendAsync(new ClientAction { Type = type, Room = room, Value = value });

    private void RefreshState()
    {
        foreach (var room in _state.Rooms)
        {
            var button = _roomButtons[room.Number];
            button.Content = $"{room.Number}\n{room.Patient.ToString().ToUpperInvariant()}" +
                             (room.Event == EventState.Active ? " !" : "");
            button.Background = room.Patient switch
            {
                PatientState.Safe => new SolidColorBrush(Color.FromRgb(31, 112, 73)),
                PatientState.Anomaly => new SolidColorBrush(Color.FromRgb(150, 47, 51)),
                _ => new SolidColorBrush(Color.FromRgb(38, 48, 62))
            };
            button.BorderBrush = room.Event == EventState.Active
                ? new SolidColorBrush(Color.FromRgb(243, 201, 105))
                : new SolidColorBrush(Color.FromRgb(86, 97, 115));
            button.BorderThickness = room.Event == EventState.Active ? new Thickness(3) : new Thickness(1);
            button.ToolTip = room.UpdatedBy is null ? null : $"Last changed by {room.UpdatedBy}";
        }
        ShiftLabel.Text = $"SHIFT {_state.ShiftNumber}";
        MembersText.Text = _state.Members.Count == 0 ? "No teammates" : string.Join("  •  ", _state.Members);
        LastActionText.Text = _state.LastChangedBy is null
            ? "No shared changes yet."
            : $"Last: {_state.LastAction} by {_state.LastChangedBy}";
        RevisionText.Text = $"Revision {_state.Revision}";
        ShiftButton.Content = _state.ShiftRunning ? "FINISH SHIFT" : "START SHIFT";
        RefreshTimers();
    }

    private void RefreshTimers()
    {
        ShiftTimeText.Text = _state.ShiftRunning && _state.ShiftStartedAtUtc is not null
            ? Format(DateTime.UtcNow - _state.ShiftStartedAtUtc.Value)
            : "00:00";
        SmallCoffeeText.Text = Countdown(_state.SmallCoffeeReadyAtUtc);
        SmallCoffeeText.Foreground = BrushForTimer(_state.SmallCoffeeReadyAtUtc);
        TallCoffeeText.Text = !_state.TallCoffeeUnlocked ? "LOCKED" : Countdown(_state.TallCoffeeReadyAtUtc);
        TallCoffeeText.Foreground = !_state.TallCoffeeUnlocked
            ? new SolidColorBrush(Color.FromRgb(120, 134, 154))
            : BrushForTimer(_state.TallCoffeeReadyAtUtc);
    }

    private static string Countdown(DateTime? readyAt)
    {
        if (readyAt is null || readyAt <= DateTime.UtcNow) return "READY";
        return Format(readyAt.Value - DateTime.UtcNow);
    }

    private static Brush BrushForTimer(DateTime? readyAt) =>
        readyAt is null || readyAt <= DateTime.UtcNow
            ? new SolidColorBrush(Color.FromRgb(100, 214, 154))
            : new SolidColorBrush(Color.FromRgb(243, 201, 105));

    private static string Format(TimeSpan time) =>
        time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{Math.Max(0, time.Minutes):00}:{Math.Max(0, time.Seconds):00}";

    private enum RoomActionMode { Safe, Anomaly, Clear, Event }
}
