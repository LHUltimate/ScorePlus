using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Scoreboard.Desktop;

public sealed record ChannelHealth(string Id, string Name, string Layout, string Status, string Url, string AudioUrl = "");

/// <summary>
/// A small local HTTP/MJPEG headend. Every display gets a real stream URL that VLC, a browser or a
/// TV can open; the same server exposes JSON health for the Live Channel page. Nothing leaves the
/// machine unless the operator points a player at the LAN address.
/// </summary>
public sealed class ChannelStreamServer : IDisposable
{
    readonly ConcurrentDictionary<string, byte[]> _frames = new(StringComparer.OrdinalIgnoreCase);
    // Viewers are counted per stream id, not per connection, so the Vision and Live Channel health
    // rows can say whether anyone is actually watching a destination.
    readonly ConcurrentDictionary<string, int> _viewers = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource _stop = new();
    TcpListener? _listener;
    Task? _accept;
    SystemAudioTap? _audio;

    public int Port { get; private set; }
    public bool IsRunning => _listener is not null;
    public string? Error { get; private set; }
    public Func<IReadOnlyList<ChannelHealth>>? HealthProvider { get; set; }
    public Func<string, byte[]?>? FrameProvider { get; set; }
    /// <summary>Supplies one still PNG for the fill or key plane. The bool is true for key.</summary>
    public Func<string, bool, byte[]?>? PngProvider { get; set; }
    public string BaseUrl => $"http://{LocalAddress()}:{Port}";

    public int ViewerCount(string id) => _viewers.TryGetValue(id, out var count) ? Math.Max(0, count) : 0;
    public bool HasFrame(string id) => _frames.ContainsKey(id);
    public void RemoveFrame(string id) => _frames.TryRemove(id, out _);

    public bool Start(int port)
    {
        if (IsRunning) return true;
        try
        {
            if (_stop.IsCancellationRequested) _stop = new CancellationTokenSource();
            Port = Math.Clamp(port, 1024, 65535);
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            Error = null;
            _accept = Task.Run(AcceptLoopAsync);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            Error = $"Stream port {port} could not open: {ex.Message}";
            _listener = null;
            return false;
        }
    }

    public void Stop()
    {
        try { _stop.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _frames.Clear();
        _viewers.Clear();
        _listener = null;
        _accept = null;
    }

    public void UpdateFrame(string displayId, byte[] jpeg)
    {
        if (jpeg.Length > 0) _frames[displayId] = jpeg;
    }

    public string DisplayUrl(string displayId) => $"{BaseUrl}/display/{Uri.EscapeDataString(displayId)}.mjpeg";
    /// <summary>The programme audio for one display or channel, as a continuous WAV stream.</summary>
    public string AudioUrl(string id) => $"{BaseUrl}/audio/{Uri.EscapeDataString(id)}.wav";

    async Task AcceptLoopAsync()
    {
        var listener = _listener;
        if (listener is null) return;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(client));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var request = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(request)) return;
                while (!string.IsNullOrWhiteSpace(await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false))) { }
                var path = request.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";
                path = path.Split('?')[0];
                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/channels", StringComparison.OrdinalIgnoreCase))
                {
                    var json = JsonSerializer.Serialize(HealthProvider?.Invoke() ?? []);
                    await WriteResponse(stream, "application/json", Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
                    return;
                }
                if (path.StartsWith("/fill/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/key/", StringComparison.OrdinalIgnoreCase))
                {
                    var key = path.StartsWith("/key/", StringComparison.OrdinalIgnoreCase);
                    var prefix = key ? "/key/" : "/fill/";
                    var raw = path[prefix.Length..];
                    var pngId = raw.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? raw[..^4] : raw;
                    var png = PngProvider?.Invoke(pngId, key);
                    if (png is not { Length: > 0 })
                    {
                        await WriteResponse(stream, "text/plain", Encoding.UTF8.GetBytes(
                            $"No {(key ? "key" : "fill")} frame for {pngId}."), "404 Not Found").ConfigureAwait(false);
                        return;
                    }
                    await WriteResponse(stream, "image/png", png).ConfigureAwait(false);
                    return;
                }
                if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    await StreamAudio(stream).ConfigureAwait(false);
                    return;
                }
                if (!path.EndsWith(".mjpeg", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponse(stream, "text/plain", Encoding.UTF8.GetBytes(
                        "ScorePlus channel headend. Use /health, /display/{display}.mjpeg, /channel/{channel}.mjpeg, /audio/{id}.wav, /fill/{id}.png or /key/{id}.png")).ConfigureAwait(false);
                    return;
                }
                var id = path.StartsWith("/display/", StringComparison.OrdinalIgnoreCase)
                    ? path[9..^6]
                    : path.StartsWith("/channel/", StringComparison.OrdinalIgnoreCase)
                        ? path[9..^6]
                        : path.Trim('/').Replace(".mjpeg", "", StringComparison.OrdinalIgnoreCase);
                await StreamMjpeg(stream, Uri.UnescapeDataString(id)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // A viewer closing a TV or browser is normal; it must not stop the headend.
            }
        }
    }

    async Task StreamMjpeg(NetworkStream stream, string displayId)
    {
        _viewers.AddOrUpdate(displayId, 1, (_, count) => count + 1);
        try
        {
            var header = "HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=scoreplus\r\n"
                + "Cache-Control: no-store, no-cache\r\nConnection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, _stop.Token).ConfigureAwait(false);
            while (!_stop.IsCancellationRequested)
            {
                var frame = FrameProvider?.Invoke(displayId);
                if (frame is null) _frames.TryGetValue(displayId, out frame);
                if (frame is { Length: > 0 })
                {
                    var head = Encoding.ASCII.GetBytes($"--scoreplus\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
                    await stream.WriteAsync(head, _stop.Token).ConfigureAwait(false);
                    await stream.WriteAsync(frame, _stop.Token).ConfigureAwait(false);
                    await stream.WriteAsync("\r\n"u8.ToArray(), _stop.Token).ConfigureAwait(false);
                }
                await Task.Delay(120, _stop.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_viewers.AddOrUpdate(displayId, 0, (_, count) => Math.Max(0, count - 1)) == 0)
                _viewers.TryRemove(displayId, out _);
        }
    }

    async Task StreamAudio(NetworkStream stream)
    {
        var audio = _audio ??= new SystemAudioTap();
        audio.AddClient();
        try
        {
            var header = "HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nCache-Control: no-store, no-cache\r\n"
                + "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token).ConfigureAwait(false);
            await stream.WriteAsync(audio.WavHeader(), _stop.Token).ConfigureAwait(false);
            while (!_stop.IsCancellationRequested)
            {
                var chunk = await audio.ReadAsync(_stop.Token).ConfigureAwait(false);
                await stream.WriteAsync(chunk, _stop.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            audio.RemoveClient();
        }
    }

    static async Task WriteResponse(NetworkStream stream, string contentType, byte[] body, string status = "200 OK")
    {
        var header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n"
            + "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    static string LocalAddress()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address))?.ToString() ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }

    public void Dispose()
    {
        Stop();
        _audio?.Dispose();
        _audio = null;
        _stop.Dispose();
    }
}