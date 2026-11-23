namespace Scoreboard.Core;

/// <summary>
/// What a show event is about. The kind is deliberately coarse: a consumer that wants the detail
/// reads <see cref="ShowEvent.Type"/> and <see cref="ShowEvent.Description"/>, while a consumer that
/// only needs to know "the score moved" can switch on the kind and ignore everything else.
/// </summary>
public enum ShowEventKind
{
    System,
    Score,
    Clock,
    Period,
    Possession,
    Penalty,
    Media,
    Layout,
    Macro,
    Roster,
    Game,
    Ticker,
    Automation,
    Device
}

/// <summary>
/// One thing that happened in the show, in the order it happened. <see cref="Sequence"/> only ever
/// grows, so a consumer can order two events that arrived on different threads.
/// </summary>
public sealed record ShowEvent(
    long Sequence,
    DateTimeOffset At,
    ShowEventKind Kind,
    string Type,
    string Description)
{
    public override string ToString() => $"{At:HH:mm:ss.fff}  {Kind,-10} {Description}";
}

/// <summary>
/// The show spine: one ordered stream that every part of the application publishes to and every
/// consumer subscribes to. It keeps the last <see cref="Capacity"/> events for the log and the
/// triggers page; the event is raised outside the lock, so a handler can publish in response to an
/// event without deadlocking the bus.
/// </summary>
public sealed class ShowEventBus
{
    public const int Capacity = 400;

    readonly object _gate = new();
    readonly Queue<ShowEvent> _recent = new();
    long _sequence;
    long _count;

    /// <summary>Raised once per published event, on the thread that published it.</summary>
    public event Action<ShowEvent>? Published;

    /// <summary>How many events have been published since the process started.</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>The sequence number of the newest event, or zero before the first one.</summary>
    public long LastSequence => Interlocked.Read(ref _sequence);

    /// <summary>The newest event, or null before the first one.</summary>
    public ShowEvent? Last { get; private set; }

    /// <summary>Subscribes for the lifetime of the returned token.</summary>
    public IDisposable Subscribe(Action<ShowEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Published += handler;
        return new Subscription(this, handler);
    }

    /// <summary>Publishes one event and returns it, sequence stamped.</summary>
    public ShowEvent Publish(ShowEventKind kind, string type, string description)
    {
        var cleanType = (type ?? "").Trim();
        var cleanDescription = (description ?? "").Trim();
        if (cleanDescription.Length == 0) cleanDescription = cleanType.Length == 0 ? "Event" : cleanType;

        ShowEvent showEvent;
        Action<ShowEvent>? handlers;
        lock (_gate)
        {
            var sequence = ++_sequence;
            showEvent = new ShowEvent(sequence, DateTimeOffset.Now, kind, cleanType, cleanDescription);
            _recent.Enqueue(showEvent);
            while (_recent.Count > Capacity) _recent.Dequeue();
            Last = showEvent;
            Interlocked.Increment(ref _count);
            handlers = Published;
        }
        // Handlers run outside the lock: bus publication is the one thing that must never re-enter.
        handlers?.Invoke(showEvent);
        return showEvent;
    }

    /// <summary>The recent events, oldest first, limited to <paramref name="max"/> newest entries.</summary>
    public IReadOnlyList<ShowEvent> Recent(int max = 100)
    {
        lock (_gate)
        {
            var take = max <= 0 ? 0 : Math.Min(max, _recent.Count);
            return _recent.Skip(_recent.Count - take).ToArray();
        }
    }

    sealed class Subscription : IDisposable
    {
        readonly ShowEventBus _bus;
        Action<ShowEvent>? _handler;

        public Subscription(ShowEventBus bus, Action<ShowEvent> handler)
        {
            _bus = bus;
            _handler = handler;
        }

        public void Dispose()
        {
            var handler = Interlocked.Exchange(ref _handler, null);
            if (handler is not null) _bus.Published -= handler;
        }
    }
}

/// <summary>The one bus the running show publishes to. Tests can use their own instances.</summary>
public static class ShowBus
{
    public static ShowEventBus Shared { get; } = new();
}

/// <summary>Maps the log's short type words onto the spine's coarse kinds.</summary>
public static class ShowEventKinds
{
    public static ShowEventKind FromType(string? type) =>
        (type ?? "").Trim().ToLowerInvariant() switch
        {
            "score" => ShowEventKind.Score,
            "clock" => ShowEventKind.Clock,
            "period" => ShowEventKind.Period,
            "possession" => ShowEventKind.Possession,
            "penalty" or "flag" => ShowEventKind.Penalty,
            "media" => ShowEventKind.Media,
            "layout" => ShowEventKind.Layout,
            "macro" => ShowEventKind.Macro,
            "roster" => ShowEventKind.Roster,
            "game" => ShowEventKind.Game,
            "ticker" => ShowEventKind.Ticker,
            "automation" or "trigger" => ShowEventKind.Automation,
            "device" or "hardware" => ShowEventKind.Device,
            _ => ShowEventKind.System
        };
}

