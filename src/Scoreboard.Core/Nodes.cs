using System.Text.Json.Serialization;

namespace Scoreboard.Core;

/// <summary>What a playback node does on the venue network.</summary>
public enum NodeRole
{
    Player,
    Processor,
    Monitor
}

/// <summary>The display output a node drives. A player normally asks for a display's natural
/// resolution; a processor can scale, offset or pixel-map it for an irregular surface.</summary>
public sealed class NodeOutputSettings
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int RefreshHz { get; set; } = 60;
    public bool Hdr { get; set; }
    public string ColorSpace { get; set; } = "sRGB";
    public bool LowLatency { get; set; } = true;
    public bool TestPattern { get; set; }
    // The desktop output path is SDR. HDR is carried as a requested output label until a real
    // HDR swap chain/display validation is available, so the UI must not imply it was verified.
    public string ResolutionLabel => $"{Width} × {Height} · {RefreshHz} Hz" + (Hdr ? " · HDR (unvalidated)" : "");

    public void Normalize()
    {
        Width = Math.Clamp(Width, 320, 16384);
        Height = Math.Clamp(Height, 240, 16384);
        RefreshHz = Math.Clamp(RefreshHz, 24, 240);
        ColorSpace = string.IsNullOrWhiteSpace(ColorSpace) ? "sRGB" : ColorSpace.Trim();
    }

    public NodeOutputSettings Copy() => new()
    {
        Width = Width, Height = Height, RefreshHz = RefreshHz, Hdr = Hdr,
        ColorSpace = ColorSpace, LowLatency = LowLatency, TestPattern = TestPattern
    };
}

/// <summary>Per-node scaling and mapping for a processor. The same renderer is used; only the
/// final placement changes, so a node never needs a second graphics path.</summary>
public sealed class NodeCalibration
{
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double RotationDegrees { get; set; }
    public double Gamma { get; set; } = 1;
    /// <summary>Horizontal geometric bow, in output pixels, for a curved display.</summary>
    public double CurveX { get; set; }
    /// <summary>Vertical geometric bow, in output pixels, for a curved display.</summary>
    public double CurveY { get; set; }
    // Corner pinning in output pixels. The four corners are dragged independently to fit a
    // keystoned, angled or irregular surface; interpolation across each edge keeps the picture
    // continuous instead of treating the output as four rectangular crops.
    public double CornerTopLeftX { get; set; }
    public double CornerTopLeftY { get; set; }
    public double CornerTopRightX { get; set; }
    public double CornerTopRightY { get; set; }
    public double CornerBottomLeftX { get; set; }
    public double CornerBottomLeftY { get; set; }
    public double CornerBottomRightX { get; set; }
    public double CornerBottomRightY { get; set; }
    public string MapNotes { get; set; } = "";

    public void Normalize()
    {
        ScaleX = double.IsFinite(ScaleX) ? Math.Clamp(ScaleX, 0.1, 4) : 1;
        ScaleY = double.IsFinite(ScaleY) ? Math.Clamp(ScaleY, 0.1, 4) : 1;
        OffsetX = double.IsFinite(OffsetX) ? Math.Clamp(OffsetX, -10000, 10000) : 0;
        OffsetY = double.IsFinite(OffsetY) ? Math.Clamp(OffsetY, -10000, 10000) : 0;
        RotationDegrees = double.IsFinite(RotationDegrees) ? Math.Clamp(RotationDegrees, -180, 180) : 0;
        Gamma = double.IsFinite(Gamma) ? Math.Clamp(Gamma, 0.4, 2.4) : 1;
        CurveX = double.IsFinite(CurveX) ? Math.Clamp(CurveX, -10000, 10000) : 0;
        CurveY = double.IsFinite(CurveY) ? Math.Clamp(CurveY, -10000, 10000) : 0;
        CornerTopLeftX = Finite(CornerTopLeftX);
        CornerTopLeftY = Finite(CornerTopLeftY);
        CornerTopRightX = Finite(CornerTopRightX);
        CornerTopRightY = Finite(CornerTopRightY);
        CornerBottomLeftX = Finite(CornerBottomLeftX);
        CornerBottomLeftY = Finite(CornerBottomLeftY);
        CornerBottomRightX = Finite(CornerBottomRightX);
        CornerBottomRightY = Finite(CornerBottomRightY);
        MapNotes = (MapNotes ?? "").Trim();
    }

