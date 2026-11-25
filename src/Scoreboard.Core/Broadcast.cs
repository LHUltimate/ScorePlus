using System.Text.Json.Serialization;

namespace Scoreboard.Core;

/// <summary>One broadcast raster the operator can choose for a layout or an output.</summary>
public sealed record BroadcastStandard(string Key, string Label, int Width, int Height, int RefreshHz);

/// <summary>The standards offered by the broadcast panels. They are processing formats, not a
/// claim that the attached display is running at that refresh rate.</summary>
public static class BroadcastStandards
{
    public static readonly BroadcastStandard[] All =
    [
        new("1080p60", "Full HD · 1080p60", 1920, 1080, 60),
        new("1080p50", "Full HD · 1080p50", 1920, 1080, 50),
        new("720p60", "HD · 720p60", 1280, 720, 60),
        new("720p50", "HD · 720p50", 1280, 720, 50),
        new("2160p60", "UHD · 2160p60", 3840, 2160, 60)
    ];

    public static BroadcastStandard Resolve(string? key) =>
        All.FirstOrDefault(standard => string.Equals(standard.Key, (key ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
        ?? All[0];

    public static string Normalize(string? key) => Resolve(key).Key;

    public static IEnumerable<string> Keys => All.Select(standard => standard.Key);

    public static IEnumerable<string> Labels => All.Select(standard => standard.Label);
}

/// <summary>
/// A display's broadcast channel plan. It declares the raster, safe areas and which graphic stacks
/// travel together when that display feeds a router fill/key or a stream overlay. Normalization keeps
/// older shows safe: a missing channel becomes the 1080p60 default without changing the artwork.
/// </summary>
public sealed class BroadcastProfile
{
    public string Standard { get; set; } = "1080p60";
    public double SafeActionPercent { get; set; } = 90;
    public double SafeTitlePercent { get; set; } = 80;
    public bool FillKey { get; set; } = true;
    public bool LowerThirdStack { get; set; } = true;
    public bool TickerStack { get; set; } = true;
    public string KeyColor { get; set; } = "#FF00B140";

    [JsonIgnore]
    public BroadcastStandard Raster => BroadcastStandards.Resolve(Standard);

    [JsonIgnore]
    public string Summary => $"{Raster.Label} · safe {Math.Round(SafeActionPercent)}/{Math.Round(SafeTitlePercent)}%"
        + (FillKey ? " · fill + key" : " · program only");

    public void Normalize()
    {
        Standard = BroadcastStandards.Normalize(Standard);
        SafeActionPercent = Math.Clamp(double.IsFinite(SafeActionPercent) ? SafeActionPercent : 90, 50, 100);
        SafeTitlePercent = Math.Clamp(double.IsFinite(SafeTitlePercent) ? SafeTitlePercent : 80, 40, SafeActionPercent);
        KeyColor = NormalizeColor(KeyColor);
    }

    public BroadcastProfile Copy() => new()
    {
        Standard = Standard,
        SafeActionPercent = SafeActionPercent,
        SafeTitlePercent = SafeTitlePercent,
        FillKey = FillKey,
        LowerThirdStack = LowerThirdStack,
        TickerStack = TickerStack,
        KeyColor = KeyColor
    };

    static string NormalizeColor(string? text)
    {
        var value = (text ?? "").Trim();
        return value.Length is 7 or 9 && value[0] == '#' && value[1..].All(char.IsAsciiHexDigit)
            ? value.ToUpperInvariant()
            : "#FF00B140";
    }
}

/// <summary>What a saved broadcast destination carries.</summary>
public enum BroadcastDestinationKind
{
    Program,
    FillKey,
    Stream
}

/// <summary>
/// A saved place the programme can be sent. A Stream destination is a browser/encoder source; a
/// FillKey destination exposes separate fill and key pictures for a router or capture device. The
/// runtime fields are deliberately not persisted.
/// </summary>
public sealed class BroadcastDestination
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Broadcast output";
    public BroadcastDestinationKind Kind { get; set; } = BroadcastDestinationKind.Stream;
    /// <summary>The display this channel carries. The channel follows that display's published
    /// layout, so a layout switch never re-points an output.</summary>
    public string DisplayId { get; set; } = "";
    public string Standard { get; set; } = "1080p60";
    public bool Scorebug { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public string Notes { get; set; } = "";
    /// <summary>Optional third-party provider name for a stream destination, e.g. YouTube.</summary>
    public string Provider { get; set; } = "Custom RTMP";
    /// <summary>The provider ingest URL, without the stream key.</summary>
    public string ServerUrl { get; set; } = "";
    /// <summary>The provider stream key. It is saved with the venue so a restart keeps working.</summary>
    public string StreamKey { get; set; } = "";

    [JsonIgnore] public bool IsLive { get; set; }
    [JsonIgnore] public int Viewers { get; set; }
    [JsonIgnore] public string LastError { get; set; } = "";
    [JsonIgnore] public string Url { get; set; } = "";

    [JsonIgnore] public BroadcastStandard Raster => BroadcastStandards.Resolve(Standard);
    [JsonIgnore] public string KindLabel => Kind switch
    {
        BroadcastDestinationKind.FillKey => "FILL + KEY",
        BroadcastDestinationKind.Program => "PROGRAM",
        _ => "STREAM"
    };
    [JsonIgnore] public string StateLabel => !Enabled ? "OFF"
        : !string.IsNullOrWhiteSpace(LastError) ? "ERROR"
        : IsLive ? $"LIVE · {Viewers} viewer{(Viewers == 1 ? "" : "s")}" : "READY";
    [JsonIgnore] public string Summary => $"{KindLabel} · {Raster.Label}"
        + (Scorebug ? " · scorebug" : " · clean")
        + (string.IsNullOrWhiteSpace(Notes) ? "" : $" · {Notes}");

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Name = string.IsNullOrWhiteSpace(Name) ? "Broadcast output" : Name.Trim();
        if (!Enum.IsDefined(Kind)) Kind = BroadcastDestinationKind.Stream;
        Standard = BroadcastStandards.Normalize(Standard);
        DisplayId = (DisplayId ?? "").Trim();
        Notes = (Notes ?? "").Trim();
        Provider = string.IsNullOrWhiteSpace(Provider) ? "Custom RTMP" : Provider.Trim();
        ServerUrl = (ServerUrl ?? "").Trim();
        StreamKey = (StreamKey ?? "").Trim();
    }

    public BroadcastDestination Copy() => new()
    {
        Id = Id, Name = Name, Kind = Kind, DisplayId = DisplayId, Standard = Standard,
        Scorebug = Scorebug, Enabled = Enabled, Notes = Notes,
        Provider = Provider, ServerUrl = ServerUrl, StreamKey = StreamKey
    };
}

/// <summary>The venue's broadcast plan: destinations and the stream overlay choices.</summary>
public sealed class BroadcastSettings
{
    public List<BroadcastDestination> Destinations { get; set; } =
    [
        new() { Id = "stream-main", Name = "Main stream", Kind = BroadcastDestinationKind.Stream, Standard = "720p60" },
        new() { Id = "router-fill-key", Name = "Router fill + key", Kind = BroadcastDestinationKind.FillKey }
    ];

    /// <summary>True puts the published scorebug over the stream picture.</summary>
    public bool StreamScorebug { get; set; } = true;

    /// <summary>A layout pinned for streaming; empty follows the display's on-air layout.</summary>
    public string StreamLayoutId { get; set; } = "";

    /// <summary>A layout pinned for the graphics output; empty follows the published layout.</summary>
    public string GraphicsLayoutId { get; set; } = "";

    /// <summary>Where the local fill/key and stream headend listens. 0 lets the app pick a free port.</summary>
    public int PreferredPort { get; set; } = 8100;

    public string GenlockSource { get; set; } = "System clock";

    public void Normalize()
    {
        Destinations ??= [];
        Destinations = Destinations.Where(destination => destination is not null).Take(24).ToList();
        foreach (var destination in Destinations) destination.Normalize();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in Destinations)
            if (!used.Add(destination.Id)) destination.Id = Guid.NewGuid().ToString("N");
        StreamLayoutId = (StreamLayoutId ?? "").Trim();
        GraphicsLayoutId = (GraphicsLayoutId ?? "").Trim();
        PreferredPort = PreferredPort <= 0 ? 0 : Math.Clamp(PreferredPort, 1024, 65535);
        GenlockSource = string.IsNullOrWhiteSpace(GenlockSource) ? "System clock" : GenlockSource.Trim();
    }

    public BroadcastSettings Copy() => new()
    {
        Destinations = Destinations.Select(destination => destination.Copy()).ToList(),
        StreamScorebug = StreamScorebug,
        StreamLayoutId = StreamLayoutId,
        GraphicsLayoutId = GraphicsLayoutId,
        PreferredPort = PreferredPort,
        GenlockSource = GenlockSource
    };
}

/// <summary>
/// The operator's marked replay range and its searchable metadata. Times are measured back from
/// the live edge in seconds, which is the same unit the replay booth already scrubs in.
/// </summary>
public sealed class ReplaySelection
{
    public int CameraIndex { get; set; }
    public double MarkInBack { get; set; }
    public double MarkOutBack { get; set; }
    public int Rating { get; set; } = 3;
    public List<string> Tags { get; set; } = [MediaTags.Replay];
    public string GameClock { get; set; } = "";
    public string Period { get; set; } = "";
    public string Score { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public const string DefaultTag = "REPLAY";
    public const string HighlightTag = "HIGHLIGHT";

    [JsonIgnore] public double Duration => Math.Max(0, MarkInBack - MarkOutBack);
    [JsonIgnore] public bool HasRange => Duration >= 0.1;
    [JsonIgnore] public string TagsText => string.Join(", ", Tags);
    [JsonIgnore] public string RatingStars => new string('★', Rating) + new string('☆', 5 - Rating);

    [JsonIgnore]
    public string Summary => HasRange
        ? $"{Duration:0.0}s · {RatingStars}" + (Tags.Count == 0 ? "" : $" · {TagsText}")
        : "No range marked";

    public void Normalize(double depth)
    {
        depth = Math.Max(0, double.IsFinite(depth) ? depth : 0);
        MarkInBack = Math.Clamp(double.IsFinite(MarkInBack) ? MarkInBack : 0, 0, depth);
        MarkOutBack = Math.Clamp(double.IsFinite(MarkOutBack) ? MarkOutBack : 0, 0, depth);
        if (MarkInBack < MarkOutBack) (MarkInBack, MarkOutBack) = (MarkOutBack, MarkInBack);
        Rating = Math.Clamp(Rating, 1, 5);
        Tags ??= [];
        Tags = Tags.Select(tag => (tag ?? "").Trim()).Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        if (Tags.Count == 0) Tags.Add(DefaultTag);
        GameClock = (GameClock ?? "").Trim();
        Period = (Period ?? "").Trim();
        Score = (Score ?? "").Trim();
        CameraIndex = Math.Max(0, CameraIndex);
    }

    public void SetTags(string? text)
    {
        Tags = (text ?? "").Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        if (Tags.Count == 0) Tags.Add(DefaultTag);
    }

    public ReplaySelection Copy() => new()
    {
        CameraIndex = CameraIndex, MarkInBack = MarkInBack, MarkOutBack = MarkOutBack,
        Rating = Rating, Tags = Tags.ToList(), GameClock = GameClock, Period = Period,
        Score = Score, CreatedAt = CreatedAt
    };

    static class MediaTags
    {
        public const string Replay = "REPLAY";
    }
}
