using TrackPeek;
using System.Net.WebSockets;
using System.Text.Json;
using System.Diagnostics;

var metrics = new MusicMetrics();
var frame = new double[MusicMetrics.BandCount];
var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
MusicMetricsSnapshot snapshot = MusicMetricsSnapshot.Silent;

for (var i = 0; i < 400; i++)
{
    Array.Clear(frame);
    if (i % 25 == 0)
        for (var band = 0; band < 5; band++) frame[band] = 0.9;

    snapshot = metrics.Process(frame, timestamp.AddMilliseconds(i * 20));
}

if (snapshot.BeatId < 10) throw new InvalidOperationException($"Expected repeated beats, got {snapshot.BeatId}.");
if (snapshot.Bpm is null || Math.Abs(snapshot.Bpm.Value - 120) > 1)
    throw new InvalidOperationException($"Expected 120 BPM, got {snapshot.Bpm?.ToString("F1") ?? "unknown"}.");
if (snapshot.BpmConfidence < 0.8) throw new InvalidOperationException($"Tempo confidence was too low: {snapshot.BpmConfidence:F2}.");

await using var api = new AvatarApiServer();
using var trace = new StringWriter();
Trace.Listeners.Add(new TextWriterTraceListener(trace));
if (!api.Start()) throw new InvalidOperationException($"The local avatar API did not start: {trace}");
api.Publish(new AvatarApiState(snapshot, new AvatarApiMediaState("playing", "Synthetic", "Test", "self-test", 2000, 4000)));

using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
using (var health = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:47831/v1/health")))
    if (health.RootElement.GetProperty("status").GetString() != "ok")
        throw new InvalidOperationException("The health endpoint did not report ok.");

using var socket = new ClientWebSocket();
socket.Options.Proxy = null;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
await socket.ConnectAsync(new Uri(AvatarApiServer.WebSocketUrl), timeout.Token);
using var hello = JsonDocument.Parse(await ReadMessageAsync(socket, timeout.Token));
if (hello.RootElement.GetProperty("type").GetString() != "hello")
    throw new InvalidOperationException("The WebSocket greeting was invalid.");

using var state = JsonDocument.Parse(await ReadMessageAsync(socket, timeout.Token));
var audio = state.RootElement.GetProperty("audio");
if (audio.GetProperty("bands").GetArrayLength() != MusicMetrics.BandCount)
    throw new InvalidOperationException("The API returned the wrong number of bands.");
if (Math.Abs(audio.GetProperty("bpm").GetDouble() - 120) > 1)
    throw new InvalidOperationException("The API did not publish the detected tempo.");
if (state.RootElement.GetProperty("media").GetProperty("title").GetString() != "Synthetic")
    throw new InvalidOperationException("The API did not publish media metadata.");
await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", timeout.Token);

Console.WriteLine($"PASS: bpm={snapshot.Bpm:F1}, confidence={snapshot.BpmConfidence:F2}, websocket and health endpoints");

static async Task<byte[]> ReadMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
{
    using var output = new MemoryStream();
    var buffer = new byte[4096];
    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
            throw new InvalidOperationException("The server closed the WebSocket unexpectedly.");
        output.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);
    return output.ToArray();
}