    static double Finite(double value) => double.IsFinite(value) ? Math.Clamp(value, -10000, 10000) : 0;

    public NodeCalibration Copy() => new()
    {
        ScaleX = ScaleX, ScaleY = ScaleY, OffsetX = OffsetX, OffsetY = OffsetY,
        RotationDegrees = RotationDegrees, Gamma = Gamma,
        CurveX = CurveX, CurveY = CurveY,
        CornerTopLeftX = CornerTopLeftX, CornerTopLeftY = CornerTopLeftY,
        CornerTopRightX = CornerTopRightX, CornerTopRightY = CornerTopRightY,
        CornerBottomLeftX = CornerBottomLeftX, CornerBottomLeftY = CornerBottomLeftY,
        CornerBottomRightX = CornerBottomRightX, CornerBottomRightY = CornerBottomRightY,
        MapNotes = MapNotes
    };
}

/// <summary>A computer that renders one display (or several, if its display list says so). The
/// console stores the address and desired output; the node agent owns the actual picture.</summary>
public sealed class DisplayNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Playback node";
    public NodeRole Role { get; set; } = NodeRole.Player;
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8098;
    public string DisplayId { get; set; } = "";
    public string SyncGroupId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Token { get; set; } = "";
    public NodeOutputSettings Output { get; set; } = new();
    public NodeCalibration Calibration { get; set; } = new();

    [JsonIgnore]
    public NodeHealthReport Health { get; set; } = new();

    [JsonIgnore]
    public string Endpoint => $"http://{Address.Trim().TrimEnd('/')}:{Port}";

    [JsonIgnore]
    public string Label => $"{Name} · {Role} · {Address}:{Port}";

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Name = string.IsNullOrWhiteSpace(Name) ? "Playback node" : Name.Trim();
        Address = string.IsNullOrWhiteSpace(Address) ? "127.0.0.1" : Address.Trim()
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        Port = Math.Clamp(Port, 1024, 65535);
        Token = (Token ?? "").Trim();
        Output ??= new NodeOutputSettings();
        Calibration ??= new NodeCalibration();
        Output.Normalize();
        Calibration.Normalize();
    }
}

/// <summary>Nodes that share a target time so a reveal or takeover starts together. The console
/// sends the same sequence to every member and each node applies it at ApplyAt.</summary>
public sealed class SyncGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Main sync";
    public int LeadMilliseconds { get; set; } = 120;
    public bool Enabled { get; set; } = true;

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Name = string.IsNullOrWhiteSpace(Name) ? "Main sync" : Name.Trim();
        LeadMilliseconds = Math.Clamp(LeadMilliseconds, 0, 2000);
    }
}

/// <summary>The last thing a node told the console. This is runtime state and is never written to
/// venue.json; it is filled by the health poll.</summary>
public sealed record NodeHealthReport
{
    public bool Online { get; init; }
    public bool Stale { get; init; }
    public string NodeId { get; init; } = "";
    public string Name { get; init; } = "";
    public NodeRole Role { get; init; }
    public string DisplayId { get; init; } = "";
    public string SyncGroupId { get; init; } = "";
    public string Version { get; init; } = "";
    public long Sequence { get; init; }
    public double LastFrameMs { get; init; }
    public double DriftMs { get; init; }
    public int PackageVersion { get; init; }
    public string Resolution { get; init; } = "";
    public bool TestPattern { get; init; }
    public string Error { get; init; } = "";
    public DateTimeOffset LastSeen { get; init; }
}

