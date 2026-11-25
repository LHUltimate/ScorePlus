using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scoreboard.Core;

/// <summary>What a playlist item puts on a display.</summary>
public enum VenueItemKind
{
    Layout,
    Media,
    Page
}

/// <summary>One item in a venue playlist. Layouts are self-contained; media is handed to the
/// desktop playback service when one is available, and a page is a named still/layout card.</summary>
public sealed class VenuePlaylistItem
{
    public VenueItemKind Kind { get; set; } = VenueItemKind.Layout;
    public string LayoutId { get; set; } = "";
    public string Label { get; set; } = "";
    public string MediaPath { get; set; } = "";
    public double Seconds { get; set; } = 10;
    public int Weight { get; set; } = 1;
    public string Sponsor { get; set; } = "";

    [JsonIgnore]
    public string DisplayLabel => !string.IsNullOrWhiteSpace(Label)
        ? Label
        : Kind switch
        {
            VenueItemKind.Media => Path.GetFileName(MediaPath),
            VenueItemKind.Page => "Page",
            _ => string.IsNullOrWhiteSpace(LayoutId) ? "Layout" : "Layout"
        };
}

/// <summary>An ordered/weighted list targeted at one display or every display.</summary>
public sealed class VenuePlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New playlist";
    public string TargetDisplayId { get; set; } = "";
    public bool Loop { get; set; } = true;
    /// <summary>0 loops forever; a positive number stops after that many complete passes.</summary>
    public int LoopCount { get; set; }
    public bool Shuffle { get; set; }
    public string ExpiresAt { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<VenuePlaylistItem> Items { get; set; } = [];

    [JsonIgnore]
    public string Summary => $"{Items.Count} item{(Items.Count == 1 ? "" : "s")} · {(Loop ? (LoopCount > 0 ? $"{LoopCount} loops" : "loop") : "once")}"
        + (TargetDisplayId.Length == 0 ? " · all displays" : " · one display");

    [JsonIgnore] public string DisplayName => Name;

    public VenuePlaylist Copy() => JsonSerializer.Deserialize<VenuePlaylist>(JsonSerializer.Serialize(this))!;
}

/// <summary>A schedule entry. Live data conditions use the same condition model as the show rules,
/// so a sponsor card can depend on score, period, weather (a custom value) or the clock.</summary>
public sealed class VenueSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New schedule";
    public string PlaylistId { get; set; } = "";
    public string DisplayId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public string Date { get; set; } = "";
    public string AtTime { get; set; } = "";
    public string EndsAt { get; set; } = "";
    public List<DayOfWeek> Days { get; set; } = [];
    public int EveryMinutes { get; set; }
    public List<AutomationCondition> Conditions { get; set; } = [];
    public bool MatchAny { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }

    [JsonIgnore] public string DisplayName => Name;

    [JsonIgnore]
    public string WhenLabel
    {
        get
        {
            var parts = new List<string>();
            if (Date.Length > 0) parts.Add(Date);
            if (AtTime.Length > 0) parts.Add(EndsAt.Length > 0 ? $"{AtTime}–{EndsAt}" : AtTime);
            if (Days.Count > 0) parts.Add(string.Join(" ", Days.Select(day => day.ToString()[..3])));
            if (EveryMinutes > 0) parts.Add($"every {EveryMinutes} min");
            return parts.Count == 0 ? "always / when conditions match" : string.Join(" · ", parts);
        }
    }

    [JsonIgnore]
    public string ConditionLabel => Conditions.Count == 0
        ? "no live condition"
        : string.Join(MatchAny ? " OR " : " AND ", Conditions.Select(condition =>
            $"{AutomationRules.FieldLabel(condition.Field)} {AutomationRules.OperatorLabel(condition.Operator)} "
            + $"{(AutomationRules.IsTextField(condition.Field) ? condition.Text : condition.Value.ToString("0.##", CultureInfo.InvariantCulture))}"));
}

/// <summary>A sold package slot. The item itself carries its sponsor name; this record is the
/// contract view used for inventory and proof-of-play totals.</summary>
public sealed class VenueSponsor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Sponsor";
    public string DisplayId { get; set; } = "";
    public string PlaylistId { get; set; } = "";
    public int Weight { get; set; } = 1;
    public string StartsOn { get; set; } = "";
    public string EndsOn { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>One line of proof: what played, where and for how long.</summary>
public sealed class VenueProof
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string PlaylistId { get; set; } = "";
    public string PlaylistName { get; set; } = "";
    public string Item { get; set; } = "";
    public string DisplayId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Sponsor { get; set; } = "";
    public string Trigger { get; set; } = "";
    public double Seconds { get; set; }
}

/// <summary>A camera + overlay turned into a local channel the venue's TVs can watch.</summary>
public sealed class VenueChannel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Concourse";
    public string DisplayId { get; set; } = "";
    public string LayoutId { get; set; } = "";
    public int CameraIndex { get; set; } = 1;
    public bool Enabled { get; set; } = true;
}