/// <summary>
/// The single condition evaluator. Automation rules, the triggers page and (in later stages) cues,
/// elements, schedules and device actions all ask it the same question, so no two features can
/// disagree about whether a condition is met.
/// </summary>
public static class TriggerEvaluator
{
    /// <summary>
    /// <paramref name="HasWait"/> says the rule has wait-until conditions, <paramref name="Trigger"/>
    /// says its ordinary conditions match, and <paramref name="WaitMet"/> says the wait conditions
    /// are satisfied. The phase machine that decides when to run an action stays with the caller.
    /// </summary>
    public readonly record struct Result(bool HasWait, bool Trigger, bool WaitMet);

    public static Result Evaluate(AutomationRule? rule, GameState? state)
    {
        if (rule is null || state is null) return new Result(false, false, false);
        var hasWait = rule.Conditions?.Any(condition => condition.WaitUntil) == true;
        var trigger = rule.Enabled && rule.Matches(state);
        var waitMet = hasWait && rule.MatchesWait(state);
        return new Result(hasWait, trigger, waitMet);
    }

    /// <summary>True when this rule is matching right now.</summary>
    public static bool Matches(AutomationRule? rule, GameState? state) => Evaluate(rule, state).Trigger;

    /// <summary>The ids of every rule that is matching right now, in list order.</summary>
    public static IReadOnlyList<string> Active(IEnumerable<AutomationRule>? rules, GameState? state) =>
        (rules ?? []).Where(rule => rule is not null && Evaluate(rule, state).Trigger)
            .Select(rule => rule.Id).ToArray();
}

/// <summary>
/// Versions and migration for the files a show is made of. A file keeps the shape it was written
/// with and is migrated on read, so a b1.2 game still opens after the spine lands, and a later stage
/// can add fields without a second reader.
/// </summary>
public static class ShowSchema
{
    /// <summary>
    /// The current shape of a .spg file. Version 1 files predate the show spine; version 2 added
    /// the spine's repair pass; version 3 adds the run of show, built from the macro board.
    /// </summary>
    public const int CurrentGameFileVersion = 3;

    /// <summary>
    /// Brings a game file up to the current shape: rule and macro identities are filled in, null
    /// lists are given empty ones, and the version is stamped. Returns true when anything moved,
    /// which is what a caller needs to know to offer the file for saving again.
    /// </summary>
    public static bool Migrate(GameFileDocument document)
    {
        if (document is null) return false;
        var changed = document.Version < CurrentGameFileVersion;
        var fromVersion = document.Version;

        document.Game ??= new GameState();
        document.Game.Stats ??= [];
        document.Game.Macros ??= [];
        document.Game.Cues ??= [];

        foreach (var macro in document.Game.Macros.Where(macro => macro is not null))
        {
            var before = (macro.Id, macro.Label, macro.Page, macro.Actions?.Count ?? 0);
            macro.Normalize();
            if (before != (macro.Id, macro.Label, macro.Page, macro.Actions?.Count ?? 0)) changed = true;
        }

        // A game written before the run of show gets one cue per macro button, in board order. The
        // cues point at the macros rather than copying their steps, so the palette stays the single
        // step editor. A deliberately emptied list on a current file is never rebuilt.
        if (fromVersion < 3 && document.Game.Cues.Count == 0 && document.Game.Macros.Count > 0)
        {
            document.Game.Cues = CueLists.FromMacros(document.Game.Macros);
            document.Game.CueIndex = 0;
            changed = true;
        }
        CueLists.Normalize(document.Game);

        if (MigrateDesign(document.Design)) changed = true;

        if (document.Version != CurrentGameFileVersion)
        {
            document.Version = CurrentGameFileVersion;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Normalizes the design half: every display keeps at least one layout, and every rule gets a
    /// stable id and a name. Returns true when a rule or layout had to be repaired.
    /// </summary>
    public static bool MigrateDesign(ScorebugDocument? design)
    {
        if (design?.Displays is null) return false;
        var changed = false;
        foreach (var display in design.Displays.Where(display => display is not null))
        {
            display.Layouts ??= [];
            if (display.Layouts.Count == 0)
            {
                display.Layouts.Add(new ScorebugLayout());
                changed = true;
            }
            display.ActiveIndex = Math.Clamp(display.ActiveIndex, 0, display.Layouts.Count - 1);
            display.PublishedIndex = Math.Clamp(display.PublishedIndex, 0, display.Layouts.Count - 1);
            display.Automations = AutomationRules.Normalize(display.Automations);
            foreach (var layout in display.Layouts)
            {
                if (!string.IsNullOrWhiteSpace(layout.Id)) continue;
                layout.Id = Guid.NewGuid().ToString("N");
                changed = true;
            }
        }
        return changed;
    }
}
