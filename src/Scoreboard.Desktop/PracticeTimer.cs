using System.Windows.Threading;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// Runs a practice plan: a list of named segments, each with its own countdown. It is a rehearsal
/// clock, deliberately separate from the game clock, so practising never touches the show.
/// </summary>
public sealed class PracticeTimer : IDisposable
{
    readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(250) };
    DateTime _deadline;
    int _remaining;
    int _index;

    public IReadOnlyList<PracticeSegment> Segments { get; private set; } = [];
    public int Index => _index;
    public int RemainingSeconds => _remaining;
    public bool Running { get; private set; }
    public bool Expired => _remaining <= 0;

    /// <summary>Raised four times a second while running and once on every start, pause or jump.</summary>
    public event Action? Tick;

    public PracticeTimer()
    {
        _ticker.Tick += (_, _) => Pump();
    }

    public void Load(IReadOnlyList<PracticeSegment> segments, int index)
    {
        Stop();
        Segments = segments;
        _index = Math.Clamp(index, 0, Math.Max(0, segments.Count - 1));
        _remaining = Current?.Seconds ?? 0;
        Tick?.Invoke();
    }

    PracticeSegment? Current => _index >= 0 && _index < Segments.Count ? Segments[_index] : null;

    public static string Format(int seconds)
    {
        seconds = Math.Max(0, seconds);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    public void Start()
    {
        if (Current is null) return;
        if (Running) return;
        if (_remaining <= 0) _remaining = Current.Seconds;
        Running = true;
        _deadline = DateTime.Now.AddSeconds(_remaining);
        _ticker.Start();
        Tick?.Invoke();
    }

    public void Pause()
    {
        Pump();
        Running = false;
        _ticker.Stop();
        Tick?.Invoke();
    }

    public void Toggle()
    {
        if (Running) Pause(); else Start();
    }

    public void Reset()
    {
        Running = false;
        _ticker.Stop();
        _remaining = Current?.Seconds ?? 0;
        Tick?.Invoke();
    }

    /// <summary>Moves to the next segment, starting it when the timer was already running.</summary>
    public void Next(bool wrap = false)
    {
        if (Segments.Count == 0) return;
        var wasRunning = Running;
        var next = _index + 1;
        if (next >= Segments.Count)
        {
            if (!wrap) { Pause(); _remaining = 0; Tick?.Invoke(); return; }
            next = 0;
        }
        _index = next;
        _remaining = Current?.Seconds ?? 0;
        Running = false;
        _ticker.Stop();
        if (wasRunning) Start(); else Tick?.Invoke();
    }

    public void Jump(int index)
    {
        if (Segments.Count == 0) return;
        var wasRunning = Running;
        _index = Math.Clamp(index, 0, Segments.Count - 1);
        _remaining = Current?.Seconds ?? 0;
        Running = false;
        _ticker.Stop();
        if (wasRunning) Start(); else Tick?.Invoke();
    }

    void Pump()
    {
        if (!Running) return;
        _remaining = Math.Max(0, (int)Math.Ceiling((_deadline - DateTime.Now).TotalSeconds));
        if (_remaining <= 0)
        {
            Running = false;
            _ticker.Stop();
        }
        Tick?.Invoke();
    }

    public void Stop()
    {
        _ticker.Stop();
        Running = false;
    }

    public void Dispose() => Stop();
}
