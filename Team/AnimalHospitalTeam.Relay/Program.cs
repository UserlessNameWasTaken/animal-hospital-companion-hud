using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using AnimalHospitalTeam.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5188");
var maxRooms = Math.Max(1, builder.Configuration.GetValue("Relay:MaxRooms", 100));
var roomLifetime = TimeSpan.FromSeconds(
    Math.Max(1, builder.Configuration.GetValue("Relay:RoomLifetimeSeconds", 21_600)));
var createRoomTokenLimit = Math.Max(
    1, builder.Configuration.GetValue("Relay:CreateRoomTokenLimit", 3));
var createRoomTokensPerPeriod = Math.Max(
    1, builder.Configuration.GetValue("Relay:CreateRoomTokensPerPeriod", 1));
var createRoomReplenishment = TimeSpan.FromSeconds(Math.Max(
    1, builder.Configuration.GetValue("Relay:CreateRoomReplenishmentSeconds", 60)));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("create-room", context =>
    {
        var clientIp = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                       ?? context.Connection.RemoteIpAddress?.ToString()
                       ?? "unknown";
        return RateLimitPartition.GetTokenBucketLimiter(clientIp, _ =>
            new TokenBucketRateLimiterOptions
            {
                TokenLimit = createRoomTokenLimit,
                TokensPerPeriod = createRoomTokensPerPeriod,
                ReplenishmentPeriod = createRoomReplenishment,
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

var app = builder.Build();
var rooms = new ConcurrentDictionary<string, TeamRoom>(StringComparer.OrdinalIgnoreCase);
var roomGate = new object();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/health", () =>
{
    CleanupExpiredRooms();
    return Results.Ok(new { status = "ok", rooms = rooms.Count, capacity = maxRooms });
});

app.MapPost("/api/rooms", () =>
{
    lock (roomGate)
    {
        CleanupExpiredRooms();
        if (rooms.Count >= maxRooms)
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Relay room capacity reached. Try again later.");

        string code;
        do code = RandomCode(6); while (rooms.ContainsKey(code));
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        rooms[code] = new TeamRoom(code, secret);
        return Results.Ok(new CreateRoomResponse { Code = code, Secret = secret });
    }
}).RequireRateLimiting("create-room");

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var code = context.Request.Query["room"].ToString().Trim().ToUpperInvariant();
    var requestedName = context.Request.Query["name"].ToString().Trim();
    var secret = ReadBearerSecret(context);
    if (!rooms.TryGetValue(code, out var room) || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(room.Secret), Encoding.UTF8.GetBytes(secret ?? "")))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    if (requestedName.Length is < 1 or > 24)
    {
        context.Response.StatusCode = 400;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connection = room.TryJoin(requestedName, socket, out var replaced);
    if (connection is null)
    {
        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation,
            "Team full", CancellationToken.None);
        return;
    }
    if (replaced is not null)
    {
        replaced.Abort();
    }

    await room.BroadcastAsync(json);
    var buffer = new byte[8192];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (!result.EndOfMessage || result.MessageType != WebSocketMessageType.Text) continue;
            var action = JsonSerializer.Deserialize<ClientAction>(
                Encoding.UTF8.GetString(buffer, 0, result.Count), json);
            if (action is not null && room.Apply(connection.Name, action))
                await room.BroadcastAsync(json);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
    finally
    {
        room.Leave(connection.Id);
        await room.BroadcastAsync(json);
    }
});

app.Run();

void CleanupExpiredRooms()
{
    var expiration = DateTime.UtcNow - roomLifetime;
    foreach (var entry in rooms)
    {
        if (entry.Value.ConnectionCount == 0 &&
            entry.Value.LastActivityUtc < expiration)
            rooms.TryRemove(entry.Key, out _);
    }
}

static string? ReadBearerSecret(HttpContext context)
{
    const string prefix = "Bearer ";
    var authorization = context.Request.Headers.Authorization.ToString();
    if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        return null;
    var secret = authorization[prefix.Length..].Trim();
    return secret.Length == 0 ? null : secret;
}

static string RandomCode(int length)
{
    const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    var bytes = RandomNumberGenerator.GetBytes(length);
    return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
}

sealed class TeamConnection(Guid id, string name, WebSocket socket)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public WebSocket Socket { get; } = socket;
    public SemaphoreSlim SendGate { get; } = new(1, 1);
}

