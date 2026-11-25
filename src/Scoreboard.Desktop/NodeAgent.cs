using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>Command line for a node process: ScorePlus.exe --node --port 8098 --name "Ribbon".</summary>
public sealed class NodeAgentOptions
{
    public int Port { get; set; } = NodeService.DefaultNodePort;
    public string Name { get; set; } = Environment.MachineName;
    public NodeRole Role { get; set; } = NodeRole.Player;
    public string DisplayId { get; set; } = "";
    public string SyncGroupId { get; set; } = "";
    public string Token { get; set; } = "";
    public bool Headless { get; set; }
    public bool Fullscreen { get; set; } = true;
    public string? DataDirectory { get; set; }
}

/// <summary>
/// A headless mode of the same renderer, not a second graphics path. It receives sequence-stamped
/// show state and content packages over a plain authenticated local HTTP transport, renders through
/// <see cref="ScorebugCanvasView"/>, and keeps the last state if the controller disappears.
/// </summary>
public sealed class NodeAgent : IDisposable
{
    public const int DiscoveryPort = NodeService.DiscoveryPort;
    const string DiscoveryProbe = "SCOREPLUS_DISCOVER";
    const long MaxJsonBodyBytes = 8L * 1024 * 1024;
    const long MaxPackageBytes = 8L * 1024 * 1024 * 1024;
    const string TokenHeader = "X-ScorePlus-Node-Token";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    readonly NodeAgentOptions _options;
    readonly Dispatcher _dispatcher;
    readonly string _folder;
    readonly string _catalogPath;
    readonly string _previousCatalogPath;
    readonly string _versionPath;
    readonly string _logPath;
    readonly GameEngine _engine = new();
    readonly CancellationTokenSource _stop = new();
    readonly object _gate = new();
    ScorebugCatalog _catalog;
    NodeWindow? _window;
    TcpListener? _listener;
    UdpClient? _discovery;
    Task? _accept;
    Task? _discoveryLoop;
    long _lastSequence;
    long _appliedSequence;
    string _stateEpoch = "";
    DateTimeOffset _epochSentAt;
    long _frames;
    DateTimeOffset _lastStateAt;
    DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    double _frameMs = 16.7;
    double _driftMs;
    int _packageVersion;
    bool _testPattern;
    string _lastError = "";
    string _displayId;
    NodeOutputSettings _output = new();
    NodeCalibration _calibration = new();

    public NodeAgent(NodeAgentOptions options, Dispatcher dispatcher)
    {
        _options = options;
        _dispatcher = dispatcher;
        _folder = options.DataDirectory ?? AppPaths.Combine("node");
        Directory.CreateDirectory(_folder);
        _catalogPath = Path.Combine(_folder, "scorebug.json");
        _previousCatalogPath = Path.Combine(_folder, "scorebug.previous.json");
        _versionPath = Path.Combine(_folder, "package-version.txt");
        _logPath = Path.Combine(_folder, "node.log");
        _displayId = options.DisplayId;
        try
        {
            _catalog = new ScorebugCatalog(_catalogPath);
            if (File.Exists(_versionPath) && int.TryParse(File.ReadAllText(_versionPath), out var version))
                _packageVersion = Math.Max(0, version);
        }
        catch
        {
            _catalog = new ScorebugCatalog(_catalogPath);
        }
        _output = new NodeOutputSettings
        {
            Width = 1920, Height = 1080, RefreshHz = 60, LowLatency = true
        };
    }

    public NodeAgentOptions Options => _options;
    public GameEngine Engine => _engine;
    public ScorebugCatalog Catalog => _catalog;
    public string NodeDisplayId => string.IsNullOrWhiteSpace(_displayId) ? _catalog.ActiveDisplay.Id : _displayId;
    public NodeOutputSettings Output => _output;
    public NodeCalibration Calibration => _calibration;
    public bool TestPattern => _testPattern;
    public bool IsRunning => _listener is not null;
    public string? Error { get; private set; }
    public string Version => typeof(NodeAgent).Assembly.GetName().Version?.ToString(3) ?? "dev";
    public event Action? OutputChanged;

