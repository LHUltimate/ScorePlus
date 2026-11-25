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
public partial class VenueDock : UserControl
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
    readonly NodeService _nodes;
    readonly List<ScorebugCanvasView> _displayViews = [];
    string? _selectedNodeId;
    bool _refreshing;

    public VenueDock(VenueEngine venue, ScorebugCatalog catalog, GameEngine engine, NodeService nodes,
        MediaPlayback? playback = null, AutomationRuntime? automation = null, string theme = "Dark")
    {
        InitializeComponent();
        _venue = venue;
        _catalog = catalog;
        _engine = engine;
        _nodes = nodes;
        _playback = playback;
        _automation = automation;
        _theme = theme;
        _venue.Changed += VenueChanged;
        _catalog.Changed += CatalogChanged;
        _engine.Changed += EngineChanged;
        _nodes.Changed += NodesChanged;
        // SelectionChanged bubbles: a playlist/list picker inside a tab must not be mistaken for the
        // tab changing, or refreshing that list recursively rebuilds the whole window.
        Tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, Tabs)) RefreshCurrent();
        };
        RefreshAll();
        Loaded += (_, _) =>
        {
            // The dock can be unloaded when a tab is switched, so reconnect the show sources when
            // it comes back and leave them alone while it is off screen.
            _venue.Changed -= VenueChanged;
            _venue.Changed += VenueChanged;
            _catalog.Changed -= CatalogChanged;
            _catalog.Changed += CatalogChanged;
            _engine.Changed -= EngineChanged;
            _engine.Changed += EngineChanged;
            RefreshAll();
        };
        Unloaded += (_, _) =>
        {
            _venue.Changed -= VenueChanged;
            _catalog.Changed -= CatalogChanged;
            _engine.Changed -= EngineChanged;
            DisposeViews();
        };
    }

    /// <summary>The window that owns any editor opened from this dock, if it is hosted in one.</summary>
    Window? HostWindow => Window.GetWindow(this);

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
            // A health poll or catalog change must not replace the node editor while the operator
            // is typing an address. Cards refresh; the editor refreshes on selection or tab entry.
            RefreshNodes(refreshEditor: Tabs.SelectedIndex != 6);
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
            case 6: RefreshNodes(); break;
        }
    }

    void NodesChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(NodesChanged); return; }
        UpdateNodeHeader();
        if (Tabs.SelectedIndex == 6) RefreshNodes(refreshEditor: false);
    }

    void UpdateStatus()
    {
        var active = _venue.ActivePlaylist;
        StatusText.Text = active is null
            ? $"Ready · {_venue.State.Playlists.Count} playlists · {_venue.State.Proof.Count} proof lines"
            : $"ON AIR · {active.Name} · {_venue.ActiveItem?.DisplayLabel ?? "next"}";
        VenueSubtitle.Text = $"{_catalog.Displays.Count} display{(_catalog.Displays.Count == 1 ? "" : "s")} · "
            + $"{_venue.State.Schedules.Count} schedule{(_venue.State.Schedules.Count == 1 ? "" : "s")} · "
            + $"{_venue.State.Channels.Count} channel{(_venue.State.Channels.Count == 1 ? "" : "s")} · "
            + (_nodes.IsLocalMode ? "local mode" : $"{_nodes.OnlineCount}/{_nodes.Nodes.Count} nodes");
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
            new LiveChannelWindow(_catalog, _engine, _playback, _automation, _theme, _venue.Store) { Owner = HostWindow }.Show();
        });
        buttons.Children.Add(channel);
        var design = ActionButton("Design", (_, _) =>
        {
            _catalog.SelectDisplay(index);
            new ScorebugEditorWindow(_catalog, _engine, _playback, _theme, _automation, _nodes) { Owner = HostWindow }.ShowDialog();
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
            var empty = Card();
            var emptyStack = new StackPanel();
            emptyStack.Children.Add(Text("NO PLAYLIST SELECTED", 12, false, true));
            emptyStack.Children.Add(Text("Create a playlist with + New playlist, then select it in the list. A playlist is a rotation of layouts or clips for one display or the whole venue.", 10, true));
            empty.Child = emptyStack;
            PlaylistEditor.Children.Add(empty);
            return;
        }
        var card = Card();
        var stack = new StackPanel();
        stack.Children.Add(Text("PLAYLIST SETTINGS", 12, false, true));
        stack.Children.Add(Text("1 · Name the playlist and choose where it plays.", 9, true));
        var identity = new Grid();
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.Children.Add(Field("PLAYLIST NAME",
            Input(playlist.Name, value => { playlist.Name = value; Save(); PlaylistList.Items.Refresh(); UpdateStatus(); }), 10));
        var target = Field("TARGET DISPLAY",
            Combo(DisplayChoices(all: true), playlist.TargetDisplayId,
                value => { playlist.TargetDisplayId = value; Save(); }, "All displays"));
        Grid.SetColumn(target, 1);
        identity.Children.Add(target);
        stack.Children.Add(identity);
        stack.Children.Add(Text("2 · Choose the playback rules.", 9, true));
        var flags = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        flags.Children.Add(Check("Loop", playlist.Loop, value => { playlist.Loop = value; Save(); }));
        flags.Children.Add(Check("Shuffle / weight", playlist.Shuffle, value => { playlist.Shuffle = value; Save(); }));
        flags.Children.Add(Check("Enabled", playlist.Enabled, value => { playlist.Enabled = value; Save(); }));
        stack.Children.Add(flags);
        var playback = new Grid();
        playback.ColumnDefinitions.Add(new ColumnDefinition());
        playback.ColumnDefinitions.Add(new ColumnDefinition());
        playback.Children.Add(Field("LOOP COUNT · 0 = UNTIL STOPPED", Input(playlist.LoopCount.ToString(), value =>
        {
            if (int.TryParse(value, out var count)) playlist.LoopCount = Math.Clamp(count, 0, 1000);
            Save();
        }), 10));
        var expires = Field("EXPIRES · YYYY-MM-DDTHH:MM",
            Input(playlist.ExpiresAt, value => { playlist.ExpiresAt = value; Save(); }));
        Grid.SetColumn(expires, 1);
        playback.Children.Add(expires);
        stack.Children.Add(playback);

        Section(stack, "3 · ITEMS IN THIS ROTATION",
            "Add layouts or clips below. The playlist plays them in order, unless Shuffle / weight is on.");
        if (playlist.Items.Count == 0)
            stack.Children.Add(Text("No items yet. Choose a layout below, or add a clip from this PC.", 10, true));
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
                Text = $"ITEM {row + 1} · {item.Kind} · {item.DisplayLabel}", FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("VenueText")
            });
            var remove = RemoveChip(() => { playlist.Items.Remove(item); Save(); LoadPlaylistEditor(playlist); });
            remove.ToolTip = "Remove this item from the playlist";
            DockPanel.SetDock(remove, Dock.Right);
            head.Children.Add(remove);
            itemStack.Children.Add(head);
            var fields = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            for (var column = 0; column < 3; column++)
                fields.ColumnDefinitions.Add(new ColumnDefinition());
            fields.Children.Add(Field("SECONDS ON SCREEN", Input(item.Seconds.ToString("0.#"), value =>
            {
                if (double.TryParse(value, out var seconds)) item.Seconds = Math.Clamp(seconds, 1, 3600);
                Save();
            }, "How long this item stays on screen", 0), 8));
            var sponsor = Field("SPONSOR · OPTIONAL", Input(item.Sponsor, value => { item.Sponsor = value; Save(); }, "The sponsor credited for this play", 1), 8);
            Grid.SetColumn(sponsor, 1);
            fields.Children.Add(sponsor);
            var weight = Field("ROTATION WEIGHT", Input(item.Weight.ToString(), value =>
            {
                if (int.TryParse(value, out var count)) item.Weight = Math.Clamp(count, 1, 100);
                Save();
            }, "Higher weights appear more often when Shuffle / weight is on", 2));
            Grid.SetColumn(weight, 2);
            fields.Children.Add(weight);
            itemStack.Children.Add(fields);
            itemCard.Child = itemStack;
            stack.Children.Add(itemCard);
        }

        Section(stack, "ADD AN ITEM", "Choose a layout, then add it to the rotation. A clip can be picked from this PC.");
        var layoutPicker = new ComboBox { ItemsSource = LayoutChoices(), SelectedIndex = -1, MinHeight = 28, Margin = new Thickness(0, 4, 0, 4) };
        stack.Children.Add(Field("LAYOUT TO ADD", layoutPicker));
        var addRow = new WrapPanel();
        addRow.Children.Add(ActionButton("+ Add layout", (_, _) =>
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
        stack.Children.Add(addRow);
        Section(stack, "PLAYBACK", "Run now starts this playlist immediately. Stop ends the current rotation; editing a playlist does not publish it.");
        var playRow = new WrapPanel();
        playRow.Children.Add(ActionButton("Run now", (_, _) =>
        {
            _venue.BeginPlaylist(playlist.Id, DateTimeOffset.Now);
            UpdateStatus();
        }));
        playRow.Children.Add(ActionButton("Stop", (_, _) => { _venue.StopPlaylist(); UpdateStatus(); }));
        stack.Children.Add(playRow);
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
            var empty = Card();
            var emptyStack = new StackPanel();
            emptyStack.Children.Add(Text("NO SCHEDULE ENTRY SELECTED", 12, false, true));
            emptyStack.Children.Add(Text("Create an entry with + New entry, then select it. Use a schedule for pre-game, halftime, sponsor rotations or a recurring concourse loop.", 10, true));
            empty.Child = emptyStack;
            ScheduleEditor.Children.Add(empty);
            return;
        }
        var card = Card();
        var stack = new StackPanel();
        stack.Children.Add(Text("SCHEDULE ENTRY", 12, false, true));
        stack.Children.Add(Text("1 · Choose what plays and where.", 9, true));
        stack.Children.Add(Field("SCHEDULE NAME",
            Input(schedule.Name, value => { schedule.Name = value; Save(); ScheduleList.Items.Refresh(); })));
        var targets = new Grid();
        targets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        targets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        targets.Children.Add(Field("PLAYLIST",
            Combo(_venue.State.Playlists.Select(playlist => new Choice(playlist.Id, playlist.Name)), schedule.PlaylistId,
                value => { schedule.PlaylistId = value; Save(); }), 10));
        var scheduleDisplay = Field("DISPLAY",
            Combo(DisplayChoices(all: true), schedule.DisplayId,
                value => { schedule.DisplayId = value; Save(); }, "All displays"));
        Grid.SetColumn(scheduleDisplay, 1);
        targets.Children.Add(scheduleDisplay);
        stack.Children.Add(targets);
        if (_venue.State.Playlists.Count == 0)
            stack.Children.Add(Text("There are no playlists yet. Create one on the PLAYLISTS tab before running this entry.", 9, true));
        stack.Children.Add(Text("2 · Set when it starts and how often it repeats.", 9, true));
        var dates = new Grid();
        for (var column = 0; column < 3; column++)
            dates.ColumnDefinitions.Add(new ColumnDefinition());
        dates.Children.Add(Field("DATE · YYYY-MM-DD", Input(schedule.Date, value => { schedule.Date = value; Save(); }, "Leave blank for always / condition-only"), 8));
        var at = Field("START · HH:MM", Input(schedule.AtTime, value => { schedule.AtTime = value; Save(); }, "Leave blank for always / condition-only"), 8);
        Grid.SetColumn(at, 1);
        dates.Children.Add(at);
        var ends = Field("END · HH:MM", Input(schedule.EndsAt, value => { schedule.EndsAt = value; Save(); }, "Optional time to stop the rotation"));
        Grid.SetColumn(ends, 2);
        dates.Children.Add(ends);
        stack.Children.Add(dates);
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
        stack.Children.Add(Label("REPEAT ON THESE DAYS · NONE = EVERY DAY"));
        stack.Children.Add(days);
        stack.Children.Add(Field("REPEAT EVERY N MINUTES · 0 = ONE START", Input(schedule.EveryMinutes.ToString(), value =>
        {
            if (int.TryParse(value, out var minutes)) schedule.EveryMinutes = Math.Clamp(minutes, 0, 1440);
            Save();
        })));
        stack.Children.Add(Check("Enabled", schedule.Enabled, value => { schedule.Enabled = value; Save(); }));
        stack.Children.Add(Check("Match any live condition (OR) instead of all of them (AND)", schedule.MatchAny, value => { schedule.MatchAny = value; Save(); }));

        Section(stack, "3 · LIVE CONDITIONS · OPTIONAL",
            "Add game values that must match for this entry to run. With no conditions, the date and time alone decide when it starts.");
        if (schedule.Conditions.Count == 0)
            stack.Children.Add(Text("No live condition. This entry runs on its schedule alone.", 9, true));
        foreach (var condition in schedule.Conditions.ToArray())
        {
            var row = new Border
            {
                Background = (Brush)FindResource("VenueSurface"), BorderBrush = (Brush)FindResource("VenueBorder"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0, 0, 0, 4)
            };
            var rowContent = new DockPanel();
            rowContent.Children.Add(new TextBlock
            {
                Text = $"{AutomationRules.FieldLabel(condition.Field)} {AutomationRules.OperatorLabel(condition.Operator)} "
                    + $"{(AutomationRules.IsTextField(condition.Field) ? condition.Text : condition.Value.ToString("0.##"))}",
                FontSize = 10, Foreground = (Brush)FindResource("VenueMuted"), VerticalAlignment = VerticalAlignment.Center
            });
            var remove = RemoveChip(() => { schedule.Conditions.Remove(condition); Save(); LoadScheduleEditor(schedule); });
            remove.ToolTip = "Remove this live condition";
            DockPanel.SetDock(remove, Dock.Right);
            rowContent.Children.Add(remove);
            row.Child = rowContent;
            stack.Children.Add(row);
        }
        stack.Children.Add(Text("ADD A LIVE CONDITION", 10, true));
        stack.Children.Add(Text("Choose a game value, how to compare it, and the value to match.", 9, true));
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
        var value = new TextBox
        {
            Style = (Style)FindResource("VenueField"), Text = "0", MinHeight = 28,
            ToolTip = "The live value this condition compares against"
        };
        field.SelectionChanged += (_, _) =>
        {
            if (field.SelectedItem is not FieldChoice choice) return;
            op.ItemsSource = AutomationRules.OperatorsFor(choice.Field)
                .Select(option => new OperatorChoice(option.Operator, option.Label)).ToArray();
            op.SelectedIndex = 0;
        };
        stack.Children.Add(Field("GAME VALUE", field));
        stack.Children.Add(Field("CONDITION", op));
        stack.Children.Add(Field("VALUE", value));
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
        Section(stack, "4 · TEST & MANAGE",
            "Run now starts the selected playlist immediately, outside the schedule. The entry stays saved for its normal times.");
        var actions = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
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
        var overlayChoices = LayoutChoices().ToList();
        overlayChoices.Insert(0, new LayoutChoice("", "", "Follow display output"));
        stack.Children.Add(LayoutCombo(overlayChoices, channel.LayoutId,
            value => { channel.LayoutId = value; Save(); }));
        stack.Children.Add(Label("Camera input"));
        stack.Children.Add(Input(channel.CameraIndex.ToString(), value =>
        {
            if (int.TryParse(value, out var index)) channel.CameraIndex = Math.Clamp(index, 1, 64);
            Save();
        }));
        stack.Children.Add(Check("Enabled", channel.Enabled, value => { channel.Enabled = value; Save(); }));
        stack.Children.Add(ActionButton("Open channel", (_, _) =>
        {
            // Opening a channel is a monitoring action. It must never take the channel's overlay
            // to air on the operator's active display.
            var window = new LiveChannelWindow(_catalog, _engine, _playback, _automation, _theme, _venue.Store)
                { Owner = HostWindow };
            window.ShowChannel(channel.Id);
            window.Show();
        }));
        stack.Children.Add(Text("Choose an overlay, or follow the display's on-air layout. In Live Channel you can switch this channel's overlay without changing any display. Open it and press Start stream to publish an MJPEG feed for players, OBS or a concourse encoder; camera capture stays local to this machine.", 10, true));
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
        var display = _catalog.Displays.FirstOrDefault();
        channel.LayoutId = display?.Layouts.ElementAtOrDefault(Math.Clamp(display.PublishedIndex, 0,
            Math.Max(0, display.Layouts.Count - 1)))?.Id ?? "";
        _venue.State.Channels.Add(channel);
        Save();
        RefreshChannels();
        ChannelList.SelectedItem = channel;
    }

    // ---------- playback nodes ----------

    void UpdateNodeHeader()
    {
        if (NodeModeText is null) return;
        NodeModeText.Text = _nodes.IsLocalMode ? "LOCAL MODE" : "NODE FABRIC";
        NodeSummaryText.Text = _nodes.IsLocalMode
            ? "This PC renders every display. Add a node only when another PC joins the show."
            : $"{_nodes.Summary} · sequence-stamped state · {_nodes.SyncGroups.Count} sync group{(_nodes.SyncGroups.Count == 1 ? "" : "s")}";
    }

    void RefreshNodes(bool refreshEditor = true)
    {
        UpdateNodeHeader();
        NodePanel.Children.Clear();
        NodePanel.Children.Add(BuildLocalNodeCard());
        foreach (var node in _nodes.Nodes.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase))
            NodePanel.Children.Add(BuildNodeCard(node));
        if (refreshEditor)
        {
            var selected = _nodes.Nodes.FirstOrDefault(node => string.Equals(node.Id, _selectedNodeId, StringComparison.OrdinalIgnoreCase))
                ?? _nodes.Nodes.FirstOrDefault();
            _selectedNodeId = selected?.Id;
            LoadNodeEditor(selected);
            RefreshSyncGroups();
        }
    }

    FrameworkElement BuildLocalNodeCard()
    {
        var card = new Border
        {
            Style = (Style)FindResource("VenueCard"), Width = 310, Margin = new Thickness(0, 0, 10, 10),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x6B, 0x58))
        };
        var stack = new StackPanel();
        var head = new DockPanel();
        head.Children.Add(new TextBlock
        {
            Text = "THIS PC", FontSize = 13, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("VenueText")
        });
        var live = new TextBlock
        {
            Text = "LOCAL · ONLINE", FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xD7, 0xA3)),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(live, Dock.Right);
        head.Children.Add(live);
        stack.Children.Add(head);
        stack.Children.Add(Text($"{_catalog.Displays.Count} display{(_catalog.Displays.Count == 1 ? "" : "s")} · every layout renders in this process", 10, true));
        stack.Children.Add(Text("Single-machine mode: no agent, no network package, no extra setup. Add a node below when a second PC or processor is available.", 9, true));
        var open = ActionButton("Open displays", (_, _) =>
        {
            Tabs.SelectedIndex = 0;
            RefreshDisplays();
        });
        stack.Children.Add(open);
        card.Child = stack;
        return card;
    }

    FrameworkElement BuildNodeCard(DisplayNode node)
    {
        var health = node.Health;
        var online = health.Online && !health.Stale;
        var stale = health.Stale && health.Online;
        var colour = online ? Color.FromRgb(0x38, 0xD7, 0xA3)
            : stale ? Color.FromRgb(0xF1, 0xC4, 0x0F)
            : Color.FromRgb(0xFF, 0x72, 0x72);
        var card = new Border
        {
            Style = (Style)FindResource("VenueCard"), Width = 310, Margin = new Thickness(0, 0, 10, 10),
            BorderBrush = string.Equals(node.Id, _selectedNodeId, StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(colour)
                : (Brush)FindResource("VenueBorder")
        };
        var stack = new StackPanel();
        var head = new DockPanel();
        head.Children.Add(new TextBlock
        {
            Text = node.Name, FontSize = 13, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("VenueText"), TextTrimming = TextTrimming.CharacterEllipsis
        });
        var state = new TextBlock
        {
            Text = online ? "ONLINE" : stale ? "STALE" : "OFFLINE",
            FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(colour), HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(state, Dock.Right);
        head.Children.Add(state);
        stack.Children.Add(head);
        stack.Children.Add(Text($"{node.Role} · {node.Address}:{node.Port}", 10, true));
        stack.Children.Add(Text($"{_catalog.FindDisplay(node.DisplayId)?.Name ?? "All displays"} · {node.Output.ResolutionLabel}", 10, true));
        stack.Children.Add(Text(health.Online
            ? $"{health.Sequence} seq · {health.LastFrameMs:0.0} ms frame · {health.DriftMs:0.0} ms drift · pkg {health.PackageVersion}"
            : string.IsNullOrWhiteSpace(health.Error) ? "Waiting for the first health report." : health.Error, 9, true));
        var buttons = new WrapPanel();
        buttons.Children.Add(ActionButton("Edit", (_, _) =>
        {
            _selectedNodeId = node.Id;
            RefreshNodes();
        }));
        buttons.Children.Add(ActionButton("Send package", async (_, _) =>
        {
            StatusText.Text = $"NODE · sending package to {node.Name}…";
            var ok = await _nodes.DeployAsync(node);
            StatusText.Text = ok ? $"NODE · package {node.Health.PackageVersion} sent to {node.Name}." : $"NODE · {node.Name} did not accept the package.";
            RefreshNodes(refreshEditor: false);
        }));
        buttons.Children.Add(ActionButton("Preview package", (_, _) => ShowPackagePreview(node)));
        buttons.Children.Add(ActionButton("Log", async (_, _) => await ShowNodeLogAsync(node)));
        buttons.Children.Add(ActionButton(health.TestPattern ? "Hide test" : "Test pattern", async (_, _) =>
        {
            var ok = await _nodes.SetTestPatternAsync(node, !health.TestPattern);
            StatusText.Text = ok ? $"NODE · test pattern {(node.Output.TestPattern ? "on" : "off")} on {node.Name}." : $"NODE · {node.Name} did not accept the test pattern.";
            RefreshNodes(refreshEditor: false);
        }));
        stack.Children.Add(buttons);
        card.Child = stack;
        return card;
    }

    void LoadNodeEditor(DisplayNode? node)
    {
        NodeEditor.Children.Clear();
        if (node is null)
        {
            NodeEditor.Children.Add(Text("No extra node yet. The local renderer above is enough for a one-machine show. Add a playback node when you want a second PC, processor or monitor.", 11, true));
            NodeEditor.Children.Add(ActionButton("+ Add playback node", AddNode_Click));
            return;
        }
        var wasRefreshing = _refreshing;
        _refreshing = true;
        try
        {
            var head = new DockPanel();
            head.Children.Add(Text($"NODE · {node.Name}", 12, false, true));
            var remove = ActionButton("Remove", (_, _) =>
            {
                if (HostWindow is { } owner && !ConfirmWindow.Ask(owner, _theme, $"REMOVE {node.Name.ToUpperInvariant()}?",
                        "The node keeps its local copy and last rendered state; it simply stops receiving show state from this console.",
                        "Remove node", destructive: true)) return;
                _nodes.RemoveNode(node);
                _selectedNodeId = null;
                RefreshNodes();
            });
            DockPanel.SetDock(remove, Dock.Right);
            head.Children.Add(remove);
            NodeEditor.Children.Add(head);
            NodeEditor.Children.Add(Label("Name"));
            NodeEditor.Children.Add(Input(node.Name, value => { node.Name = value; PersistNode(node); }));
            var identity = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            identity.ColumnDefinitions.Add(new ColumnDefinition());
            identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            identity.ColumnDefinitions.Add(new ColumnDefinition());
            identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            identity.ColumnDefinitions.Add(new ColumnDefinition());
            AddNodeField(identity, 0, "Role", RoleCombo(node));
            AddNodeField(identity, 2, "Address", Input(node.Address, value => { node.Address = value; PersistNode(node); }));
            AddNodeField(identity, 4, "Port", Input(node.Port.ToString(), value =>
            {
                if (int.TryParse(value, out var port)) node.Port = Math.Clamp(port, 1024, 65535);
                PersistNode(node);
            }));
            NodeEditor.Children.Add(identity);
            var targets = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            targets.ColumnDefinitions.Add(new ColumnDefinition());
            targets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            targets.ColumnDefinitions.Add(new ColumnDefinition());
            AddNodeField(targets, 0, "Display", Combo(DisplayChoices(all: true), node.DisplayId, value => { node.DisplayId = value; PersistNode(node); }));
            var groups = new[] { new Choice("", "No group") }
                .Concat(_nodes.SyncGroups.Select(group => new Choice(group.Id, group.Name))).ToArray();
            AddNodeField(targets, 2, "Sync group", Combo(groups, node.SyncGroupId, value => { node.SyncGroupId = value; PersistNode(node); }, "No group"));
            NodeEditor.Children.Add(targets);
            NodeEditor.Children.Add(Label("Access token (required by the agent when it was started with --token)"));
            NodeEditor.Children.Add(Input(node.Token, value => { node.Token = value; PersistNode(node); }));
            NodeEditor.Children.Add(Text("Output", 11, false, true));
            var output = new WrapPanel();
            output.Children.Add(NumberField("Width", node.Output.Width.ToString(), value =>
            {
                if (int.TryParse(value, out var width)) node.Output.Width = Math.Clamp(width, 320, 16384);
                PersistNode(node);
            }));
            output.Children.Add(NumberField("Height", node.Output.Height.ToString(), value =>
            {
                if (int.TryParse(value, out var height)) node.Output.Height = Math.Clamp(height, 240, 16384);
                PersistNode(node);
            }));
            output.Children.Add(NumberField("Hz", node.Output.RefreshHz.ToString(), value =>
            {
                if (int.TryParse(value, out var hz)) node.Output.RefreshHz = Math.Clamp(hz, 24, 240);
                PersistNode(node);
            }));
            output.Children.Add(Check("HDR · unvalidated", node.Output.Hdr, value => { node.Output.Hdr = value; PersistNode(node); }));
            output.Children.Add(Check("Low latency", node.Output.LowLatency, value => { node.Output.LowLatency = value; PersistNode(node); }));
            NodeEditor.Children.Add(output);
            NodeEditor.Children.Add(Text("HDR is carried as a requested output label only. The node window is an SDR surface until an HDR display and swap chain have been validated on that machine; test it before relying on it.", 9, true));
            NodeEditor.Children.Add(Text("Processor calibration", 11, false, true));
            var calibration = new WrapPanel();
            calibration.Children.Add(NumberField("Scale X", node.Calibration.ScaleX.ToString("0.###"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var scale)) node.Calibration.ScaleX = scale;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("Scale Y", node.Calibration.ScaleY.ToString("0.###"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var scale)) node.Calibration.ScaleY = scale;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("Offset X", node.Calibration.OffsetX.ToString("0.#"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var offset)) node.Calibration.OffsetX = offset;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("Offset Y", node.Calibration.OffsetY.ToString("0.#"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var offset)) node.Calibration.OffsetY = offset;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("Rotate", node.Calibration.RotationDegrees.ToString("0.#"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var rotation)) node.Calibration.RotationDegrees = rotation;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("Curve X", node.Calibration.CurveX.ToString("0.#"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var curve)) node.Calibration.CurveX = curve;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("Curve Y", node.Calibration.CurveY.ToString("0.#"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var curve)) node.Calibration.CurveY = curve;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("TL X", node.Calibration.CornerTopLeftX.ToString("0.#"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var corner)) node.Calibration.CornerTopLeftX = corner;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("TL Y", node.Calibration.CornerTopLeftY.ToString("0.#"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var corner)) node.Calibration.CornerTopLeftY = corner;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("TR X", node.Calibration.CornerTopRightX.ToString("0.#"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var corner)) node.Calibration.CornerTopRightX = corner;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("TR Y", node.Calibration.CornerTopRightY.ToString("0.#"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var corner)) node.Calibration.CornerTopRightY = corner;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("BL X", node.Calibration.CornerBottomLeftX.ToString("0.#"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var corner)) node.Calibration.CornerBottomLeftX = corner;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("BL Y", node.Calibration.CornerBottomLeftY.ToString("0.#"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var corner)) node.Calibration.CornerBottomLeftY = corner;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("BR X", node.Calibration.CornerBottomRightX.ToString("0.#"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var corner)) node.Calibration.CornerBottomRightX = corner;
                PersistNode(node);
            }));
            calibration.Children.Add(NumberField("BR Y", node.Calibration.CornerBottomRightY.ToString("0.#"), value =>
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var corner)) node.Calibration.CornerBottomRightY = corner;
                PersistNode(node);
            }));
            NodeEditor.Children.Add(calibration);
            NodeEditor.Children.Add(Text("Curve and corner values are output pixels. The same mesh maps the test pattern and the programme picture, so the test pattern shows the true processor geometry.", 9, true));
            var actions = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            actions.Children.Add(ActionButton("Save node", (_, _) =>
            {
                _nodes.SaveNode(node);
                _nodes.RaiseChanged();
                StatusText.Text = $"NODE · saved {node.Name}.";
            }));
            actions.Children.Add(ActionButton("Test connection", async (_, _) =>
            {
                var ok = await _nodes.RefreshHealthAsync(node);
                StatusText.Text = ok ? $"NODE · {node.Name} answered." : $"NODE · {node.Name} did not answer.";
                RefreshNodes(refreshEditor: false);
            }));
            actions.Children.Add(ActionButton("Send package", async (_, _) =>
            {
                StatusText.Text = $"NODE · sending package to {node.Name}…";
                var ok = await _nodes.DeployAsync(node);
                StatusText.Text = ok ? $"NODE · package {node.Health.PackageVersion} sent to {node.Name}." : $"NODE · {node.Name} did not accept the package.";
                RefreshNodes(refreshEditor: false);
            }));
            actions.Children.Add(ActionButton("Preview package", (_, _) => ShowPackagePreview(node)));
            actions.Children.Add(ActionButton("Node log", async (_, _) => await ShowNodeLogAsync(node)));
            actions.Children.Add(ActionButton("Rollback", async (_, _) =>
            {
                var ok = await _nodes.RollbackAsync(node);
                StatusText.Text = ok ? $"NODE · {node.Name} rolled back." : $"NODE · {node.Name} has no previous package.";
                RefreshNodes(refreshEditor: false);
            }));
            actions.Children.Add(ActionButton(node.Output.TestPattern ? "Hide test" : "Test pattern", async (_, _) =>
            {
                var ok = await _nodes.SetTestPatternAsync(node, !node.Output.TestPattern);
                StatusText.Text = ok ? $"NODE · test pattern {(node.Output.TestPattern ? "on" : "off")} on {node.Name}." : $"NODE · {node.Name} did not accept the test pattern.";
                RefreshNodes(refreshEditor: false);
            }));
            NodeEditor.Children.Add(actions);
            NodeEditor.Children.Add(Text("A node keeps its last received state and playlist when the controller disappears. The console marks it stale rather than pretending the last frame is live.", 10, true));
        }
        finally { _refreshing = wasRefreshing; }
    }

    void ShowPackagePreview(DisplayNode node)
    {
        try
        {
            var preview = _nodes.PreviewPackage();
            new NodePackagePreviewWindow(node, preview, _theme) { Owner = HostWindow }.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"NODE · package preview failed: {ex.Message}";
        }
    }

    async Task ShowNodeLogAsync(DisplayNode node)
    {
        StatusText.Text = $"NODE · reading the log from {node.Name}…";
        var log = await _nodes.GetLogAsync(node);
        StatusText.Text = $"NODE · log from {node.Name}.";
        new NodeLogWindow(node, log, _theme) { Owner = HostWindow }.ShowDialog();
    }

    void AddNodeField(Grid grid, int column, string label, FrameworkElement field)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        stack.Children.Add(Label(label));
        stack.Children.Add(field);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    Border NumberField(string label, string value, Action<string> changed)
    {
        var stack = new StackPanel { Width = 112, Margin = new Thickness(0, 0, 8, 4) };
        stack.Children.Add(Label(label));
        stack.Children.Add(Input(value, changed));
        return new Border { Child = stack };
    }

    ComboBox RoleCombo(DisplayNode node)
    {
        var combo = new ComboBox
        {
            ItemsSource = Enum.GetValues<NodeRole>(), SelectedItem = node.Role,
            MinHeight = 28, Margin = new Thickness(0, 0, 0, 4)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && combo.SelectedItem is NodeRole role)
            {
                node.Role = role;
                PersistNode(node);
            }
        };
        return combo;
    }

    void PersistNode(DisplayNode node)
    {
        node.Normalize();
        _venue.Store.Save();
    }

    void RefreshSyncGroups()
    {
        SyncGroupPanel.Children.Clear();
        foreach (var group in _nodes.SyncGroups)
        {
            var row = new Border
            {
                Background = (Brush)FindResource("VenueSurface"), BorderBrush = (Brush)FindResource("VenueBorder"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 5)
            };
            var dock = new DockPanel();
            var name = Input(group.Name, value => { group.Name = value; _nodes.SaveSyncGroup(group); });
            name.Width = 210;
            name.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(name, Dock.Left);
            dock.Children.Add(name);
            var lead = Input(group.LeadMilliseconds.ToString(), value =>
            {
                if (int.TryParse(value, out var ms)) group.LeadMilliseconds = Math.Clamp(ms, 0, 2000);
                _nodes.SaveSyncGroup(group);
            });
            lead.Width = 72; lead.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(lead, Dock.Left);
            dock.Children.Add(lead);
            var enabled = Check("Enabled", group.Enabled, value => { group.Enabled = value; _nodes.SaveSyncGroup(group); });
            DockPanel.SetDock(enabled, Dock.Left);
            dock.Children.Add(enabled);
            var members = _nodes.Nodes.Count(node => string.Equals(node.SyncGroupId, group.Id, StringComparison.OrdinalIgnoreCase));
            var count = Text($"{members} node{(members == 1 ? "" : "s")}", 9, true);
            DockPanel.SetDock(count, Dock.Right);
            dock.Children.Add(count);
            row.Child = dock;
            SyncGroupPanel.Children.Add(row);
        }
        if (_nodes.SyncGroups.Count == 0)
            SyncGroupPanel.Children.Add(Text("No sync groups. One group is created with the first node.", 10, true));
    }

    void AddNode_Click(object sender, RoutedEventArgs e)
    {
        var address = TextPromptWindow.Ask(HostWindow, "Add playback node", "Address or host name", "127.0.0.1");
        if (address is null) return;
        var node = _nodes.AddNode(address);
        _selectedNodeId = node.Id;
        RefreshNodes();
        StatusText.Text = $"NODE · added {node.Name} at {node.Address}. Send its package when it is online.";
    }

    async void DiscoverNodes_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "NODE · looking for agents on the LAN…";
        var found = await _nodes.DiscoverAsync();
        var added = 0;
        foreach (var item in found)
        {
            if (_nodes.Nodes.Any(node => string.Equals(node.Id, item.NodeId, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(node.Address, item.Address, StringComparison.OrdinalIgnoreCase) && node.Port == item.Port))) continue;
            var node = _nodes.AddNode(item.Address, item.Name);
            node.Id = item.NodeId;
            node.Role = item.Role;
            node.Port = item.Port;
            node.DisplayId = item.DisplayId;
            node.SyncGroupId = item.SyncGroupId;
            _nodes.SaveNode(node);
            _selectedNodeId = node.Id;
            added++;
        }
        RefreshNodes();
        StatusText.Text = added > 0
            ? $"NODE · discovered {added} agent{(added == 1 ? "" : "s")}."
            : "NODE · no agents answered. Check the port and Windows Firewall.";
    }

    void AddSyncGroup_Click(object sender, RoutedEventArgs e)
    {
        _nodes.AddSyncGroup();
        RefreshNodes();
        StatusText.Text = "SYNC · added a frame-clock group.";
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

    // A visible label above every field, rather than a tooltip, so a new operator can read the
    // playlist and schedule forms from top to bottom without guessing what each box does.
    FrameworkElement Field(string label, FrameworkElement control, double rightMargin = 0)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, rightMargin, 0) };
        panel.Children.Add(Label(label));
        panel.Children.Add(control);
        return panel;
    }

    void Section(StackPanel stack, string title, string help = "")
    {
        stack.Children.Add(Text(title, 11, false, true));
        if (help.Length > 0) stack.Children.Add(Text(help, 9, true));
    }

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

    Button RemoveChip(Action action)
    {
        var button = new Button { Content = "×", Style = (Style)FindResource("VenueRemoveChip") };
        button.Click += (_, _) => action();
        return button;
    }
}
