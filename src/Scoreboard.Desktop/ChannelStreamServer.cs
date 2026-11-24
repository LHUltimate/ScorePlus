using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Scoreboard.Desktop;

public sealed record ChannelHealth(string Id, string Name, string Layout, string Status, string Url);

/// <summary>
/// A small local HTTP/MJPEG headend. Every display gets a real stream URL that VLC, a browser or a
/// TV can open; the same server exposes JSON health for the Live Channel page. Nothing leaves the
/// machine unless the operator points a player at the LAN address.
/// </summary>
public sealed class ChannelStreamServer : IDisposable
{
    readonly ConcurrentDictionary<string, byte[]> _frames = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource _stop = new();
    TcpListener? _listener;
    Task? _accept;

    public int Port { get; private set; }
    public bool IsRunning => _listener is not null;
    public string? Error { get; private set; }
    public Func<IReadOnlyList<ChannelHealth>>? HealthProvider { get; set; }
    public Func<string, byte[]?>? FrameProvider { get; set; }
    public string BaseUrl => $"http://{LocalAddress()}:{Port}";

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
        _listener = null;
        _accept = null;
    }

    public void UpdateFrame(string displayId, byte[] jpeg)
    {
        if (jpeg.Length > 0) _frames[displayId] = jpeg;
    }

    public string DisplayUrl(string displayId) => $"{BaseUrl}/display/{Uri.EscapeDataString(displayId)}.mjpeg";

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
                if (!path.EndsWith(".mjpeg", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponse(stream, "text/plain", Encoding.UTF8.GetBytes(
                        "ScorePlus channel headend. Use /health or /display/{display}.mjpeg")).ConfigureAwait(false);
                    return;
                }
                var id = path.Contains("/display/", StringComparison.OrdinalIgnoreCase)
                    ? path[(path.IndexOf("/display/", StringComparison.OrdinalIgnoreCase) + 9)..^6]
                    : path.Trim('/').Replace(".mjpeg", "", StringComparison.OrdinalIgnoreCase);
                await StreamMjpeg(stream, id).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // A viewer closing a TV or browser is normal; it must not stop the headend.
            }
        }
    }

    async Task StreamMjpeg(NetworkStream stream, string displayId)
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

    static async Task WriteResponse(NetworkStream stream, string contentType, byte[] body)
    {
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n"
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
        _stop.Dispose();
    }
}