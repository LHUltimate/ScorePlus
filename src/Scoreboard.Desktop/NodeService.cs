using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The console side of Stage 5. With no nodes configured this service is inert and the existing
/// single-machine output stays authoritative. Adding a node turns the same show into a small
/// distributed system: state is sequence-stamped, packages are versioned, and each node reports
/// its own frame health.
/// </summary>
public sealed class NodeService : IDisposable
{
    public const int DefaultNodePort = 8098;
    public const int DiscoveryPort = 45871;
    const string DiscoveryProbe = "SCOREPLUS_DISCOVER";
    const int MaxPackageBytes = 12 * 1024 * 1024;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    readonly VenueEngine _venue;
    readonly GameEngine _engine;
    readonly ScorebugCatalog _catalog;
    readonly Dispatcher _dispatcher;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(1200) };
    readonly CancellationTokenSource _stop = new();
    readonly object _gate = new();
    readonly string _epoch = Guid.NewGuid().ToString("N");
    long _sequence;
    int _packageVersion = 1;
    bool _disposed;

    public NodeService(VenueEngine venue, GameEngine engine, ScorebugCatalog catalog, Dispatcher dispatcher)
    {
        _venue = venue;
        _engine = engine;
        _catalog = catalog;
        _dispatcher = dispatcher;
        _ = Task.Run(PollLoopAsync);
    }

    public event Action? Changed;

    public IReadOnlyList<DisplayNode> Nodes => _venue.State.Nodes;
    public IReadOnlyList<SyncGroup> SyncGroups => _venue.State.SyncGroups;
    public bool IsLocalMode => Nodes.Count == 0;
    public int OnlineCount => Nodes.Count(node => node.Health.Online && !node.Health.Stale);
    public int StaleCount => Nodes.Count(node => node.Health.Stale || (!node.Health.Online && node.Enabled));
    public string Summary => IsLocalMode
        ? "LOCAL MODE · this PC renders every display"
        : $"{Nodes.Count} node{(Nodes.Count == 1 ? "" : "s")} · {OnlineCount} online · {StaleCount} stale";

    public DisplayNode AddNode(string? address = null, string? name = null)
    {
        var node = _venue.Store.AddNode(address, name);
        _venue.Store.Save();
        RaiseChanged();
        // A probe populates health; deployment stays explicit (Send package) so a newly typed
        // address cannot race a second package against the operator's first one.
        _ = RefreshHealthAsync(node);
        return node;
    }

    public void SaveNode(DisplayNode node)
    {
        node.Normalize();
        _venue.Store.Save();
    }

    public void RemoveNode(DisplayNode node)
    {
        _venue.State.Nodes.Remove(node);
        _venue.Store.Save();
        RaiseChanged();
    }

    public SyncGroup AddSyncGroup(string? name = null)
    {
        var group = _venue.Store.AddSyncGroup(name);
        _venue.Store.Save();
        RaiseChanged();
        return group;
    }

    public void SaveSyncGroup(SyncGroup group)
    {
        group.Normalize();
        _venue.Store.Save();
    }

    /// <summary>The current group for a node, or the first available group. Null means immediate
    /// local application, which is exactly the single-machine path.</summary>
    public SyncGroup? GroupFor(DisplayNode node) =>
        SyncGroups.FirstOrDefault(group => string.Equals(group.Id, node.SyncGroupId, StringComparison.OrdinalIgnoreCase))
        ?? SyncGroups.FirstOrDefault();

    /// <summary>Called by Update Output. Every enabled node gets one sequence; a sync group gets a
    /// shared ApplyAt so all of its screens change together. No nodes means no network work.</summary>
    public void Broadcast()
    {
        if (_disposed) return;
        var nodes = Nodes.Where(node => node.Enabled).ToArray();
        if (nodes.Length == 0) return;
        var sequence = Interlocked.Increment(ref _sequence);
        var now = DateTimeOffset.UtcNow;
        // One snapshot for every node in this broadcast: two screens must not disagree about the
        // score because the game changed halfway through the loop.
        var game = _engine.CloneState();
        foreach (var node in nodes)
        {
            var group = GroupFor(node);
            var applyAt = group is { Enabled: true } ? now.AddMilliseconds(group.LeadMilliseconds) : now;
            var message = new NodeStateMessage
            {
                Epoch = _epoch,
                Sequence = sequence,
                SentAt = now,
                ApplyAt = applyAt,
                DisplayId = node.DisplayId,
                SyncGroupId = group?.Id ?? "",
                Game = game,
                Output = node.Output.Copy(),
                Calibration = node.Calibration.Copy(),
                TestPattern = node.Output.TestPattern
            };
            _ = SendStateAsync(node, message);
        }
    }

    /// <summary>Pushes the current layouts, brand, storyboard, sponsors and venue rotation as a versioned package.</summary>
    public async Task<bool> DeployAsync(DisplayNode node)
    {
        if (_disposed) return false;
        try
        {
            var document = new ScorebugDocument
            {
                Displays = _catalog.Displays,
                ActiveDisplayIndex = _catalog.ActiveDisplayIndex,
                Transitions = _catalog.Transitions,
                Animations = _catalog.Animations,
                Brand = _catalog.Brand,
                Storyboard = _catalog.Storyboard,
                Sponsors = _catalog.Sponsors
            };
            var package = new NodePackage
            {
                Version = Math.Max(1, _packageVersion++),
                Summary = $"{_catalog.Displays.Count} displays · {_catalog.Displays.Sum(display => display.Layouts.Count)} layouts",
                CatalogJson = JsonSerializer.Serialize(document, JsonOptions),
                VenueJson = JsonSerializer.Serialize(_venue.Store.State, JsonOptions)
            };
            var json = JsonSerializer.Serialize(package, JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaxPackageBytes) throw new InvalidOperationException("The content package is larger than the node limit.");
            using var response = await SendAsync(node, HttpMethod.Post, "/package", json).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Node returned {(int)response.StatusCode}.");
            node.Health = node.Health with { PackageVersion = package.Version, Online = true, Error = "" };
            RaiseChanged();
            Broadcast();
            return true;
        }
        catch (Exception ex)
        {
            MarkError(node, ex.Message);
            return false;
        }
    }

    public async Task<bool> RollbackAsync(DisplayNode node)
    {
        try
        {
            using var response = await SendAsync(node, HttpMethod.Post, "/rollback", "{}").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Node returned {(int)response.StatusCode}.");
            await RefreshHealthAsync(node).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            MarkError(node, ex.Message);
            return false;
        }
    }

    public async Task<bool> SetTestPatternAsync(DisplayNode node, bool on)
    {
        try
        {
            using var response = await SendAsync(node, HttpMethod.Post, "/test", JsonSerializer.Serialize(new { on }, JsonOptions)).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Node returned {(int)response.StatusCode}.");
            node.Output.TestPattern = on;
            node.Health = node.Health with { TestPattern = on };
            SaveNode(node);
            return true;
        }
        catch (Exception ex)
        {
            MarkError(node, ex.Message);
            return false;
        }
    }

    async Task<bool> SendStateAsync(DisplayNode node, NodeStateMessage message)
    {
        try
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
            using var response = await SendAsync(node, HttpMethod.Post, "/state", json).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Node returned {(int)response.StatusCode}.");
            node.Health = node.Health with
            {
                Online = true, Stale = false, Sequence = message.Sequence,
                LastSeen = DateTimeOffset.UtcNow, Error = ""
            };
            return true;
        }
        catch (Exception ex)
        {
            MarkError(node, ex.Message);
            return false;
        }
    }

    async Task<HttpResponseMessage> SendAsync(DisplayNode node, HttpMethod method, string path, string json)
    {
        using var request = new HttpRequestMessage(method, $"{node.Endpoint}{path}");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(node.Token)) request.Headers.Add("X-ScorePlus-Node-Token", node.Token);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, _stop.Token).ConfigureAwait(false);
    }

    async Task PollLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                foreach (var node in Nodes.Where(node => node.Enabled).ToArray())
                    await RefreshHealthAsync(node).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* a poll is best effort; the show must keep running */ }
            try { await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<bool> RefreshHealthAsync(DisplayNode node)
    {
        try
        {
            using var response = await SendAsync(node, HttpMethod.Get, "/health", "").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Node returned {(int)response.StatusCode}.");
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var report = JsonSerializer.Deserialize<NodeHealthReport>(json, JsonOptions)
                ?? throw new InvalidOperationException("Node returned no health report.");
            var age = report.LastSeen == default ? TimeSpan.Zero : DateTimeOffset.UtcNow - report.LastSeen;
            node.Health = report with
            {
                Online = true,
                Stale = report.Stale || age > TimeSpan.FromSeconds(5),
                LastSeen = DateTimeOffset.UtcNow,
                Error = ""
            };
            RaiseChanged();
            return true;
        }
        catch (Exception ex)
        {
            MarkError(node, ex.Message);
            return false;
        }
    }

    /// <summary>Finds agents on the LAN and on this PC. A venue with no network still works; this
    /// is a convenience for the first multi-machine setup.</summary>
    public async Task<IReadOnlyList<NodeDiscovery>> DiscoverAsync(int timeoutMs = 900)
    {
        var found = new List<NodeDiscovery>();
        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            udp.Client.ReceiveTimeout = timeoutMs;
            var probe = Encoding.UTF8.GetBytes(DiscoveryProbe);
            await udp.SendAsync(probe.AsMemory(), new IPEndPoint(IPAddress.Broadcast, DiscoveryPort)).ConfigureAwait(false);
            var until = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTimeOffset.UtcNow < until)
            {
                var receive = udp.ReceiveAsync();
                var completed = await Task.WhenAny(receive, Task.Delay(Math.Max(1, (int)(until - DateTimeOffset.UtcNow).TotalMilliseconds))).ConfigureAwait(false);
                if (!ReferenceEquals(completed, receive)) break;
                var packet = await receive.ConfigureAwait(false);
                var json = Encoding.UTF8.GetString(packet.Buffer);
                var item = JsonSerializer.Deserialize<NodeDiscovery>(json, JsonOptions);
                if (item is null || string.IsNullOrWhiteSpace(item.Address) || item.Port < 1024) continue;
                if (found.Any(existing => string.Equals(existing.NodeId, item.NodeId, StringComparison.OrdinalIgnoreCase))) continue;
                found.Add(item);
            }
        }
        catch (SocketException) { /* broadcast is optional */ }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        return found;
    }

    public void RaiseChanged()
    {
        if (_disposed) return;
        void Publish() => Changed?.Invoke();
        if (_dispatcher.CheckAccess()) Publish();
        else _dispatcher.BeginInvoke(Publish);
    }

    void MarkError(DisplayNode node, string message)
    {
        node.Health = node.Health with
        {
            Online = false,
            Stale = true,
            Error = message,
            LastSeen = node.Health.LastSeen
        };
        RaiseChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stop.Cancel(); } catch { }
        _http.Dispose();
        _stop.Dispose();
    }
}