/// <summary>Everything the Venue window owns. Kept as one document so a venue can be copied to
/// another control machine as a single file.</summary>
public sealed class VenueState
{
    public int Schema { get; set; } = 1;
    public List<VenuePlaylist> Playlists { get; set; } = [];
    public List<VenueSchedule> Schedules { get; set; } = [];
    public List<VenueSponsor> Sponsors { get; set; } = [];
    public List<VenueChannel> Channels { get; set; } = [];
    public List<VenueProof> Proof { get; set; } = [];

    /// <summary>Optional playback nodes. An empty list is the normal single-machine setup: this
    /// PC renders every display and no network service is needed.</summary>
    public List<DisplayNode> Nodes { get; set; } = [];

    /// <summary>Frame-clock groups shared by nodes. A group is optional on one machine.</summary>
    public List<SyncGroup> SyncGroups { get; set; } = [];
}

/// <summary>Persistence for the venue document, with the same never-crash-a-show policy as the
/// rest of ScorePlus.</summary>
public sealed class VenueStore
{
    public string FilePath { get; }
    public VenueState State { get; private set; } = new();

    public VenueStore(string? filePath = null) => FilePath = filePath ?? AppPaths.Combine("venue.json");

    public static VenueStore Load(string? filePath = null)
    {
        var store = new VenueStore(filePath);
        try
        {
            if (File.Exists(store.FilePath))
            {
                var loaded = JsonSerializer.Deserialize<VenueState>(File.ReadAllText(store.FilePath));
                if (loaded is not null) store.State = loaded;
            }
        }
        catch
        {
            // A damaged venue document starts empty rather than taking the console down.
        }
        store.Normalize();
        return store;
    }

