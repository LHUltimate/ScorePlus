using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// Fleet, rotations, schedules, sponsor inventory, proof and local IPTV channels. Everything here
/// works on one machine; a larger venue can use the same page to target individual displays and
/// leave it running unattended.
/// </summary>
public partial class VenueWindow : Window
{
    sealed record Choice(string Value, string Label) { public override string ToString() => Label; }
    sealed record LayoutChoice(string DisplayId, string LayoutId, string Label) { public override string ToString() => Label; }
    sealed record FieldChoice(AutomationField Field, string Label) { public override string ToString() => Label; }
    sealed record OperatorChoice(AutomationOperator Operator, string Label) { public override string ToString() => Label; }
    sealed record ProofRow(string When, string Item, string Display, string Sponsor, string Length, double Seconds);

    readonly VenueEngine _venue;
    readonly ScorebugCatalog _catalog;
    readonly GameEngine _engine;
    readonly MediaPlayback? _playback;
    readonly AutomationRuntime? _automation;
    readonly string _theme;
    readonly List<ScorebugCanvasView> _displayViews = [];
    Window? _ownerWindow;
    WindowState? _ownerState;
    bool _refreshing;

    public VenueWindow(VenueEngine venue, ScorebugCatalog catalog, GameEngine engine,
        MediaPlayback? playback = null, AutomationRuntime? automation = null, string theme = "Dark")
    {
        InitializeComponent();
        _venue = venue;
        _catalog = catalog;
        _engine = engine;
        _playback = playback;
        _automation = automation;
        _theme = theme;
        Loaded += (_, _) =>
        {
            _ownerWindow = Owner;
            _ownerState = _ownerWindow?.WindowState;
        };
        // Release the console before this window disappears. Some window managers minimize an
        // owner while it is re-activated after its last owned window closes.
        Closing += (_, e) => { if (!e.Cancel) Owner = null; };
        ThemeManager.Apply(this, theme);
        _venue.Changed += VenueChanged;
        _catalog.Changed += CatalogChanged;
        _engine.Changed += EngineChanged;
        // SelectionChanged bubbles: a playlist/list picker inside a tab must not be mistaken for the
        // tab changing, or refreshing that list recursively rebuilds the whole window.
        Tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, Tabs)) RefreshCurrent();
        };
        RefreshAll();
        Closed += (_, _) =>
        {
            _venue.Changed -= VenueChanged;
            _catalog.Changed -= CatalogChanged;
            _engine.Changed -= EngineChanged;
            DisposeViews();
            Dispatcher.BeginInvoke(new Action(RestoreOwnerIfMinimized));
        };
    }

    /// <summary>
    /// Some Windows configurations minimize an owner when its last owned window closes. A venue
    /// desk must never hide the console behind it, so put the owner back the way it was before.
    /// </summary>
    void RestoreOwnerIfMinimized()
    {
        if (_ownerWindow is not { WindowState: WindowState.Minimized }) return;
        if (_ownerState is null or WindowState.Minimized) return;
        _ownerWindow.WindowState = _ownerState.Value;
        _ownerWindow.Activate();
    }

    void DisposeViews()
    {
        foreach (var view in _displayViews) view.Dispose();
        _displayViews.Clear();
    }

    void VenueChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(VenueChanged); return; }
        UpdateStatus();
        if (Tabs.SelectedIndex is 4 or 5) RefreshCurrent();
    }

    void CatalogChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(CatalogChanged); return; }
        if (_refreshing) return;
        RefreshAll();
    }

    void EngineChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(EngineChanged); return; }
        UpdateStatus();
    }

    void RefreshAll()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            RefreshDisplays();
            RefreshPlaylists();
            RefreshSchedules();
            RefreshSponsors();
            RefreshProof();
            RefreshChannels();
            UpdateStatus();
        }
        finally { _refreshing = false; }
    }

    void RefreshCurrent()
    {
        switch (Tabs.SelectedIndex)
        {
            case 0: RefreshDisplays(); break;
            case 1: RefreshPlaylists(); break;
            case 2: RefreshSchedules(); break;
            case 3: RefreshSponsors(); break;
            case 4: RefreshProof(); break;
            case 5: RefreshChannels(); break;
        }
    }

    void UpdateStatus()
    {
        var active = _venue.ActivePlaylist;
        StatusText.Text = active is null
            ? $"Ready · {_venue.State.Playlists.Count} playlists · {_venue.State.Proof.Count} proof lines"
            : $"ON AIR · {active.Name} · {_venue.ActiveItem?.DisplayLabel ?? "next"}";
        VenueSubtitle.Text = $"{_catalog.Displays.Count} display{(_catalog.Displays.Count == 1 ? "" : "s")} · "
            + $"{_venue.State.Schedules.Count} schedule{(_venue.State.Schedules.Count == 1 ? "" : "s")} · "
            + $"{_venue.State.Channels.Count} channel{(_venue.State.Channels.Count == 1 ? "" : "s")}";
    }

    // ---------- displays ----------

    void RefreshDisplays()
    {
        DisposeViews();
        DisplayPanel.Children.Clear();
        for (var index = 0; index < _catalog.Displays.Count; index++)
            DisplayPanel.Children.Add(BuildDisplayCard(_catalog.Displays[index], index));
        if (_catalog.Displays.Count == 0)
            DisplayPanel.Children.Add(Text("No displays. Add one to start a screen.", 11, true));
    }

    FrameworkElement BuildDisplayCard(ScorebugDisplay display, int index)
    {
        var layout = display.Layouts.ElementAtOrDefault(Math.Clamp(display.PublishedIndex, 0, Math.Max(0, display.Layouts.Count - 1)));
        var card = new Border
        {
            Style = (Style)FindResource("VenueCard"),
            Width = 310, Margin = new Thickness(0, 0, 10, 10)
        };
        var stack = new StackPanel();
        var head = new DockPanel();
        head.Children.Add(new TextBlock
        {
            Text = display.Name, FontSize = 13, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("VenueText")
        });
        var live = new TextBlock
        {
            Text = index == _catalog.ActiveDisplayIndex ? "PROGRAM" : "OUTPUT", FontSize = 9,
            Foreground = index == _catalog.ActiveDisplayIndex ? new SolidColorBrush(Color.FromRgb(0x38, 0xD7, 0xA3))
                : (Brush)FindResource("VenueMuted"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(live, Dock.Right);
        head.Children.Add(live);
        stack.Children.Add(head);
        stack.Children.Add(Text($"{layout?.Name ?? "No layout"} · {layout?.Width ?? 0} × {layout?.Height ?? 0}", 10, true));
        stack.Children.Add(Text($"last change {(int)_catalog.PublishedElapsedSeconds(display.Id)} s ago · {display.Layouts.Count} layouts", 9, true));

        if (layout is not null)
        {
            var host = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(7, 11, 16)), CornerRadius = new CornerRadius(5),
                Height = 150, Margin = new Thickness(0, 8, 0, 8), ClipToBounds = true
            };
            var view = new ScorebugCanvasView(_catalog, _engine, stateProvider: () => _engine.State,
                playback: null, layoutProvider: () => layout, liveValues: true, displayId: display.Id)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            host.Child = view;
            _displayViews.Add(view);
            stack.Children.Add(host);
        }

        var buttons = new WrapPanel();
        var publish = ActionButton("Publish", (_, _) =>
        {
            _catalog.PublishLayout(display.Id, display.ActiveIndex);
            StatusText.Text = $"Published {display.Name}.";
            RefreshDisplays();
        });
        buttons.Children.Add(publish);
        var channel = ActionButton("Open channel", (_, _) =>
        {
            _catalog.SelectDisplay(index);
            new LiveChannelWindow(_catalog, _engine, _playback, _automation, _theme) { Owner = this }.Show();
        });
        buttons.Children.Add(channel);
        var design = ActionButton("Design", (_, _) =>
        {
            _catalog.SelectDisplay(index);
            new ScorebugEditorWindow(_catalog, _engine, _playback, _theme, _automation) { Owner = this }.ShowDialog();
            RefreshDisplays();
        });
        buttons.Children.Add(design);
        stack.Children.Add(buttons);
        card.Child = stack;
        return card;
    }

    void AddDisplay_Click(object sender, RoutedEventArgs e)
    {
        _catalog.AddDisplay();
        RefreshDisplays();
        StatusText.Text = "Added a display. Design its first layout, then publish it from here.";
    }

    // ---------- playlists ----------

    void RefreshPlaylists()
    {
        var selectedId = (PlaylistList.SelectedItem as VenuePlaylist)?.Id;
        PlaylistList.ItemsSource = null;
        PlaylistList.ItemsSource = _venue.State.Playlists.ToArray();
        PlaylistList.SelectedItem = _venue.State.Playlists.FirstOrDefault(playlist => playlist.Id == selectedId)
            ?? _venue.State.Playlists.FirstOrDefault();
        LoadPlaylistEditor(PlaylistList.SelectedItem as VenuePlaylist);
    }

    void Playlist_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        LoadPlaylistEditor(PlaylistList.SelectedItem as VenuePlaylist);
    }

    void LoadPlaylistEditor(VenuePlaylist? playlist)
    {
        PlaylistEditor.Children.Clear();
        if (playlist is null)
        {
            PlaylistEditor.Children.Add(Text("Add a playlist to build a rotation.", 11, true));
            return;
        }
        var card = Card();
        var stack = new StackPanel();
        stack.Children.Add(Text("PLAYLIST", 12, false, true));
        stack.Children.Add(Label("Name"));
        stack.Children.Add(Input(playlist.Name, value => { playlist.Name = value; Save(); PlaylistList.Items.Refresh(); UpdateStatus(); }));
        stack.Children.Add(Label("Target"));
        stack.Children.Add(Combo(DisplayChoices(all: true), playlist.TargetDisplayId,
            value => { playlist.TargetDisplayId = value; Save(); }, "All displays"));
        var flags = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        flags.Children.Add(Check("Loop", playlist.Loop, value => { playlist.Loop = value; Save(); }));
        flags.Children.Add(Check("Shuffle / weight", playlist.Shuffle, value => { playlist.Shuffle = value; Save(); }));
        flags.Children.Add(Check("Enabled", playlist.Enabled, value => { playlist.Enabled = value; Save(); }));
        stack.Children.Add(flags);
        stack.Children.Add(Label("Loop count (0 = until stopped)"));
        stack.Children.Add(Input(playlist.LoopCount.ToString(), value =>
        {
            if (int.TryParse(value, out var count)) playlist.LoopCount = Math.Clamp(count, 0, 1000);
            Save();
        }));
        stack.Children.Add(Label("Expires (optional, e.g. 2026-12-31T23:00:00)"));
        stack.Children.Add(Input(playlist.ExpiresAt, value => { playlist.ExpiresAt = value; Save(); }));

        stack.Children.Add(Text("ITEMS", 11, false, true));
        foreach (var item in playlist.Items.ToArray())
        {
            var row = playlist.Items.IndexOf(item);
            var itemCard = new Border
            {
                Background = (Brush)FindResource("VenueSurface"), BorderBrush = (Brush)FindResource("VenueBorder"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6),
                Margin = new Thickness(0, 0, 0, 5)
            };
            var itemStack = new StackPanel();
            var head = new DockPanel();
            head.Children.Add(new TextBlock
            {
                Text = $"{item.Kind} · {LayoutName(item.LayoutId)}", FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("VenueText")
            });
            var remove = ActionButton("×", (_, _) => { playlist.Items.Remove(item); Save(); LoadPlaylistEditor(playlist); });
            DockPanel.SetDock(remove, Dock.Right);
            head.Children.Add(remove);
            itemStack.Children.Add(head);
            var fields = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            for (var column = 0; column < 3; column++)
                fields.ColumnDefinitions.Add(new ColumnDefinition());
            fields.Children.Add(Input(item.Seconds.ToString("0.#"), value =>
            {
                if (double.TryParse(value, out var seconds)) item.Seconds = Math.Clamp(seconds, 1, 3600);
                Save();
            }, "Seconds", 0));
            var sponsor = Input(item.Sponsor, value => { item.Sponsor = value; Save(); }, "Sponsor (optional)", 1);
            Grid.SetColumn(sponsor, 1);
            fields.Children.Add(sponsor);
            var weight = Input(item.Weight.ToString(), value =>
            {
                if (int.TryParse(value, out var count)) item.Weight = Math.Clamp(count, 1, 100);
                Save();
            }, "Rotation weight", 2);
            Grid.SetColumn(weight, 2);
            fields.Children.Add(weight);
            itemStack.Children.Add(fields);
            itemCard.Child = itemStack;
            stack.Children.Add(itemCard);
        }

        var layoutPicker = new ComboBox { ItemsSource = LayoutChoices(), SelectedIndex = -1, MinHeight = 28, Margin = new Thickness(0, 4, 0, 4) };
        stack.Children.Add(layoutPicker);
        var addRow = new WrapPanel();
        addRow.Children.Add(ActionButton("+ Add layout item", (_, _) =>
        {
            if (layoutPicker.SelectedItem is not LayoutChoice picked) return;
            playlist.Items.Add(new VenuePlaylistItem
            {
                Kind = VenueItemKind.Layout, LayoutId = picked.LayoutId, Label = picked.Label.Split('·')[^1].Trim(), Seconds = 10
            });
            Save();
            LoadPlaylistEditor(playlist);
        }));
        addRow.Children.Add(ActionButton("+ Add clip", (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Media files|*.mp4;*.mov;*.wmv;*.avi;*.mkv;*.png;*.jpg;*.jpeg;*.gif|All files|*.*"
            };
            if (dialog.ShowDialog() != true) return;
            playlist.Items.Add(new VenuePlaylistItem
            {
                Kind = VenueItemKind.Media, MediaPath = dialog.FileName,
                Label = Path.GetFileName(dialog.FileName), Seconds = 15
            });
            Save();
            LoadPlaylistEditor(playlist);
        }));
        addRow.Children.Add(ActionButton("Run now", (_, _) =>
        {
            _venue.BeginPlaylist(playlist.Id, DateTimeOffset.Now);
            UpdateStatus();
        }));
        addRow.Children.Add(ActionButton("Stop", (_, _) => { _venue.StopPlaylist(); UpdateStatus(); }));
        stack.Children.Add(addRow);
        card.Child = stack;
        PlaylistEditor.Children.Add(card);
    }

    void AddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var playlist = _venue.Store.AddPlaylist();
        Save();
        RefreshPlaylists();
        PlaylistList.SelectedItem = playlist;
    }

    // ---------- schedules ----------

    void RefreshSchedules()
    {
        var selectedId = (ScheduleList.SelectedItem as VenueSchedule)?.Id;
        ScheduleList.ItemsSource = null;
        ScheduleList.ItemsSource = _venue.State.Schedules.ToArray();
        ScheduleList.SelectedItem = _venue.State.Schedules.FirstOrDefault(schedule => schedule.Id == selectedId)
            ?? _venue.State.Schedules.FirstOrDefault();
        LoadScheduleEditor(ScheduleList.SelectedItem as VenueSchedule);
    }

    void Schedule_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        LoadScheduleEditor(ScheduleList.SelectedItem as VenueSchedule);
    }

    void LoadScheduleEditor(VenueSchedule? schedule)
    {
        ScheduleEditor.Children.Clear();
        if (schedule is null)
        {
            ScheduleEditor.Children.Add(Text("Add a schedule entry for pre-game, halftime or a sponsor rotation.", 11, true));
            return;
        }
        var card = Card();
        var stack = new StackPanel();
        stack.Children.Add(Text("SCHEDULE ENTRY", 12, false, true));
        stack.Children.Add(Label("Name"));
        stack.Children.Add(Input(schedule.Name, value => { schedule.Name = value; Save(); ScheduleList.Items.Refresh(); }));
        stack.Children.Add(Label("Playlist"));
        stack.Children.Add(Combo(_venue.State.Playlists.Select(playlist => new Choice(playlist.Id, playlist.Name)), schedule.PlaylistId,
            value => { schedule.PlaylistId = value; Save(); }));
        stack.Children.Add(Label("Display"));
        stack.Children.Add(Combo(DisplayChoices(all: false), schedule.DisplayId,
            value => { schedule.DisplayId = value; Save(); }, "All displays"));
        var dates = new Grid();
        dates.ColumnDefinitions.Add(new ColumnDefinition());
        dates.ColumnDefinitions.Add(new ColumnDefinition());
        dates.Children.Add(Input(schedule.Date, value => { schedule.Date = value; Save(); }, "Date yyyy-MM-dd"));
        var at = Input(schedule.AtTime, value => { schedule.AtTime = value; Save(); }, "Start HH:mm");
        Grid.SetColumn(at, 1);
        dates.Children.Add(at);
        stack.Children.Add(Label("Date and start time (blank = always / live condition)"));
        stack.Children.Add(dates);
        stack.Children.Add(Input(schedule.EndsAt, value => { schedule.EndsAt = value; Save(); }, "End HH:mm (optional)"));
        var days = new WrapPanel();
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var box = Check(day.ToString()[..3], schedule.Days.Contains(day), value =>
            {
                if (value && !schedule.Days.Contains(day)) schedule.Days.Add(day);
                else if (!value) schedule.Days.Remove(day);
                Save();
            });
            days.Children.Add(box);
        }
        stack.Children.Add(Label("Days (none = every day)"));
        stack.Children.Add(days);
        stack.Children.Add(Label("Repeat every N minutes (0 = one start)"));
        stack.Children.Add(Input(schedule.EveryMinutes.ToString(), value =>
        {
            if (int.TryParse(value, out var minutes)) schedule.EveryMinutes = Math.Clamp(minutes, 0, 1440);
            Save();
        }));
        stack.Children.Add(Check("Enabled", schedule.Enabled, value => { schedule.Enabled = value; Save(); }));
        stack.Children.Add(Check("Match any condition", schedule.MatchAny, value => { schedule.MatchAny = value; Save(); }));

        stack.Children.Add(Text("LIVE CONDITIONS", 11, false, true));
        foreach (var condition in schedule.Conditions.ToArray())
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = $"{AutomationRules.FieldLabel(condition.Field)} {AutomationRules.OperatorLabel(condition.Operator)} "
                    + $"{(AutomationRules.IsTextField(condition.Field) ? condition.Text : condition.Value.ToString("0.##"))}",
                FontSize = 10, Foreground = (Brush)FindResource("VenueMuted"), VerticalAlignment = VerticalAlignment.Center
            });
            var remove = ActionButton("×", (_, _) => { schedule.Conditions.Remove(condition); Save(); LoadScheduleEditor(schedule); });
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            stack.Children.Add(row);
        }
        var field = new ComboBox
        {
            ItemsSource = AutomationRules.Fields.Select(option => new FieldChoice(option.Field, option.Label)).ToArray(),
            SelectedIndex = 0, MinHeight = 28, Margin = new Thickness(0, 4, 0, 4)
        };
        var op = new ComboBox
        {
            ItemsSource = AutomationRules.OperatorsFor(AutomationField.PlayClock)
                .Select(option => new OperatorChoice(option.Operator, option.Label)).ToArray(),
            SelectedIndex = 0, MinHeight = 28, Margin = new Thickness(0, 0, 0, 4)
        };
        var value = new TextBox { MinHeight = 28, Margin = new Thickness(0, 0, 0, 4), Text = "0" };
        field.SelectionChanged += (_, _) =>
        {
            if (field.SelectedItem is not FieldChoice choice) return;
            op.ItemsSource = AutomationRules.OperatorsFor(choice.Field)
                .Select(option => new OperatorChoice(option.Operator, option.Label)).ToArray();
            op.SelectedIndex = 0;
        };
        stack.Children.Add(field);
        stack.Children.Add(op);
        stack.Children.Add(value);
        stack.Children.Add(ActionButton("+ Add condition", (_, _) =>
        {
            if (field.SelectedItem is not FieldChoice fieldChoice || op.SelectedItem is not OperatorChoice operatorChoice) return;
            var condition = new AutomationCondition { Field = fieldChoice.Field, Operator = operatorChoice.Operator };
            if (AutomationRules.IsTextField(fieldChoice.Field)) condition.Text = value.Text.Trim();
            else if (double.TryParse(value.Text, out var number)) condition.Value = number;
            schedule.Conditions.Add(condition);
            Save();
            LoadScheduleEditor(schedule);
        }));
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(ActionButton("Run now", (_, _) =>
        {
            _venue.BeginPlaylist(schedule.PlaylistId, DateTimeOffset.Now, "manual", schedule.DisplayId);
            UpdateStatus();
        }));
        actions.Children.Add(ActionButton("Delete entry", (_, _) =>
        {
            _venue.State.Schedules.Remove(schedule);
            Save();
            RefreshSchedules();
        }));
        stack.Children.Add(actions);
        card.Child = stack;
        ScheduleEditor.Children.Add(card);
    }

    void AddSchedule_Click(object sender, RoutedEventArgs e)
    {
        var schedule = _venue.Store.AddSchedule();
        Save();
        RefreshSchedules();
        ScheduleList.SelectedItem = schedule;
    }

    // ---------- sponsors ----------

    void RefreshSponsors()
    {
        var selected = (SponsorList.SelectedItem as VenueSponsor)?.Id;
        SponsorList.ItemsSource = null;
        SponsorList.ItemsSource = _venue.State.Sponsors.ToArray();
        SponsorList.SelectedItem = _venue.State.Sponsors.FirstOrDefault(sponsor => sponsor.Id == selected)
            ?? _venue.State.Sponsors.FirstOrDefault();
        LoadSponsorEditor(SponsorList.SelectedItem as VenueSponsor);
    }

    void Sponsor_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        LoadSponsorEditor(SponsorList.SelectedItem as VenueSponsor);
    }

    void LoadSponsorEditor(VenueSponsor? sponsor)
    {
        SponsorEditor.Children.Clear();
        if (sponsor is null)
        {
            SponsorEditor.Children.Add(Text("Add a sold package to track sponsor rotations and totals.", 11, true));
            return;
        }
        var card = Card();
        var stack = new StackPanel();
        stack.Children.Add(Text("SPONSOR INVENTORY", 12, false, true));
        stack.Children.Add(Label("Sponsor"));
        stack.Children.Add(Input(sponsor.Name, value => { sponsor.Name = value; Save(); SponsorList.Items.Refresh(); }));
        stack.Children.Add(Label("Playlist"));
        stack.Children.Add(Combo(_venue.State.Playlists.Select(playlist => new Choice(playlist.Id, playlist.Name)),
            sponsor.PlaylistId, value => { sponsor.PlaylistId = value; Save(); }));
        stack.Children.Add(Label("Display"));
        stack.Children.Add(Combo(DisplayChoices(all: true), sponsor.DisplayId, value => { sponsor.DisplayId = value; Save(); }, "All displays"));
        stack.Children.Add(Label("Rotation weight"));
        stack.Children.Add(Input(sponsor.Weight.ToString(), value =>
        {
            if (int.TryParse(value, out var weight)) sponsor.Weight = Math.Clamp(weight, 1, 100);
            Save();
        }));
        stack.Children.Add(Label("Starts / ends (yyyy-MM-dd)"));
        var dates = new Grid();
        dates.ColumnDefinitions.Add(new ColumnDefinition());
        dates.ColumnDefinitions.Add(new ColumnDefinition());
        dates.Children.Add(Input(sponsor.StartsOn, value => { sponsor.StartsOn = value; Save(); }));
        var end = Input(sponsor.EndsOn, value => { sponsor.EndsOn = value; Save(); });
        Grid.SetColumn(end, 1);
        dates.Children.Add(end);
        stack.Children.Add(dates);
        stack.Children.Add(Check("Enabled", sponsor.Enabled, value => { sponsor.Enabled = value; Save(); }));
        stack.Children.Add(Text("PROOF TOTALS", 11, false, true));
        var totals = _venue.SponsorTotals();
        if (totals.Count == 0) stack.Children.Add(Text("No sponsored item has played yet.", 10, true));
        foreach (var total in totals)
            stack.Children.Add(Text($"{total.Sponsor} · {total.Plays} plays · {total.Seconds / 60:0.0} min", 10));
        stack.Children.Add(ActionButton("Delete sponsor", (_, _) =>
        {
            _venue.State.Sponsors.Remove(sponsor);
            Save();
            RefreshSponsors();
        }));
        card.Child = stack;
        SponsorEditor.Children.Add(card);
    }

    void AddSponsor_Click(object sender, RoutedEventArgs e)
    {
        var sponsor = new VenueSponsor { Name = $"Sponsor {_venue.State.Sponsors.Count + 1}" };
        _venue.State.Sponsors.Add(sponsor);
        Save();
        RefreshSponsors();
        SponsorList.SelectedItem = sponsor;
    }

    // ---------- proof ----------

    void RefreshProof()
    {
        var rows = _venue.State.Proof.Select(proof => new ProofRow(
            proof.At.LocalDateTime.ToString("MMM d HH:mm:ss"), proof.Item, proof.DisplayName,
            string.IsNullOrWhiteSpace(proof.Sponsor) ? "—" : proof.Sponsor,
            proof.Seconds <= 0 ? "—" : $"{proof.Seconds:0.#} s", proof.Seconds)).ToArray();
        ProofList.ItemsSource = rows;
        ProofSummary.Text = rows.Length == 0
            ? "Nothing has played through a venue playlist yet."
            : $"{rows.Length} proof lines · {rows.Sum(row => row.Seconds) / 60:0.0} minutes";
    }

    void ExportProof_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "scoreplus-proof-of-play.csv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _venue.ProofCsv());
            StatusText.Text = $"Exported proof of play to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not export proof: {ex.Message}";
        }
    }

    void ClearProof_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Clear the proof-of-play history?", "Proof of play",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _venue.ClearProof();
        RefreshProof();
    }

    // ---------- channels ----------

    void RefreshChannels()
    {
        var selected = (ChannelList.SelectedItem as VenueChannel)?.Id;
        ChannelList.ItemsSource = null;
        ChannelList.ItemsSource = _venue.State.Channels.ToArray();
        ChannelList.SelectedItem = _venue.State.Channels.FirstOrDefault(channel => channel.Id == selected)
            ?? _venue.State.Channels.FirstOrDefault();
        LoadChannelEditor(ChannelList.SelectedItem as VenueChannel);
    }

    void Channel_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        LoadChannelEditor(ChannelList.SelectedItem as VenueChannel);
    }

    void LoadChannelEditor(VenueChannel? channel)
    {
        ChannelEditor.Children.Clear();
        if (channel is null)
        {
            ChannelEditor.Children.Add(Text("Turn a camera and an overlay into a concourse channel.", 11, true));
            return;
        }
        var card = Card();
        var stack = new StackPanel();
        stack.Children.Add(Text("CHANNEL WIZARD", 12, false, true));
        stack.Children.Add(Label("Name"));
        stack.Children.Add(Input(channel.Name, value => { channel.Name = value; Save(); ChannelList.Items.Refresh(); }));
        stack.Children.Add(Label("Display / overlay"));
        stack.Children.Add(Combo(DisplayChoices(all: false), channel.DisplayId,
            value => { channel.DisplayId = value; Save(); }));
        stack.Children.Add(Label("Layout"));
        stack.Children.Add(LayoutCombo(LayoutChoices(), channel.LayoutId, value => { channel.LayoutId = value; Save(); }));
        stack.Children.Add(Label("Camera input"));
        stack.Children.Add(Input(channel.CameraIndex.ToString(), value =>
        {
            if (int.TryParse(value, out var index)) channel.CameraIndex = Math.Clamp(index, 1, 64);
            Save();
        }));
        stack.Children.Add(Check("Enabled", channel.Enabled, value => { channel.Enabled = value; Save(); }));
        stack.Children.Add(ActionButton("Open channel", (_, _) =>
        {
            var displayIndex = _catalog.Displays.FindIndex(display =>
                string.Equals(display.Id, channel.DisplayId, StringComparison.OrdinalIgnoreCase));
            if (displayIndex >= 0)
            {
                _catalog.SelectDisplay(displayIndex);
                var layoutIndex = _catalog.Layouts.ToList().FindIndex(layout =>
                    string.Equals(layout.Id, channel.LayoutId, StringComparison.OrdinalIgnoreCase));
                if (layoutIndex >= 0) _catalog.SelectLayout(layoutIndex, publish: true);
            }
            new LiveChannelWindow(_catalog, _engine, _playback, _automation, _theme) { Owner = this }.Show();
        }));
        stack.Children.Add(Text("The channel uses the selected display's live scorebug overlay. Open it and press Start stream to publish an MJPEG feed for players, OBS or a concourse encoder; camera capture stays local to this machine.", 10, true));
        card.Child = stack;
        ChannelEditor.Children.Add(card);
    }

    void AddChannel_Click(object sender, RoutedEventArgs e)
    {
        var channel = new VenueChannel
        {
            Name = $"Channel {_venue.State.Channels.Count + 1}",
            DisplayId = _catalog.Displays.FirstOrDefault()?.Id ?? ""
        };
        _venue.State.Channels.Add(channel);
        Save();
        RefreshChannels();
        ChannelList.SelectedItem = channel;
    }

    // ---------- helpers ----------

    void Save()
    {
        _venue.Store.Save();
        UpdateStatus();
    }

    IEnumerable<Choice> DisplayChoices(bool all)
    {
        if (all) yield return new Choice("", "All displays");
        foreach (var display in _catalog.Displays) yield return new Choice(display.Id, display.Name);
    }

    IEnumerable<LayoutChoice> LayoutChoices() =>
        _catalog.Displays.SelectMany(display => display.Layouts.Select(layout =>
            new LayoutChoice(display.Id, layout.Id, $"{display.Name} · {layout.Name}"))).ToArray();

    string LayoutName(string id)
    {
        foreach (var display in _catalog.Displays)
            foreach (var layout in display.Layouts)
                if (string.Equals(layout.Id, id, StringComparison.OrdinalIgnoreCase)) return layout.Name;
        return "missing layout";
    }

    Border Card() => new() { Style = (Style)FindResource("VenueCard"), Margin = new Thickness(0, 0, 0, 8) };

    TextBlock Text(string text, double size, bool muted = false, bool heading = false) => new()
    {
        Text = text, FontSize = size,
        FontWeight = heading ? FontWeights.Bold : FontWeights.Normal,
        Foreground = (Brush)FindResource(muted ? "VenueMuted" : "VenueText"),
        TextWrapping = TextWrapping.Wrap, Margin = heading ? new Thickness(0, 10, 0, 4) : new Thickness(0, 2, 0, 3)
    };

    TextBlock Label(string text) => Text(text, 10, true);

    TextBox Input(string value, Action<string> changed, string? tooltip = null, int column = -1)
    {
        var box = new TextBox { Style = (Style)FindResource("VenueField"), Text = value, ToolTip = tooltip };
        box.TextChanged += (_, _) => { if (!_refreshing) changed(box.Text); };
        return box;
    }

    ComboBox LayoutCombo(IEnumerable<LayoutChoice> choices, string? selected, Action<string> changed)
    {
        var list = choices.ToArray();
        var combo = new ComboBox
        {
            ItemsSource = list, MinHeight = 28, Margin = new Thickness(0, 0, 0, 4),
            SelectedItem = list.FirstOrDefault(choice => string.Equals(choice.LayoutId, selected ?? "", StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault()
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && combo.SelectedItem is LayoutChoice choice) changed(choice.LayoutId);
        };
        return combo;
    }

    ComboBox Combo(IEnumerable<Choice> choices, string? selected, Action<string> changed, string allLabel = "")
    {
        var list = choices.ToArray();
        var combo = new ComboBox
        {
            ItemsSource = list, MinHeight = 28, Margin = new Thickness(0, 0, 0, 4),
            SelectedItem = list.FirstOrDefault(choice => string.Equals(choice.Value, selected ?? "", StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault()
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && combo.SelectedItem is Choice choice) changed(choice.Value);
        };
        return combo;
    }

    CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox
        {
            Content = text, IsChecked = value, FontSize = 10, Margin = new Thickness(0, 2, 14, 2),
            Foreground = (Brush)FindResource("VenueText")
        };
        box.Checked += (_, _) => { if (!_refreshing) changed(true); };
        box.Unchecked += (_, _) => { if (!_refreshing) changed(false); };
        return box;
    }

    Button ActionButton(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Style = (Style)FindResource("VenueButton") };
        button.Click += click;
        return button;
    }
}
