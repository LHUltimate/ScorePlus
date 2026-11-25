using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// Where one Camera Switch input goes when it is taken, and the tags it carries. A camera can be
/// routed to several displays, several venue channels, or all of either, and it can carry a list
/// of tags so several scorebug video boxes show it at once.
/// </summary>
public sealed class CameraRoute
{
    public int CameraIndex { get; set; }

    public bool AllDisplays { get; set; }
    public List<string> Displays { get; set; } = [];

    public bool AllChannels { get; set; }
    public List<string> Channels { get; set; } = [];

    public bool AllTags { get; set; }
    public List<string> Tags { get; set; } = [];

    public bool RoutesToDisplays => AllDisplays || Displays.Count > 0;
    public bool RoutesToChannels => AllChannels || Channels.Count > 0;
    public bool HasTags => AllTags || Tags.Count > 0;

    /// <summary>True when the operator chose anything at all. An empty route means "no routing".</summary>
    public bool IsConfigured => RoutesToDisplays || RoutesToChannels || HasTags;

    /// <summary>
    /// The tag a take should carry, or null to keep the camera's own library tag. GLOBAL is the
    /// catch-all that every video box shows.
    /// </summary>
    public string? TagOverride => AllTags ? MediaLibrary.AnyTag
        : Tags.Count == 0 ? null
        : MediaLibrary.NormalizeTag(string.Join(", ", Tags));

    public bool MatchesDisplay(string? id) =>
        AllDisplays || (id is not null && Displays.Contains(id, StringComparer.OrdinalIgnoreCase));

    public bool MatchesChannel(string? id) =>
        AllChannels || (id is not null && Channels.Contains(id, StringComparer.OrdinalIgnoreCase));

    /// <summary>A short line for the switch panel and the camera tile tooltip.</summary>
    public string Summary(int displayCount, int channelCount)
    {
        var parts = new List<string>();
        if (AllDisplays) parts.Add("all displays");
        else if (Displays.Count > 0)
            parts.Add(displayCount > 0 && Displays.Count >= displayCount ? "all displays" : $"{Displays.Count} display{(Displays.Count == 1 ? "" : "s")}");
        else parts.Add("no displays");

        if (AllChannels) parts.Add("all channels");
        else if (Channels.Count > 0)
            parts.Add(channelCount > 0 && Channels.Count >= channelCount ? "all channels" : $"{Channels.Count} channel{(Channels.Count == 1 ? "" : "s")}");
        else parts.Add("no channels");

        parts.Add(HasTags ? $"tags {TagOverride}" : "own tag");
        return string.Join(" · ", parts);
    }

    public CameraRoute Copy() => new()
    {
        CameraIndex = CameraIndex,
        AllDisplays = AllDisplays,
        Displays = [.. Displays],
        AllChannels = AllChannels,
        Channels = [.. Channels],
        AllTags = AllTags,
        Tags = [.. Tags]
    };

    /// <summary>Reads a hand-edited preference file without letting null lists or duplicates in.</summary>
    public void Normalize()
    {
        Displays ??= [];
        Channels ??= [];
        Tags ??= [];
        Displays = Displays.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Channels = Channels.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Tags = Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(MediaLibrary.NormalizeTag)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// The live camera routing of the audience outputs. Camera Switch sets this when a camera is
/// taken; every OutputWindow looks up its own display and paints that camera full-frame until the
/// switch returns to the published layout.
/// </summary>
public static class CameraRouting
{
    static readonly Dictionary<string, int> Cameras = new(StringComparer.OrdinalIgnoreCase);

    public static event Action? Changed;

    public static IReadOnlyDictionary<string, int> Active => Cameras;

    public static bool HasAny => Cameras.Count > 0;

    public static int? CameraFor(string? displayId) =>
        displayId is not null && Cameras.TryGetValue(displayId, out var index) ? index : null;

    /// <summary>Sends one camera to the named displays. An empty list returns them to the layout.</summary>
    public static void Set(IEnumerable<string> displayIds, int cameraIndex)
    {
        var next = displayIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (next.Length == 0) { Clear(); return; }
        Cameras.Clear();
        foreach (var id in next) Cameras[id] = Math.Max(0, cameraIndex);
        Changed?.Invoke();
    }

    public static void Clear()
    {
        if (Cameras.Count == 0) return;
        Cameras.Clear();
        Changed?.Invoke();
    }
}
