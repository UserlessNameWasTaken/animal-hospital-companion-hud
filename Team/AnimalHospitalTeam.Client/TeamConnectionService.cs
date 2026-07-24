using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AnimalHospitalTeam.Shared;

namespace AnimalHospitalTeam.Client;

public sealed class TeamConnectionService : IDisposable
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentQueue<ClientAction> _pendingActions = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private CancellationTokenSource? _sessionCancellation;
    private ClientWebSocket? _socket;
    private ConnectionDetails? _details;
    private Task? _sessionTask;
    private bool _hasConnected;
    private bool _disposed;

    public event Action<TeamState>? StateReceived;
    public event Action<string>? StatusChanged;
    public event Action<bool>? ConnectionChanged;
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task<string> TestRelayAsync(string server)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var baseUri = NormalizeHttpBaseUri(server);
        using var response = await http.GetAsync(new Uri(baseUri, "health"));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Relay health check returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("status", out var status) &&
                status.GetString() == "ok")
                return "Relay responded successfully.";
        }
        catch (JsonException) { }

        throw new HttpRequestException(
            "The address responded, but not with the relay health response. " +
            "Check the tunnel service URL and any Cloudflare Access login policy.");
    }

    public async Task<CreateRoomResponse> CreateRoomAsync(string server)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var baseUri = NormalizeHttpBaseUri(server);
        using var response = await http.PostAsync(new Uri(baseUri, "api/rooms"), null);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Create room returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        return await response.Content.ReadFromJsonAsync<CreateRoomResponse>(_json)
               ?? throw new InvalidOperationException("Relay returned no room.");
    }

    public async Task ConnectAsync(string server, string room, string secret, string name)
    {
        Disconnect();
        _details = new ConnectionDetails(NormalizeHttpBaseUri(server), room, secret, name);
        _sessionCancellation = new CancellationTokenSource();
        _hasConnected = false;

        var firstConnection = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionTask = RunSessionAsync(_sessionCancellation.Token, firstConnection);
        await firstConnection.Task;
    }

    public Task SendAsync(ClientAction action)
    {
        if (!_hasConnected)
        {
            StatusChanged?.Invoke("Join a team before sending changes.");
            return Task.CompletedTask;
        }

        _pendingActions.Enqueue(action);
        return FlushPendingAsync(_sessionCancellation?.Token ?? CancellationToken.None);
    }

    private async Task RunSessionAsync(
        CancellationToken cancellation,
        TaskCompletionSource firstConnection)
    {
        var attempt = 0;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var details = _details ?? throw new InvalidOperationException("Missing team details.");
                var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                socket.Options.SetRequestHeader("Authorization", $"Bearer {details.Secret}");
                _socket = socket;

                StatusChanged?.Invoke(attempt == 0 ? "Connecting…" : $"Reconnecting… attempt {attempt}");
                await socket.ConnectAsync(BuildUri(details), cancellation);
                attempt = 0;
                _hasConnected = true;
                ConnectionChanged?.Invoke(true);
                StatusChanged?.Invoke("Connected");
                firstConnection.TrySetResult();

                await FlushPendingAsync(cancellation);
                await ReceiveLoopAsync(socket, cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                firstConnection.TrySetCanceled(cancellation);
                break;
            }
            catch (Exception ex)
            {
                if (!_hasConnected)
                {
                    firstConnection.TrySetException(ex);
                    break;
                }
                StatusChanged?.Invoke($"Connection lost: {FriendlyMessage(ex)}");
            }
            finally
            {
                ConnectionChanged?.Invoke(false);
                var oldSocket = _socket;
                _socket = null;
                oldSocket?.Dispose();
            }

            if (cancellation.IsCancellationRequested) break;
            attempt++;
            var delay = Math.Min(15, (int)Math.Pow(2, Math.Min(attempt - 1, 4)));
            StatusChanged?.Invoke($"Offline — retrying in {delay}s");
            try { await Task.Delay(TimeSpan.FromSeconds(delay), cancellation); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellation);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException($"Relay closed the connection: {result.CloseStatusDescription ?? "no reason"}");

            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var envelope = JsonSerializer.Deserialize<ServerEnvelope>(message.ToArray(), _json);
            message.SetLength(0);
            if (envelope?.State is not null) StateReceived?.Invoke(envelope.State);
            if (!string.IsNullOrWhiteSpace(envelope?.Message)) StatusChanged?.Invoke(envelope.Message);
        }
    }

    private async Task FlushPendingAsync(CancellationToken cancellation)
    {
        await _sendGate.WaitAsync(cancellation);
        try
        {
            while (_pendingActions.TryPeek(out var action))
            {
                var socket = _socket;
                if (socket?.State != WebSocketState.Open) return;
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(action, _json));
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellation);
                _pendingActions.TryDequeue(out _);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally { _sendGate.Release(); }
    }

    private static Uri BuildUri(ConnectionDetails details)
    {
        var builder = new UriBuilder(details.Server)
        {
            Scheme = details.Server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = details.Server.IsDefaultPort ? -1 : details.Server.Port,
            Path = "ws",
            Query = $"room={Uri.EscapeDataString(details.Room)}" +
                    $"&name={Uri.EscapeDataString(details.Name)}"
        };
        return builder.Uri;
    }

    private static Uri NormalizeHttpBaseUri(string server)
    {
        var value = server.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Enter a relay address.");

        if (!value.Contains("://", StringComparison.Ordinal))
            value = $"https://{value}";
        else if (value.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            value = $"https://{value[6..]}";
        else if (value.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            value = $"http://{value[5..]}";

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException(
                "Use a relay address such as https://relay.example.com.");

        if (uri.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(uri.Query))
            throw new ArgumentException(
                "Enter only the relay base address; remove /health, /api/rooms, /ws, and query text.");

        return new Uri($"{uri.Scheme}://{uri.Authority}/");
    }

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        WebSocketException socket => $"WebSocket failed ({socket.WebSocketErrorCode}): {socket.Message}",
        _ => ex.Message
    };

    public void Disconnect()
    {
        _hasConnected = false;
        _details = null;
        _sessionCancellation?.Cancel();
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
        _socket?.Dispose();
        _socket = null;
        while (_pendingActions.TryDequeue(out _)) { }
        ConnectionChanged?.Invoke(false);
        StatusChanged?.Invoke("Disconnected");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _sendGate.Dispose();
    }

    private sealed record ConnectionDetails(Uri Server, string Room, string Secret, string Name);
}
