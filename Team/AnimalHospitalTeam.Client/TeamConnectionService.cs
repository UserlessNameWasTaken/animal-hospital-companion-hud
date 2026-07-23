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

    public async Task<CreateRoomResponse> CreateRoomAsync(string server)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.PostAsync($"{server.TrimEnd('/')}/api/rooms", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CreateRoomResponse>(_json)
               ?? throw new InvalidOperationException("Relay returned no room.");
    }

    public async Task ConnectAsync(string server, string room, string secret, string name)
    {
        Disconnect();
        _details = new ConnectionDetails(server.TrimEnd('/'), room, secret, name);
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
        var wsBase = details.Server
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase);
        return new Uri($"{wsBase}/ws?room={Uri.EscapeDataString(details.Room)}" +
                       $"&secret={Uri.EscapeDataString(details.Secret)}" +
                       $"&name={Uri.EscapeDataString(details.Name)}");
    }

    private static string FriendlyMessage(Exception ex) =>
        ex is WebSocketException ? "relay unavailable" : ex.Message;

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

    private sealed record ConnectionDetails(string Server, string Room, string Secret, string Name);
}