sealed class TeamRoom(string code, string secret)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TeamConnection> _connections = [];
    private readonly HashSet<string> _actionIds = [];
    private readonly TeamState _state = new();
    public string Code { get; } = code;
    public string Secret { get; } = secret;
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;
    public int ConnectionCount
    {
        get { lock (_gate) return _connections.Count; }
    }

    public TeamConnection? TryJoin(string name, WebSocket socket, out WebSocket? replaced)
    {
        lock (_gate)
        {
            var stale = _connections.Values.FirstOrDefault(
                c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            replaced = stale?.Socket;
            if (stale is not null) _connections.Remove(stale.Id);
            if (_connections.Count >= 4)
                return null;
            var connection = new TeamConnection(Guid.NewGuid(), name, socket);
            _connections[connection.Id] = connection;
            LastActivityUtc = DateTime.UtcNow;
            UpdateMembers();
            return connection;
        }
    }

    public void Leave(Guid id)
    {
        lock (_gate)
        {
            _connections.Remove(id);
            LastActivityUtc = DateTime.UtcNow;
            UpdateMembers();
        }
    }

    public bool Apply(string actor, ClientAction action)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(action.ActionId) || !_actionIds.Add(action.ActionId))
                return false;
            if (_actionIds.Count > 5000) _actionIds.Clear();

            var changed = action.Type switch
            {
                "set_patient" => SetPatient(actor, action),
                "set_event" => SetEvent(actor, action),
                "clear_room" => ClearRoom(actor, action),
                "clear_rooms" => ClearRooms(actor),
                "reset_hud" => ResetHud(actor, false),
                "new_run" => ResetHud(actor, true),
                "toggle_shift" => ToggleShift(actor),
                "small_coffee" => SetCoffee(actor, false),
                "tall_coffee" => SetCoffee(actor, true),
                "toggle_tall" => ToggleTall(actor),
                _ => false
            };
            if (changed)
            {
                LastActivityUtc = DateTime.UtcNow;
                _state.Revision++;
                _state.LastChangedBy = actor;
                _state.LastAction = action.Type;
            }
            return changed;
        }
    }

    private bool SetPatient(string actor, ClientAction action)
    {
        var room = FindRoom(action.Room);
        if (room is null || !Enum.TryParse<PatientState>(action.Value, true, out var value)) return false;
        room.Patient = value; room.UpdatedBy = actor; return true;
    }

    private bool SetEvent(string actor, ClientAction action)
    {
        var room = FindRoom(action.Room);
        if (room is null || !Enum.TryParse<EventState>(action.Value, true, out var value)) return false;
        room.Event = value; room.UpdatedBy = actor; return true;
    }

    private bool ClearRoom(string actor, ClientAction action)
    {
        var room = FindRoom(action.Room);
        if (room is null) return false;
        room.Patient = PatientState.Neutral;
        room.Event = EventState.Clear;
        room.UpdatedBy = actor;
        return true;
    }

    private bool ClearRooms(string actor)
    {
        foreach (var room in _state.Rooms)
        {
            room.Patient = PatientState.Neutral;
            room.Event = EventState.Clear;
            room.UpdatedBy = actor;
        }
        return true;
    }

    private bool ResetHud(string actor, bool newRun)
    {
        _state.ShiftRunning = false;
        _state.ShiftStartedAtUtc = null;
        _state.SmallCoffeeReadyAtUtc = null;
        _state.TallCoffeeReadyAtUtc = null;
        if (newRun) _state.ShiftNumber = 1;
        ClearRooms(actor);
        return true;
    }

    private bool ToggleShift(string actor)
    {
        if (_state.ShiftRunning)
        {
            _state.ShiftRunning = false;
            _state.ShiftStartedAtUtc = null;
            _state.ShiftNumber++;
            ClearRooms(actor);
        }
        else
        {
            _state.ShiftRunning = true;
            _state.ShiftStartedAtUtc = DateTime.UtcNow;
        }
        return true;
    }

    private bool SetCoffee(string actor, bool tall)
    {
        if (tall && !_state.TallCoffeeUnlocked) return false;
        var ready = DateTime.UtcNow.AddSeconds(tall ? 300 : 180);
        if (tall) _state.TallCoffeeReadyAtUtc = ready;
        else _state.SmallCoffeeReadyAtUtc = ready;
        return true;
    }

    private bool ToggleTall(string actor)
    {
        _state.TallCoffeeUnlocked = !_state.TallCoffeeUnlocked;
        if (!_state.TallCoffeeUnlocked) _state.TallCoffeeReadyAtUtc = null;
        return true;
    }

    private SharedRoomState? FindRoom(int? number) =>
        _state.Rooms.FirstOrDefault(r => r.Number == number);

    private void UpdateMembers() =>
        _state.Members = _connections.Values.Select(c => c.Name).OrderBy(n => n).ToList();

    public async Task BroadcastAsync(JsonSerializerOptions options)
    {
        byte[] payload;
        TeamConnection[] targets;
        lock (_gate)
        {
            payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new ServerEnvelope { Type = "state", State = _state }, options));
            targets = _connections.Values.ToArray();
        }
        foreach (var target in targets)
        {
            if (target.Socket.State != WebSocketState.Open) continue;
            await target.SendGate.WaitAsync();
            try { await target.Socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None); }
            catch (WebSocketException) { }
            finally { target.SendGate.Release(); }
        }
    }
}