    /// <summary>Parses only node invocations. A normal double-click on ScorePlus never matches.</summary>
    public static bool TryParseLaunch(string[] args, out NodeAgentOptions options)
    {
        options = new NodeAgentOptions();
        if (!args.Any(arg => string.Equals(arg, "--node", StringComparison.OrdinalIgnoreCase))) return false;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            string? Next() => index + 1 < args.Length ? args[++index] : null;
            switch (arg.ToLowerInvariant())
            {
                case "--port":
                    if (int.TryParse(Next(), out var port)) options.Port = Math.Clamp(port, 1024, 65535);
                    break;
                case "--name":
                    options.Name = Next()?.Trim() ?? options.Name;
                    break;
                case "--role":
                    if (Enum.TryParse<NodeRole>(Next(), true, out var role)) options.Role = role;
                    break;
                case "--display":
                    options.DisplayId = Next()?.Trim() ?? "";
                    break;
                case "--sync":
                    options.SyncGroupId = Next()?.Trim() ?? "";
                    break;
                case "--token":
                    options.Token = Next()?.Trim() ?? "";
                    break;
                case "--data":
                    options.DataDirectory = Next();
                    break;
                case "--headless":
                    options.Headless = true;
                    break;
                case "--windowed":
                    options.Fullscreen = false;
                    break;
            }
        }
        return true;
    }

    public bool Start()
    {
        if (_listener is not null) return true;
        try
        {
            _listener = new TcpListener(IPAddress.Any, _options.Port);
            _listener.Start();
            Error = null;
            _startedAt = DateTimeOffset.UtcNow;
            _accept = Task.Run(AcceptLoopAsync);
            StartDiscovery();
            Log($"Node agent started on port {_options.Port} as {_options.Role}.");
            return true;
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            Error = $"Node port {_options.Port} could not open: {ex.Message}";
            Log(Error);
            _listener = null;
            return false;
        }
    }

    public void ShowOutput(bool fullscreen)
    {
        if (_window is { IsVisible: true }) { _window.Activate(); return; }
        _window = new NodeWindow(this);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        if (fullscreen) _window.Loaded += (_, _) => WindowFullscreen.Enter(_window);
    }

    public void NoteFrame(double milliseconds)
    {
        var next = Math.Max(1, Math.Min(1000, milliseconds));
        _frameMs = _frameMs * 0.9 + next * 0.1;
        Interlocked.Increment(ref _frames);
    }

    public NodeHealthReport HealthReport()
    {
        var online = _lastStateAt != default;
        var stale = online && DateTimeOffset.UtcNow - _lastStateAt > TimeSpan.FromSeconds(5);
        return new NodeHealthReport
        {
            Online = true,
            Stale = stale,
            NodeId = NodeId,
            Name = _options.Name,
            Role = _options.Role,
            DisplayId = NodeDisplayId,
            SyncGroupId = _options.SyncGroupId,
            Version = Version,
            Sequence = Interlocked.Read(ref _appliedSequence),
            LastFrameMs = _frameMs,
            DriftMs = _driftMs,
            PackageVersion = _packageVersion,
            Resolution = _output.ResolutionLabel,
            TestPattern = _testPattern,
            Error = _lastError,
            LastSeen = DateTimeOffset.UtcNow
        };
    }

    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..12];

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
                var requestLine = await ReadHeaderLineAsync(stream, _stop.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine)) return;
                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return;
                var method = parts[0].ToUpperInvariant();
                var path = parts[1].Split('?')[0];
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    var line = await ReadHeaderLineAsync(stream, _stop.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line)) break;
                    var split = line.IndexOf(':');
                    if (split > 0) headers[line[..split].Trim()] = line[(split + 1)..].Trim();
                }

                var authorized = string.IsNullOrWhiteSpace(_options.Token)
                    || (headers.TryGetValue(TokenHeader, out var supplied) && string.Equals(supplied, _options.Token, StringComparison.Ordinal));
                if (!authorized)
                {
                    await WriteResponse(stream, 401, "application/json", "{\"error\":\"token required\"}").ConfigureAwait(false);
                    return;
                }

                long contentLength = 0;
                var hasLength = headers.TryGetValue("Content-Length", out var lengthText)
                    && long.TryParse(lengthText, out contentLength) && contentLength >= 0;
                contentLength = hasLength ? contentLength : 0;
                var route = (method, path.ToLowerInvariant());

                // Packages are streamed straight to disk. Never turn a show video into a string:
                // that was the old base64/JSON path and it ran out of memory on real clips.
                if (route == ("POST", "/package"))
                {
                    if (!hasLength || contentLength <= 0)
                    {
                        await WriteResponse(stream, 411, "application/json", "{\"error\":\"package length required\"}").ConfigureAwait(false);
                        return;
                    }
                    if (contentLength > MaxPackageBytes)
                    {
                        await WriteResponse(stream, 413, "application/json", "{\"error\":\"package too large\"}").ConfigureAwait(false);
                        return;
                    }
                    var temp = Path.Combine(_folder, $"incoming-package-{Guid.NewGuid():N}.spn");
                    try
                    {
                        await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                            1024 * 1024, FileOptions.SequentialScan))
                            await NodePackageBinary.CopyExactlyAsync(stream, file, contentLength, null, _stop.Token).ConfigureAwait(false);

                        PackageUnpackResult unpacked;
                        try
                        {
                            unpacked = await Task.Run(() => UnpackPackage(temp), _stop.Token).ConfigureAwait(false);
                            await _dispatcher.InvokeAsync(() => ApplyPackage(unpacked.Package, unpacked.Mapped)).Task.ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _lastError = ex.Message;
                            Log($"Package rejected: {ex.Message}");
                            var error = JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
                            await WriteResponse(stream, 500, "application/json", error).ConfigureAwait(false);
                            return;
                        }
                        await WriteJson(stream, new { accepted = true, version = unpacked.Package.Version }).ConfigureAwait(false);
                    }
                    finally
                    {
                        try { File.Delete(temp); } catch { }
                    }
                    return;
                }

                if (contentLength > MaxJsonBodyBytes)
                {
                    await WriteResponse(stream, 413, "application/json", "{\"error\":\"body too large\"}").ConfigureAwait(false);
                    return;
                }
                var body = contentLength > 0
                    ? await ReadBodyStringAsync(stream, (int)contentLength, _stop.Token).ConfigureAwait(false)
                    : "";

                switch (route)
                {
                    case ("GET", "/health"):
                        await WriteJson(stream, HealthReport()).ConfigureAwait(false);
                        return;
                    case ("GET", "/log"):
                        await WriteResponse(stream, 200, "text/plain", ReadLog()).ConfigureAwait(false);
                        return;
                    case ("POST", "/state"):
                        var state = JsonSerializer.Deserialize<NodeStateMessage>(body, JsonOptions);
                        if (state is null) throw new InvalidOperationException("State message was empty.");
                        _lastStateAt = DateTimeOffset.UtcNow;
                        _ = ApplyStateAsync(state);
                        await WriteJson(stream, new { accepted = true, sequence = state.Sequence }).ConfigureAwait(false);
                        return;
                    case ("POST", "/rollback"):
                        await _dispatcher.InvokeAsync(RollbackPackage).Task.ConfigureAwait(false);
                        await WriteJson(stream, new { accepted = true, version = _packageVersion }).ConfigureAwait(false);
                        return;
                    case ("POST", "/test"):
                        var request = JsonSerializer.Deserialize<TestRequest>(body, JsonOptions);
                        _testPattern = request?.On == true;
                        await _dispatcher.InvokeAsync(() => OutputChanged?.Invoke()).Task.ConfigureAwait(false);
                        await WriteJson(stream, new { accepted = true, test = _testPattern }).ConfigureAwait(false);
                        return;
                    default:
                        await WriteResponse(stream, 404, "application/json", "{\"error\":\"not found\"}").ConfigureAwait(false);
                        return;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // A closed controller is normal. The last rendered frame stays on screen.
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log($"Request failed: {ex.Message}");
            }
        }
    }

    static async Task<string?> ReadHeaderLineAsync(NetworkStream stream, CancellationToken token)
    {
        var bytes = new List<byte>(128);
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (one[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            if (bytes.Count >= 16 * 1024) throw new InvalidDataException("HTTP header line is too long.");
            bytes.Add(one[0]);
        }
    }

    static async Task<string> ReadBodyStringAsync(NetworkStream stream, int length, CancellationToken token)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, length - read), token).ConfigureAwait(false);
            if (count <= 0) break;
            read += count;
        }
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    async Task ApplyStateAsync(NodeStateMessage state)
    {
        try
        {
            var delay = state.ApplyAt == default ? TimeSpan.Zero : state.ApplyAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
            }
            await _dispatcher.InvokeAsync(() =>
            {
                var newEpoch = !string.IsNullOrWhiteSpace(state.Epoch)
                    && !string.Equals(state.Epoch, _stateEpoch, StringComparison.Ordinal);
                // A delayed message from the old console may still arrive after the new console's
                // first message. Its send time is older, so it must not take the epoch back.
                if (newEpoch && state.SentAt < _epochSentAt) return;
                if (!newEpoch && state.Sequence < Interlocked.Read(ref _appliedSequence)) return;
                if (newEpoch)
                {
                    _stateEpoch = state.Epoch;
                    _epochSentAt = state.SentAt;
                }
                Interlocked.Exchange(ref _lastSequence, state.Sequence);
                Interlocked.Exchange(ref _appliedSequence, state.Sequence);
                if (state.Game is not null) _engine.LoadFromState(state.Game);
                _output = state.Output ?? _output;
                _calibration = state.Calibration ?? _calibration;
                _testPattern = state.TestPattern;
                if (!string.IsNullOrWhiteSpace(state.DisplayId)) _displayId = state.DisplayId;
                _driftMs = state.ApplyAt == default
                    ? 0
                    : Math.Abs((DateTimeOffset.UtcNow - state.ApplyAt).TotalMilliseconds);
                _lastStateAt = DateTimeOffset.UtcNow;
                OutputChanged?.Invoke();
            }).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log($"State apply failed: {ex.Message}");
        }
    }

    PackageUnpackResult UnpackPackage(string packagePath)
    {
        using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        var package = NodePackageBinary.ReadManifest(stream);
        if (string.IsNullOrWhiteSpace(package.CatalogJson))
            throw new InvalidOperationException("Package has no layout document.");

        // Each version gets its own asset folder. A partially written package can never replace
        // the files the previous catalog is still pointing at, and rollback keeps working.
        var versionFolder = SafeFileName(string.IsNullOrWhiteSpace(package.PackageId)
            ? package.Version.ToString()
            : package.PackageId);
        var assetFolder = Path.Combine(_folder, "assets", versionFolder);
        Directory.CreateDirectory(assetFolder);
        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in package.Assets ?? [])
        {
            var name = SafeFileName(string.IsNullOrWhiteSpace(asset.FileName) ? "asset.bin" : Path.GetFileName(asset.FileName));
            var target = Path.Combine(assetFolder, name);
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1024 * 1024];
                var remaining = asset.Size;
                while (remaining > 0)
                {
                    var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) throw new EndOfStreamException($"Package asset {asset.FileName} ended early.");
                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(asset.Sha256)
                    && !string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Package asset {asset.FileName} failed its hash check.");
            }
            if (!string.IsNullOrWhiteSpace(asset.SourcePath)) mapped[asset.SourcePath] = target;
        }
        return new PackageUnpackResult(package, mapped);
    }

    void ApplyPackage(NodePackage package, IReadOnlyDictionary<string, string> mapped)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(package.CatalogJson)) throw new InvalidOperationException("Package has no layout document.");
            if (File.Exists(_catalogPath)) File.Copy(_catalogPath, _previousCatalogPath, true);

            var catalogJson = MapAssetPaths(package.CatalogJson, mapped);
            var temp = _catalogPath + ".tmp";
            File.WriteAllText(temp, catalogJson);
            File.Move(temp, _catalogPath, true);
            File.WriteAllText(_versionPath, package.Version.ToString());
            if (!string.IsNullOrWhiteSpace(package.VenueJson))
                File.WriteAllText(Path.Combine(_folder, "venue.json"), MapAssetPaths(package.VenueJson, mapped));
            _catalog = new ScorebugCatalog(_catalogPath);
            _packageVersion = package.Version;
            _lastError = "";
            Log($"Catalog loaded: {_catalog.Displays.Count} displays · "
                + $"{_catalog.Displays.Sum(display => display.Layouts.Sum(layout => layout.Elements.Count))} elements · "
                + $"warning: {_catalog.Warning ?? "none"}.");
            Log($"Package {package.Version} installed · {package.Summary}"
                + $" · {mapped.Count} asset{(mapped.Count == 1 ? "" : "s")} unpacked"
                + (package.MissingAssets is { Count: > 0 } ? $" · {package.MissingAssets.Count} missing on console" : "") + ".");
            OutputChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log($"Package failed: {ex.Message}");
            throw;
        }
    }

    static string SafeFileName(string name)
    {
        var cleaned = new string(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "asset.bin" : cleaned;
    }

    /// <summary>Rewrites only exact path strings in the package JSON, leaving layout names and
    /// text content alone. The node stores assets in its own folder, so the console's absolute
    /// paths cannot leak into the node's catalog.</summary>
    static string MapAssetPaths(string json, IReadOnlyDictionary<string, string> mapped)
    {
        if (mapped.Count == 0 || string.IsNullOrWhiteSpace(json)) return json;
        var root = JsonNode.Parse(json);
        Rewrite(root, mapped);
        return root?.ToJsonString() ?? json;
    }

    static void Rewrite(JsonNode? node, IReadOnlyDictionary<string, string> mapped)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToArray())
                {
                    var value = obj[key];
                    if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
                        && mapped.TryGetValue(text, out var target))
                        obj[key] = target;
                    else
                        Rewrite(value, mapped);
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    var value = array[index];
                    if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
                        && mapped.TryGetValue(text, out var target))
                        array[index] = target;
                    else
                        Rewrite(value, mapped);
                }
                break;
        }
    }

    void RollbackPackage()
    {
        if (!File.Exists(_previousCatalogPath)) throw new InvalidOperationException("No previous package is stored on this node.");
        File.Copy(_previousCatalogPath, _catalogPath, true);
        _catalog = new ScorebugCatalog(_catalogPath);
        _packageVersion = Math.Max(0, _packageVersion - 1);
        File.WriteAllText(_versionPath, _packageVersion.ToString());
        _lastError = "";
        Log($"Rolled back to package {_packageVersion}.");
        OutputChanged?.Invoke();
    }

    void StartDiscovery()
    {
        try
        {
            _discovery = new UdpClient(DiscoveryPort);
            _discoveryLoop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        var packet = await _discovery.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                        if (!Encoding.UTF8.GetString(packet.Buffer).Trim().Equals(DiscoveryProbe, StringComparison.Ordinal)) continue;
                        var discovery = new NodeDiscovery
                        {
                            NodeId = NodeId,
                            Name = _options.Name,
                            Address = LocalAddress(),
                            Port = _options.Port,
                            Role = _options.Role,
                            Version = Version,
                            DisplayId = NodeDisplayId,
                            SyncGroupId = _options.SyncGroupId
                        };
                        var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(discovery, JsonOptions));
                        await _discovery.SendAsync(reply.AsMemory(), packet.RemoteEndPoint, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException) { break; }
                }
            });
        }
        catch (SocketException)
        {
            // A second node on one PC cannot bind the discovery port. It still works over a
            // manually entered address, which is what the console UI is for.
        }
    }

    static string LocalAddress()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))?.ToString()
                ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }

    static async Task WriteJson(NetworkStream stream, object value) =>
        await WriteResponse(stream, 200, "application/json", JsonSerializer.Serialize(value, JsonOptions)).ConfigureAwait(false);

    static async Task WriteResponse(NetworkStream stream, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch
        {
            200 => "OK", 401 => "Unauthorized", 404 => "Not Found", 411 => "Length Required",
            413 => "Payload Too Large", 500 => "Internal Server Error", _ => "Error"
        };
        var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n"
            + "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
    }

    string ReadLog()
    {
        try { return File.Exists(_logPath) ? File.ReadAllText(_logPath) : "No node log yet."; }
        catch { return "Node log unavailable."; }
    }

    void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            File.AppendAllText(_logPath, $"{DateTimeOffset.Now:u} {message}\n");
        }
        catch { }
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _discovery?.Dispose(); } catch { }
        _window?.Close();
        _listener = null;
        _discovery = null;
        Log("Node agent stopped.");
        _stop.Dispose();
    }

    sealed record TestRequest(bool On);

    sealed record PackageUnpackResult(NodePackage Package, IReadOnlyDictionary<string, string> Mapped);
}