    public void Normalize()
    {
        State ??= new VenueState();
        State.Playlists ??= [];
        State.Schedules ??= [];
        State.Sponsors ??= [];
        State.Channels ??= [];
        State.Proof ??= [];
        State.Nodes ??= [];
        State.SyncGroups ??= [];
        State.Nodes = State.Nodes.Where(node => node is not null).Take(64).ToList();
        foreach (var node in State.Nodes) node.Normalize();
        State.SyncGroups = State.SyncGroups.Where(group => group is not null).Take(32).ToList();
        foreach (var group in State.SyncGroups) group.Normalize();
        foreach (var playlist in State.Playlists)
        {
            playlist.Id = string.IsNullOrWhiteSpace(playlist.Id) ? Guid.NewGuid().ToString("N") : playlist.Id.Trim();
            playlist.Name = string.IsNullOrWhiteSpace(playlist.Name) ? "Untitled playlist" : playlist.Name.Trim();
            playlist.Items ??= [];
            playlist.Items = playlist.Items.Where(item => item is not null).Take(200).ToList();
            playlist.LoopCount = Math.Clamp(playlist.LoopCount, 0, 1000);
            foreach (var item in playlist.Items)
            {
                item.LayoutId = (item.LayoutId ?? "").Trim();
                item.MediaPath = (item.MediaPath ?? "").Trim();
                item.Label = (item.Label ?? "").Trim();
                item.Sponsor = (item.Sponsor ?? "").Trim();
                item.Seconds = double.IsFinite(item.Seconds) ? Math.Clamp(item.Seconds, 1, 3600) : 10;
                item.Weight = Math.Clamp(item.Weight, 1, 100);
            }
        }
        foreach (var schedule in State.Schedules)
        {
            schedule.Id = string.IsNullOrWhiteSpace(schedule.Id) ? Guid.NewGuid().ToString("N") : schedule.Id.Trim();
            schedule.Name = string.IsNullOrWhiteSpace(schedule.Name) ? "Untitled schedule" : schedule.Name.Trim();
            schedule.AtTime = AutomationRules.NormalizeTime(schedule.AtTime);
            schedule.EndsAt = AutomationRules.NormalizeTime(schedule.EndsAt);
            schedule.Conditions ??= [];
            schedule.Conditions = schedule.Conditions.Where(condition => condition is not null).Take(24).ToList();
        }
        State.Proof = State.Proof.OrderByDescending(proof => proof.At).Take(10000).ToList();
    }

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
        }
        catch
        {
            // The window can still show the live state; only persistence is lost.
        }
    }

    public VenuePlaylist AddPlaylist(string? name = null)
    {
        var playlist = new VenuePlaylist { Name = string.IsNullOrWhiteSpace(name) ? $"Playlist {State.Playlists.Count + 1}" : name.Trim() };
        State.Playlists.Add(playlist);
        return playlist;
    }

    public VenueSchedule AddSchedule(string? name = null)
    {
        var schedule = new VenueSchedule { Name = string.IsNullOrWhiteSpace(name) ? $"Schedule {State.Schedules.Count + 1}" : name.Trim() };
        State.Schedules.Add(schedule);
        return schedule;
    }

    public DisplayNode AddNode(string? address = null, string? name = null)
    {
        var node = new DisplayNode
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Node {State.Nodes.Count + 1}" : name.Trim(),
            Address = string.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address.Trim()
        };
        if (State.SyncGroups.Count == 0) AddSyncGroup("Main sync");
        node.SyncGroupId = State.SyncGroups[0].Id;
        State.Nodes.Add(node);
        return node;
    }

    public SyncGroup AddSyncGroup(string? name = null)
    {
        var group = new SyncGroup
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Sync group {State.SyncGroups.Count + 1}" : name.Trim()
        };
        State.SyncGroups.Add(group);
        return group;
    }
}

/// <summary>
/// The unattended venue engine. It runs playlists, advances items on their own clocks, evaluates
/// conditional schedules against the live game and records proof of play. It is deliberately
/// headless so the same rules can power a single-machine high-school show and a larger venue's
/// concourse screens.
/// </summary>
public sealed class VenueEngine
{
    sealed class ActivePlay
    {
        public VenuePlaylist Playlist { get; }
        public int Index { get; set; }
        public int Loops { get; set; }
        public DateTimeOffset Started { get; set; }
        public string Trigger { get; init; } = "";
        public string TargetDisplayId { get; init; } = "";

        public ActivePlay(VenuePlaylist playlist, int index, DateTimeOffset started)
        {
            Playlist = playlist;
            Index = index;
            Started = started;
        }
    }

    readonly ScorebugCatalog _catalog;
    readonly GameEngine _engine;
    readonly VenueStore _store;
    ActivePlay? _active;
    readonly Random _random = new();

    public VenueEngine(ScorebugCatalog catalog, GameEngine engine, VenueStore? store = null)
    {
        _catalog = catalog;
        _engine = engine;
        _store = store ?? VenueStore.Load();
    }

    public VenueStore Store => _store;
    public VenueState State => _store.State;
    public VenuePlaylist? ActivePlaylist => _active?.Playlist;
    public int ActiveIndex => _active?.Index ?? -1;
    public VenuePlaylistItem? ActiveItem => _active is null ? null : _active.Playlist.Items.ElementAtOrDefault(_active.Index);
    public event Action<string>? Status;
    public event Action? Changed;

