using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrackPeek;

internal sealed record AvatarApiMediaState(
    string PlaybackState,
    string? Title,
    string? Artist,
    string? SourceAppId,
    long? PositionMs,
    long? DurationMs);

internal sealed record AvatarApiState(MusicMetricsSnapshot Audio, AvatarApiMediaState? Media);

internal sealed class AvatarApiServer : IAsyncDisposable
{
    public const int Port = 47831;
    public const string WebSocketUrl = "ws://127.0.0.1:47831/v1/ws";

    private const int MaxClients = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TcpListener _listener = new(IPAddress.Loopback, Port);
    private readonly ConcurrentDictionary<long, WebSocket> _clients = new();
    private readonly SemaphoreSlim _clientSlots = new(MaxClients, MaxClients);
    private AvatarApiState? _latest;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptLoop;
    private Task? _broadcastLoop;
    private long _nextClientId;
    private long _sequence;

    public bool Start()
    {
        try
        {
            _listener.Start();
            _cancellation = new CancellationTokenSource();
            _acceptLoop = AcceptLoopAsync(_cancellation.Token);
            _broadcastLoop = BroadcastLoopAsync(_cancellation.Token);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not start TrackPeek avatar API on port {Port}: {ex}");
            try { _listener.Stop(); } catch { }
            return false;
        }
    }

    public void Publish(AvatarApiState state) => Volatile.Write(ref _latest, state);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleRequestAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex) { Trace.WriteLine($"TrackPeek avatar API accept error: {ex}"); }
        }
    }

    private async Task HandleRequestAsync(TcpClient client, CancellationToken cancellationToken)
    {
        WebSocket? socket = null;
        long clientId = 0;
        var stream = client.GetStream();
        try
        {
            var request = await ReadRequestAsync(stream, cancellationToken);
            if (request.Method == "GET" && request.Path == "/v1/health")
            {
                await WriteJsonAsync(stream, new
                {
                    status = "ok",
                    protocolVersion = 1,
                    webSocketUrl = WebSocketUrl,
                    readOnly = true
                });
                return;
            }

            if (request.Path != "/v1/ws" || request.Headers.GetValueOrDefault("Upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) != true)
            {
                await WriteResponseAsync(stream, HttpStatusCode.NotFound, "Not Found");
                return;
            }

            if (request.Headers.ContainsKey("Origin"))
            {
                await WriteResponseAsync(stream, HttpStatusCode.Forbidden, "Forbidden");
                return;
            }

            var websocketKey = request.Headers.GetValueOrDefault("Sec-WebSocket-Key");
            if (websocketKey is null || request.Headers.GetValueOrDefault("Connection")?.Contains("Upgrade", StringComparison.OrdinalIgnoreCase) != true)
            {
                await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "Bad Request");
                return;
            }

            if (!_clientSlots.Wait(0))
            {
                await WriteResponseAsync(stream, HttpStatusCode.ServiceUnavailable, "Too Many Clients");
                return;
            }
            clientId = -1;

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(websocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var handshake = Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");
            await stream.WriteAsync(handshake, cancellationToken);
            socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
            clientId = Interlocked.Increment(ref _nextClientId);
            await SendJsonAsync(socket, new
            {
                type = "hello",
                protocolVersion = 1,
                sampleRateHz = 10,
                readOnly = true
            }, cancellationToken);
            _clients.TryAdd(clientId, socket);
            await ReceiveLoopAsync(socket, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Trace.WriteLine($"TrackPeek avatar API client error: {ex.Message}"); }
        finally
        {
            if (clientId > 0)
            {
                _clients.TryRemove(clientId, out _);
                _clientSlots.Release();
            }
            else if (clientId == -1)
            {
                _clientSlots.Release();
            }
            if (socket is not null)
            {
                try { socket.Abort(); } catch { }
                socket.Dispose();
            }
            client.Dispose();
        }
    }

    private static async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return;
            socket.Abort();
            return;
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var state = Volatile.Read(ref _latest);
                if (state is null || _clients.IsEmpty) continue;

                var message = new
                {
                    type = "state",
                    protocolVersion = 1,
                    sequence = Interlocked.Increment(ref _sequence),
                    timestampUtc = state.Audio.TimestampUtc,
                    audio = new
                    {
                        active = state.Audio.Active,
                        energy = state.Audio.Energy,
                        bass = state.Audio.Bass,
                        mid = state.Audio.Mid,
                        treble = state.Audio.Treble,
                        bands = state.Audio.Bands,
                        bpm = state.Audio.BpmConfidence >= 0.25 ? state.Audio.Bpm : null,
                        bpmConfidence = state.Audio.BpmConfidence,
                        beatId = state.Audio.BeatId
                    },
                    media = state.Media
                };
                var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
                await Task.WhenAll(_clients.Select(client => SendToClientAsync(client.Key, client.Value, payload, cancellationToken)));
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendToClientAsync(long clientId, WebSocket socket, byte[] payload, CancellationToken cancellationToken)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"TrackPeek avatar API send failed: {ex.Message}");
            _clients.TryRemove(clientId, out _);
            try { socket.Abort(); } catch { }
        }
    }

    private static async Task SendJsonAsync(WebSocket socket, object value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task WriteJsonAsync(Stream stream, object value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
    }

    private static async Task WriteResponseAsync(Stream stream, HttpStatusCode status, string body)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)status} {status}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
    }

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers)> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 8192)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0) throw new IOException("Client disconnected before sending an HTTP request.");
            buffer.WriteByte(one[0]);
            var bytes = buffer.GetBuffer();
            var length = (int)buffer.Length;
            if (length >= 4 && bytes[length - 4] == '\r' && bytes[length - 3] == '\n' && bytes[length - 2] == '\r' && bytes[length - 1] == '\n') break;
        }
        if (buffer.Length >= 8192) throw new InvalidDataException("HTTP request headers exceed 8 KiB.");
        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ', 3);
        if (requestLine.Length < 2) throw new InvalidDataException("Malformed HTTP request line.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0) headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return (requestLine[0], requestLine[1], headers);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is null) return;
        _cancellation.Cancel();
        try { _listener.Stop(); } catch { }
        foreach (var socket in _clients.Values)
        {
            try { socket.Abort(); } catch { }
        }

        var loops = new[] { _acceptLoop, _broadcastLoop }.Where(task => task is not null).Cast<Task>();
        try { await Task.WhenAll(loops); } catch { }
        _cancellation.Dispose();
        _cancellation = null;
    }
}
