using System.Windows;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The event log used to sit under the preview. It now lives in its own window so the dock can be
/// all media, and it stays live against the same engine. Its second page is the show's trigger
/// view: every conditional rule, where it lives and whether it is matching right now, which used to
/// need a second diagnostics window.
/// </summary>
public partial class EventLogWindow : Window
{
    readonly GameEngine _engine;
    readonly ScorebugCatalog? _catalog;
    readonly AutomationRuntime? _automation;
    readonly ShowHeartbeat _beat = ShowHeartbeat.Shared;
    long _lastBeatSecond = -1;
    int _count = -1;
    bool _frozen;

    public EventLogWindow(GameEngine engine, string theme, bool showHints,
        ScorebugCatalog? catalog = null, AutomationRuntime? automation = null)
    {
        InitializeComponent();
        _engine = engine;
        _catalog = catalog;
        _automation = automation;
        LogHint.Visibility = showHints ? Visibility.Visible : Visibility.Collapsed;
        ThemeManager.Apply(this, theme);
        _engine.Changed += Refresh;
        if (_catalog is not null) _catalog.Changed += RefreshTriggers;
        _beat.Tick += Beat;
        _beat.Start();
        Refresh();
        RefreshTriggers();
    }

    public void SetHintVisibility(bool show) => LogHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    void Beat(TimeSpan elapsed)
    {
        // The triggers page only needs whole seconds; the shared heartbeat runs ten times faster.
        var second = (long)elapsed.TotalSeconds;
        if (second == _lastBeatSecond) return;
        _lastBeatSecond = second;
        RefreshTriggers();
    }

    void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Refresh); return; }
        if (_frozen || _engine.State.Events.Count == _count) return;
        _count = _engine.State.Events.Count;
        LogList.ItemsSource = _engine.State.Events.AsEnumerable().Reverse()
            .Select(x => $"{x.At:HH:mm:ss}  {x.Type,-12} {x.Description}").ToArray();
    }

    /// <summary>
    /// Lists every rule the show carries. The leading mark is filled while the rule matches, so the
    /// operator can see what is driving the board without opening the studio.
    /// </summary>
    void RefreshTriggers()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshTriggers); return; }
        if (_catalog is null) return;
        var rows = new List<string>();
        var live = 0;
        foreach (var display in _catalog.Displays)
        {
            foreach (var rule in display.Automations ?? [])
            {
                var active = _automation?.IsRuleActive(display.Id, rule.Id) == true;
                if (active) live++;
                var state = !rule.Enabled ? "off" : active ? "LIVE" : "idle";
                var schedule = AutomationRules.NormalizeTime(rule.AtTime);
                var when = schedule.Length > 0 ? $"  at {schedule}{(rule.AtTimeDaily ? " daily" : "")}" : "";
                var actions = rule.Actions?.Count ?? 0;
                var last = _automation?.LastFired(display.Id, rule.Id);
                var fired = last is null ? "" : $"  · last {last.Value:HH:mm:ss}";
                rows.Add($"{(active ? "●" : "○")} [{state,-4}] {display.Name} · {rule.Name}{when} · {actions} action{(actions == 1 ? "" : "s")}{fired}");
            }
        }
        TriggerList.ItemsSource = rows;
        TriggerEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TriggerSummary.Text = rows.Count == 0
            ? "No rules on this show yet."
            : $"{rows.Count} rule{(rows.Count == 1 ? "" : "s")} · {live} matching now · refreshed once a second";
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        _frozen = true;
        LogList.ItemsSource = null;
        _count = _engine.State.Events.Count;
    }

    protected override void OnClosed(EventArgs e)
    {
        _engine.Changed -= Refresh;
        if (_catalog is not null) _catalog.Changed -= RefreshTriggers;
        _beat.Tick -= Beat;
        base.OnClosed(e);
    }
}