/// <summary>One immutable state send. Sequence is the de-duplication key; ApplyAt is the shared
/// target frame time for a sync group.</summary>
public sealed record NodeStateMessage
{
    /// <summary>A console session marker. A restarted console starts a new epoch, so a node that
    /// stayed up does not discard the new console's lower sequence numbers as old messages.</summary>
    public string Epoch { get; init; } = "";
    public long Sequence { get; init; }
    public DateTimeOffset SentAt { get; init; }
    public DateTimeOffset ApplyAt { get; init; }
    public string DisplayId { get; init; } = "";
    public string SyncGroupId { get; init; } = "";
    public GameState Game { get; init; } = new();
    public NodeOutputSettings Output { get; init; } = new();
    public NodeCalibration Calibration { get; init; } = new();
    public bool TestPattern { get; init; }
}

/// <summary>A versioned content push: layouts, brand kit, storyboard and sponsor map. The node
/// stores it locally so it can keep rendering after the controller disappears. Asset bytes follow
/// the manifest as raw data, never as base64 inside this JSON.</summary>
public sealed record NodePackage
{
    public string PackageId { get; init; } = Guid.NewGuid().ToString("N");
    public int Version { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string CreatedBy { get; init; } = Environment.MachineName;
    public string Summary { get; init; } = "";
    public string CatalogJson { get; init; } = "";
    /// <summary>The venue rotation, schedules, sponsors and channels that travel with the layouts.</summary>
    public string VenueJson { get; init; } = "";
    /// <summary>Image, video and other media files the layouts or venue refer to.</summary>
    public List<NodePackageAsset> Assets { get; init; } = [];
    /// <summary>Referenced files that were already gone when the package was built.</summary>
    public List<string> MissingAssets { get; init; } = [];
}

/// <summary>One file carried by a node package. The file's bytes follow the package manifest as
/// raw data; the node verifies the hash before using it.</summary>
public sealed record NodePackageAsset
{
    public string SourcePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
}

/// <summary>What the Venue Nodes page shows before an operator sends a package.</summary>
public sealed record NodePackagePreview
{
    public int Version { get; init; }
    public string Summary { get; init; } = "";
    public int DisplayCount { get; init; }
    public int LayoutCount { get; init; }
    public int AssetCount { get; init; }
    public long AssetBytes { get; init; }
    public IReadOnlyList<NodePackageAsset> Assets { get; init; } = [];
    public IReadOnlyList<string> MissingAssets { get; init; } = [];
}

/// <summary>The outcome of a send from a layout, media item or content pack.</summary>
public sealed record NodeSendResult
{
    public int PackageVersion { get; init; }
    public int Sent { get; init; }
    public int Failed { get; init; }
    public int AssetCount { get; init; }
    public long AssetBytes { get; init; }
    public string Source { get; init; } = "";
    public string Error { get; init; } = "";
    public bool Ok => Failed == 0 && string.IsNullOrEmpty(Error);
    public string Message => !string.IsNullOrEmpty(Error)
        ? $"NODE PACKAGE · {Error}"
        : Sent == 0
            ? "NODE PACKAGE · no enabled nodes; local rendering is unchanged."
            : $"NODE PACKAGE {PackageVersion} · sent to {Sent} node{(Sent == 1 ? "" : "s")}"
              + (Failed > 0 ? $" · {Failed} failed" : "")
              + $" · {AssetCount} asset{(AssetCount == 1 ? "" : "s")} · {AssetBytes / 1024.0 / 1024.0:0.0} MB";
}

/// <summary>What a node broadcasts in response to a discovery probe.</summary>
public sealed record NodeDiscovery
{
    public string NodeId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Address { get; init; } = "";
    public int Port { get; init; }
    public NodeRole Role { get; init; }
    public string Version { get; init; } = "";
    public string DisplayId { get; init; } = "";
    public string SyncGroupId { get; init; } = "";
}
