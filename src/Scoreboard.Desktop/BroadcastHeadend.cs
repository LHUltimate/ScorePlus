using System.Collections.Concurrent;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>A registered output on the local broadcast headend. The display is the channel the
/// output is linked to; the layout name is only what was on that channel when it registered.</summary>
public sealed record BroadcastOutput(string Id, string Name, BroadcastDestinationKind Kind, string Layout, string Display = "");

/// <summary>
/// One HTTP headend shared by the Vision page, Live Book and the graphics output. It serves
/// programme MJPEG streams and alpha-aware fill/key PNGs. No encoder or cloud credential is
/// required: a router, capture device, VLC or OBS can read the same local URLs.
/// </summary>
public sealed class BroadcastHeadend : IDisposable
{
    static readonly Lazy<BroadcastHeadend> Instance = new(() => new BroadcastHeadend());
    public static BroadcastHeadend Shared => Instance.Value;

    readonly ChannelStreamServer _server = new();
    readonly ConcurrentDictionary<string, BroadcastOutput> _outputs = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte[]> _fill = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte[]> _key = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();
    bool _configured;
    double _lastRenderMs;
    long _frames;

    BroadcastHeadend()
    {
        _server.FrameProvider = id => null;
        _server.PngProvider = (id, key) =>
        {
            var store = key ? _key : _fill;
            return store.TryGetValue(id, out var bytes) ? bytes : null;
        };
        _server.HealthProvider = Health;
    }

    /// <summary>The last measured render time from any surface. Vision shows this instead of claiming
    /// a theoretical frame budget: a frame that takes 30 ms is late on a 60 Hz channel.</summary>
    public double LastRenderMilliseconds => _lastRenderMs;
    public long RenderedFrames => _frames;

    public void NoteRender(double milliseconds)
    {
        if (double.IsFinite(milliseconds) && milliseconds >= 0) _lastRenderMs = milliseconds;
        _frames++;
    }

    public bool IsRunning => _server.IsRunning;
    public int Port => _server.Port;
    public string? Error => _server.Error;
    public string BaseUrl => _server.IsRunning ? _server.BaseUrl : $"http://127.0.0.1:{(_server.Port > 0 ? _server.Port : 8100)}";

    public IReadOnlyList<BroadcastOutput> Outputs => _outputs.Values
        .OrderBy(output => output.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public bool Start(int preferredPort = 8100)
    {
        lock (_gate)
        {
            if (_server.IsRunning) return true;
            var start = preferredPort <= 0 ? 8100 : Math.Clamp(preferredPort, 1024, 65535);
            for (var offset = 0; offset < 12; offset++)
            {
                var port = start + offset;
                if (port > 65535) break;
                if (!_server.Start(port)) continue;
                Configure();
                return true;
            }
            return false;
        }
    }

    void Configure()
    {
        if (_configured) return;
        _configured = true;
        // The channel server already calls these properties. Reassigning here keeps the delegate
        // closures close to the dictionaries they read and makes the provider contract explicit.
        _server.FrameProvider = id => null;
        _server.PngProvider = (id, key) =>
        {
            var store = key ? _key : _fill;
            return store.TryGetValue(id, out var bytes) ? bytes : null;
        };
        _server.HealthProvider = Health;
    }

    public void Register(string id, string name, BroadcastDestinationKind kind, string layout, string display = "")
    {
        id = CleanId(id);
        if (id.Length == 0) return;
        _outputs[id] = new BroadcastOutput(id, string.IsNullOrWhiteSpace(name) ? id : name.Trim(), kind,
            string.IsNullOrWhiteSpace(layout) ? "Published layout" : layout.Trim(), (display ?? "").Trim());
    }

    public void Unregister(string id)
    {
        id = CleanId(id);
        _outputs.TryRemove(id, out _);
        _server.RemoveFrame(id);
        _fill.TryRemove(id, out _);
        _key.TryRemove(id, out _);
    }

    public void SetFrames(string id, BroadcastRenderResult frame)
    {
        id = CleanId(id);
        if (id.Length == 0 || frame.Jpeg.Length == 0) return;
        _server.UpdateFrame(id, frame.Jpeg);
        _fill[id] = frame.FillPng;
        _key[id] = frame.KeyPng;
    }

    public string StreamUrl(string id) => $"{BaseUrl}/display/{Uri.EscapeDataString(CleanId(id))}.mjpeg";
    /// <summary>The same stream address on loopback, for an encoder running on this machine.</summary>
    public string LocalStreamUrl(string id) => $"http://127.0.0.1:{Port}/display/{Uri.EscapeDataString(CleanId(id))}.mjpeg";
    /// <summary>The programme's system-audio mix on loopback, for the provider encoder.</summary>
    public string LocalAudioUrl(string id) => $"http://127.0.0.1:{Port}/audio/{Uri.EscapeDataString(CleanId(id))}.wav";
    public string FillUrl(string id) => $"{BaseUrl}/fill/{Uri.EscapeDataString(CleanId(id))}.png";
    public string KeyUrl(string id) => $"{BaseUrl}/key/{Uri.EscapeDataString(CleanId(id))}.png";
    public int ViewerCount(string id) => _server.ViewerCount(CleanId(id));

    public void RefreshDestination(BroadcastDestination destination)
    {
        // Registered is not the same as delivering a frame: a fill/key destination is only live
        // once the graphics renderer has published its first fill, and a stream once its stream
        // frame exists. That distinction is what keeps the UI from claiming a black route is live.
        var hasFrame = destination.Kind == BroadcastDestinationKind.FillKey
            ? _fill.ContainsKey(destination.Id) && _key.ContainsKey(destination.Id)
            : _server.HasFrame(destination.Id);
        var live = IsRunning && destination.Enabled && _outputs.ContainsKey(destination.Id) && hasFrame;
        destination.IsLive = live;
        destination.Viewers = ViewerCount(destination.Id);
        destination.Url = destination.Kind == BroadcastDestinationKind.FillKey
            ? FillUrl(destination.Id)
            : StreamUrl(destination.Id);
        destination.LastError = IsRunning ? "" : Error ?? "the local headend is stopped";
    }

    public string[] UrlsFor(BroadcastDestination destination) => destination.Kind switch
    {
        BroadcastDestinationKind.FillKey => [FillUrl(destination.Id), KeyUrl(destination.Id)],
        _ => [StreamUrl(destination.Id)]
    };

    IReadOnlyList<ChannelHealth> Health() => _outputs.Values.Select(output =>
    {
        var viewers = ViewerCount(output.Id);
        var status = IsRunning ? $"LIVE · {viewers} viewer{(viewers == 1 ? "" : "s")}" : "READY";
        return new ChannelHealth(output.Id, output.Name, output.Kind.ToString(), status, StreamUrl(output.Id));
    }).ToArray();

    static string CleanId(string? id) => (id ?? "").Trim();

    public void Dispose()
    {
        _server.Dispose();
        _fill.Clear();
        _key.Clear();
        _outputs.Clear();
    }
}