    /// <summary>Called for media items. Returning false leaves the proof line marked as a miss
    /// instead of pretending a clip played.</summary>
    public Func<VenuePlaylistItem, bool>? MediaPlayer { get; set; }

    public VenuePlaylist? FindPlaylist(string? id) => State.Playlists.FirstOrDefault(playlist =>
        string.Equals(playlist.Id, id, StringComparison.OrdinalIgnoreCase));

    public VenueSchedule? FindSchedule(string? id) => State.Schedules.FirstOrDefault(schedule =>
        string.Equals(schedule.Id, id, StringComparison.OrdinalIgnoreCase));

    public VenueChannel? FindChannel(string? id) => State.Channels.FirstOrDefault(channel =>
        string.Equals(channel.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<(ScorebugDisplay Display, ScorebugLayout? Layout)> Oversight() =>
        _catalog.Displays.Select(display => (display,
            display.Layouts.ElementAtOrDefault(Math.Clamp(display.PublishedIndex, 0, Math.Max(0, display.Layouts.Count - 1))))).ToArray();

    public bool IsDue(VenueSchedule schedule, DateTimeOffset now, GameState? game = null)
    {
        if (!schedule.Enabled || FindPlaylist(schedule.PlaylistId) is null) return false;
        if (!string.IsNullOrWhiteSpace(schedule.Date)
            && !string.Equals(schedule.Date.Trim(), now.LocalDateTime.ToString("yyyy-MM-dd"), StringComparison.Ordinal))
            return false;
        if (schedule.Days.Count > 0 && !schedule.Days.Contains(now.LocalDateTime.DayOfWeek)) return false;
        if (schedule.AtTime.Length > 0 && !InTimeWindow(schedule.AtTime, schedule.EndsAt, now.LocalDateTime)) return false;
        if (!ConditionsMatch(schedule, game ?? _engine.State)) return false;
        if (schedule.LastRunAt is { } last)
        {
            var gap = schedule.EveryMinutes > 0 ? TimeSpan.FromMinutes(schedule.EveryMinutes) : TimeSpan.FromSeconds(2);
            if (now - last < gap) return false;
        }
        return true;
    }

    static bool InTimeWindow(string start, string end, DateTime now)
    {
        if (!TryMinutes(start, out var from)) return true;
        if (!TryMinutes(end, out var to)) to = from;
        var at = now.Hour * 60 + now.Minute;
        // A window such as 22:00–01:00 wraps over midnight.
        return from <= to ? at >= from && at <= to : at >= from || at <= to;
    }

    static bool TryMinutes(string text, out int minutes)
    {
        minutes = 0;
        var normalized = AutomationRules.NormalizeTime(text);
        if (normalized.Length == 0) return false;
        var parts = normalized.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var hours) || !int.TryParse(parts[1], out var mins)) return false;
        minutes = hours * 60 + mins;
        return true;
    }

    public bool ConditionsMatch(VenueSchedule schedule, GameState state)
    {
        var conditions = (schedule.Conditions ?? []).Where(condition => condition is not null).ToArray();
        if (conditions.Length == 0) return true;
        return schedule.MatchAny ? conditions.Any(condition => condition.Matches(state))
            : conditions.All(condition => condition.Matches(state));
    }

    /// <summary>Starts a playlist immediately and publishes its first item.</summary>
    public bool BeginPlaylist(string? playlistId, DateTimeOffset now, string trigger = "manual", string? displayId = null)
    {
        var playlist = FindPlaylist(playlistId);
        if (playlist is null || !playlist.Enabled || playlist.Items.Count == 0) return false;
        if (Expired(playlist, now)) return false;
        _active = new ActivePlay(playlist, FirstIndex(playlist), now)
        {
            Trigger = trigger,
            TargetDisplayId = string.IsNullOrWhiteSpace(displayId) ? playlist.TargetDisplayId : displayId
        };
        PublishActive(now, trigger);
        return true;
    }

