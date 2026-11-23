using System.Windows.Threading;

namespace Scoreboard.Desktop;

/// <summary>
/// The show's one repeating timer. Everything that needs to wake up on a schedule — scheduled
/// automation rules, the triggers page, the status bar's show health — subscribes here instead of
/// creating its own timer, so the console has one clock rather than one per feature.
/// The tick runs on the UI thread; a subscriber must not block it.
/// </summary>
public sealed class ShowHeartbeat
{
    static ShowHeartbeat? _shared;

    /// <summary>The heartbeat shared by every part of the running application.</summary>
    public static ShowHeartbeat Shared => _shared ??= new ShowHeartbeat();

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    ShowHeartbeat()
    {
        _timer.Tick += (_, _) =>
        {
            Elapsed += _timer.Interval;
            Tick?.Invoke(Elapsed);
        };
    }

    /// <summary>Raised about ten times a second while at least one subscriber is attached.</summary>
    public event Action<TimeSpan>? Tick;

    /// <summary>Time since the heartbeat was first started.</summary>
    public TimeSpan Elapsed { get; private set; }

    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Starts the shared timer. Calling it again is harmless.</summary>
    public void Start()
    {
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>How many whole seconds the heartbeat has been running, for once-a-second work.</summary>
    public long Seconds => (long)Elapsed.TotalSeconds;
}
