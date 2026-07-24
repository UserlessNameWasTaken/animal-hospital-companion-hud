using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var server = new Uri((args.FirstOrDefault(a => !a.StartsWith("--")) ??
                      "https://relay.ahospitalhud.com").TrimEnd('/') + "/");
var hardening = args.Contains("--hardening", StringComparer.OrdinalIgnoreCase);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
using var http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromSeconds(10) };

var health = await http.GetFromJsonAsync<HealthResponse>("health", json)
             ?? throw new InvalidOperationException("Relay returned no health response.");
Require(health.Status == "ok", "Health response did not come from Animal Hospital Relay.");

var created = await CreateRoom();
await ExpectRejectedSecret(created);

using var host = await Connect(created, "TunnelTestHost");
using var teammate = await Connect(created, "TunnelTestTeammate");
var hostUpdate = ReceiveUntil(host, IsSynchronizedAnomaly);
var teammateUpdate = ReceiveUntil(teammate, IsSynchronizedAnomaly);

await host.SendAsync(
    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        actionId = Guid.NewGuid().ToString("N"),
        type = "set_patient",
        room = 4,
        value = "Anomaly"
    }, json)),
    WebSocketMessageType.Text,
    true,
    CancellationToken.None);

var states = await Task.WhenAll(hostUpdate, teammateUpdate);
Require(states.All(state => state.LastChangedBy == "TunnelTestHost"),
    "Relay reported the wrong actor.");

host.Abort();
teammate.Abort();

if (hardening)
{
    await Task.Delay(150);
    _ = await CreateRoom(); // Fill the configured two-room capacity.

    using var capacityResponse = await http.PostAsync("api/rooms", null);
    Require(capacityResponse.StatusCode == HttpStatusCode.ServiceUnavailable,
        $"Expected room-capacity HTTP 503, received {(int)capacityResponse.StatusCode}.");

    await Task.Delay(TimeSpan.FromSeconds(2.2));
    _ = await CreateRoom(); // Expired inactive rooms must have been reclaimed.

    using var limitedResponse = await http.PostAsync("api/rooms", null);
    Require((int)limitedResponse.StatusCode == StatusCodes.TooManyRequests,
        $"Expected rate-limit HTTP 429, received {(int)limitedResponse.StatusCode}.");
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    ok = true,
    server = server.ToString().TrimEnd('/'),
    health = "Passed",
    roomCreation = "Passed",
    bearerAuthentication = "Passed",
    secureWebSocket = "Passed",
    twoClientSync = "Passed",
    capacity = hardening ? "Passed" : "Not requested",
    expiration = hardening ? "Passed" : "Not requested",
    rateLimiting = hardening ? "Passed" : "Not requested",
    revision = states[0].Revision
}, new JsonSerializerOptions { WriteIndented = true }));

async Task<RoomResponse> CreateRoom()
{
    using var response = await http.PostAsync("api/rooms", null);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadFromJsonAsync<RoomResponse>(json)
           ?? throw new InvalidOperationException("Relay returned no room.");
}

async Task<ClientWebSocket> Connect(RoomResponse room, string name, string? secret = null)
{
    var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Authorization", $"Bearer {secret ?? room.Secret}");
    var uri = new UriBuilder(server)
    {
        Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        Port = server.IsDefaultPort ? -1 : server.Port,
        Path = "ws",
        Query = $"room={Uri.EscapeDataString(room.Code)}&name={Uri.EscapeDataString(name)}"
    }.Uri;
    await socket.ConnectAsync(uri, CancellationToken.None);
    return socket;
}

async Task ExpectRejectedSecret(RoomResponse room)
{
    using var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Authorization", "Bearer incorrect-secret");
    var uri = new UriBuilder(server)
    {
        Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        Port = server.IsDefaultPort ? -1 : server.Port,
        Path = "ws",
        Query = $"room={Uri.EscapeDataString(room.Code)}&name=RejectedClient"
    }.Uri;
    try
    {
        await socket.ConnectAsync(uri, CancellationToken.None);
        throw new InvalidOperationException("Relay accepted an incorrect bearer secret.");
    }
    catch (WebSocketException)
    {
        // Expected: the relay rejects the upgrade with HTTP 401.
    }
}

async Task<State> ReceiveUntil(ClientWebSocket socket, Func<State, bool> predicate)
{
    var buffer = new byte[8192];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (true)
    {
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Relay closed the test connection.");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        var envelope = JsonSerializer.Deserialize<Envelope>(message.ToArray(), json);
        if (envelope?.State is not null && predicate(envelope.State))
            return envelope.State;
    }
}

static bool IsSynchronizedAnomaly(State state) =>
    state.Members.Count == 2 &&
    state.Rooms.FirstOrDefault(room => room.Number == 4)?.Patient == 2;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static class StatusCodes
{
    public const int TooManyRequests = 429;
}

sealed record HealthResponse(string Status, int Rooms, int Capacity);
sealed record RoomResponse(string Code, string Secret);
sealed record Envelope(State? State);
sealed record State(List<string> Members, List<Room> Rooms, string? LastChangedBy, long Revision);
sealed record Room(int Number, int Patient, int Event);