    /// <summary>Runs a playlist once, without making it the active rotation.</summary>
    public bool RunOnce(string? playlistId, DateTimeOffset now, string trigger = "manual")
    {
        var playlist = FindPlaylist(playlistId);
        if (playlist is null || playlist.Items.Count == 0 || Expired(playlist, now)) return false;
        foreach (var item in playlist.Items)
            Publish(playlist, item, now, trigger, 0);
        return true;
    }

    public void StopPlaylist()
    {
        _active = null;
        Status?.Invoke("Venue playlist stopped.");
        Changed?.Invoke();
    }

    /// <summary>One tick of the shared show clock: advance the active item and start any due schedule.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (_active is not null)
        {
            var item = ActiveItem;
            if (item is null) StopPlaylist();
            else if ((now - _active.Started).TotalSeconds >= Math.Max(1, item.Seconds))
            {
                Advance(now);
            }
        }

        // A single rotation is enough per tick. Due entries are ordered by priority, then by name so
        // two sponsors with the same priority do not starve each other.
        var due = State.Schedules
            .Where(schedule => schedule.Enabled && IsDue(schedule, now))
            .OrderByDescending(schedule => schedule.Priority)
            .ThenBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (due is null) return;
        due.LastRunAt = now;
        _store.Save();
        BeginPlaylist(due.PlaylistId, now, $"schedule · {due.Name}", due.DisplayId);
        Changed?.Invoke();
    }

    void Advance(DateTimeOffset now)
    {
        if (_active is null) return;
        if (_active.Playlist.Shuffle && _active.Playlist.Items.Count > 1)
        {
            // Weighted rotation: a sponsor can buy more appearances without duplicating its item.
            _active.Index = WeightedNext(_active.Playlist, _active.Index);
            _active.Started = now;
            PublishActive(now, _active.Trigger);
            return;
        }
        var next = _active.Index + 1;
        if (next >= _active.Playlist.Items.Count)
        {
            if (!_active.Playlist.Loop)
            {
                StopPlaylist();
                return;
            }
            _active.Loops++;
            if (_active.Playlist.LoopCount > 0 && _active.Loops >= _active.Playlist.LoopCount)
            {
                StopPlaylist();
                return;
            }
            next = 0;
        }
        _active.Index = next;
        _active.Started = now;
        PublishActive(now, _active.Trigger);
    }

    void PublishActive(DateTimeOffset now, string trigger)
    {
        if (_active is null || ActiveItem is not { } item) return;
        Publish(_active.Playlist, item, now, trigger, _active.Index, _active.TargetDisplayId);
        Changed?.Invoke();
    }

    void Publish(VenuePlaylist playlist, VenuePlaylistItem item, DateTimeOffset now, string trigger, int index,
        string? targetDisplayId = null)
    {
        var displays = TargetDisplays(string.IsNullOrWhiteSpace(targetDisplayId) ? playlist.TargetDisplayId : targetDisplayId);
        var published = false;
        foreach (var display in displays)
        {
            var layoutIndex = FindLayoutIndex(display, item.LayoutId);
            if (item.Kind == VenueItemKind.Layout && layoutIndex < 0) continue;
            if (item.Kind == VenueItemKind.Media)
            {
                var played = MediaPlayer?.Invoke(item) == true;
                Record(playlist, item, display, now, played ? item.Seconds : 0,
                    played ? trigger : $"{trigger} · not played");
                if (played) published = true;
                continue;
            }
            // Publish on the target display without taking over the operator's staged display.
            // A page behaves as a layout card; an already-published layout still gets its proof
            // line, but does not disturb the console's active edit target.
            if (layoutIndex >= 0 && display.PublishedIndex != layoutIndex)
                _catalog.PublishLayout(display.Id, layoutIndex);
            Record(playlist, item, display, now, item.Seconds, trigger);
            published = true;
        }
        // Proof must survive a power cut, not only a clean window close.
        _store.Save();
    }

    void Record(VenuePlaylist playlist, VenuePlaylistItem item, ScorebugDisplay? display, DateTimeOffset now,
        double seconds, string trigger)
    {
        State.Proof.Insert(0, new VenueProof
        {
            At = now, PlaylistId = playlist.Id, PlaylistName = playlist.Name,
            Item = item.DisplayLabel, DisplayId = display?.Id ?? "", DisplayName = display?.Name ?? "All displays",
            Sponsor = item.Sponsor, Trigger = trigger, Seconds = seconds
        });
        if (State.Proof.Count > 10000) State.Proof.RemoveRange(10000, State.Proof.Count - 10000);
        Status?.Invoke($"VENUE · {playlist.Name} · {item.DisplayLabel} · {display?.Name ?? "all displays"} · {trigger}");
    }

    IReadOnlyList<ScorebugDisplay> TargetDisplays(string? displayId)
    {
        if (!string.IsNullOrWhiteSpace(displayId) && _catalog.FindDisplay(displayId) is { } one) return [one];
        return _catalog.Displays.ToArray();
    }

    static int FindLayoutIndex(ScorebugDisplay display, string? layoutId) =>
        display.Layouts.FindIndex(layout => string.Equals(layout.Id, layoutId, StringComparison.OrdinalIgnoreCase));

    int WeightedNext(VenuePlaylist playlist, int current)
    {
        var total = playlist.Items.Sum(item => Math.Max(1, item.Weight));
        // With more than one item, avoid repeating the item that just played. This makes the
        // weights meaningful in a small rotation instead of a coin landing on the same sponsor.
        var excluded = playlist.Items.Count > 1 ? current : -1;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var pick = _random.Next(total);
            var index = 0;
            while (index < playlist.Items.Count)
            {
                pick -= Math.Max(1, playlist.Items[index].Weight);
                if (pick < 0)
                {
                    if (index != excluded) return index;
                    break;
                }
                index++;
            }
        }
        return (current + 1) % playlist.Items.Count;
    }

    int FirstIndex(VenuePlaylist playlist)
    {
        if (!playlist.Shuffle || playlist.Items.Count < 2) return 0;
        var total = playlist.Items.Sum(item => Math.Max(1, item.Weight));
        var pick = _random.Next(total);
        for (var index = 0; index < playlist.Items.Count; index++)
        {
            pick -= Math.Max(1, playlist.Items[index].Weight);
            if (pick < 0) return index;
        }
        return 0;
    }

    static bool Expired(VenuePlaylist playlist, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(playlist.ExpiresAt)) return false;
        return DateTimeOffset.TryParse(playlist.ExpiresAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var expiry) && now > expiry;
    }

    /// <summary>CSV for a sponsor package or an end-of-game room report.</summary>
    public string ProofCsv()
    {
        var csv = new StringBuilder("when,playlist,item,display,sponsor,seconds,trigger\n");
        foreach (var proof in State.Proof)
        {
            csv.Append(Escape(proof.At.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
                .Append(Escape(proof.PlaylistName)).Append(',')
                .Append(Escape(proof.Item)).Append(',')
                .Append(Escape(proof.DisplayName)).Append(',')
                .Append(Escape(proof.Sponsor)).Append(',')
                .Append(proof.Seconds.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                .Append(Escape(proof.Trigger)).Append('\n');
        }
        return csv.ToString();
    }

    static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    public IReadOnlyList<(string Sponsor, int Plays, double Seconds, DateTimeOffset? Last)> SponsorTotals() =>
        State.Proof.Where(proof => !string.IsNullOrWhiteSpace(proof.Sponsor))
            .GroupBy(proof => proof.Sponsor.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.Key, group.Count(), group.Sum(proof => proof.Seconds),
                (DateTimeOffset?)group.Max(proof => proof.At)))
            .OrderByDescending(row => row.Item2)
            .ToArray();

    public void ClearProof()
    {
        State.Proof.Clear();
        _store.Save();
        Changed?.Invoke();
    }
}
