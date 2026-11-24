using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The DATA tab: one editor per source type, a live row browser, and a mapping from source keys to
/// game or custom values. Manual and file sources keep a small school running on one laptop; the
/// same page accepts JSON over HTTP for a league or professional venue.
/// </summary>
public partial class DataDock : UserControl
{
    sealed record KindChoice(DataFeedKind Kind, string Label) { public override string ToString() => Label; }
    sealed record TargetChoice(DataFeedTarget Target, string Label) { public override string ToString() => Label; }
    sealed record ValueRow(string Key, string Value) { public override string ToString() => $"{Key} = {Value}"; }

    readonly GameEngine _engine;
    readonly LeagueCloudService? _leagueCloud;
    readonly DataFeedStore _store;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(8) };
    readonly Dictionary<string, DateTimeOffset> _lastPoll = new(StringComparer.OrdinalIgnoreCase);
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    bool _loading;
    bool _polling;
    string _leagueSignature = "";
    string _tickerGamesSignature = "";
    bool _tickerControlsLoaded;

    public DataDock(GameEngine engine, string theme = "Dark", LeagueCloudService? leagueCloud = null)
    {
        InitializeComponent();
        _engine = engine;
        _leagueCloud = leagueCloud;
        _store = DataFeedStore.Load();
        ThemeManager.ApplyTo(this, theme);
        _engine.Changed += UpdateGameSummary;
        if (_leagueCloud is not null) _leagueCloud.GamesChanged += _ => Dispatcher.BeginInvoke(RefreshLeague);
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();
        RefreshView();
        LoadCloudControls();
        LoadTickerControls();
        RefreshLeague();
        RefreshCloud();
    }

    public DataFeedStore Store => _store;

    /// <summary>The Data tab owns the entry points for cloud and ticker setup.</summary>
    public Action? OpenLeagueCloudRequested { get; set; }
    public Action? OpenTickerRequested { get; set; }

    DataFeed? SelectedFeed => FeedList.SelectedItem as DataFeed;

    public void RefreshView()
    {
        var selectedId = SelectedFeed?.Id;
        _loading = true;
        try
        {
            FeedList.ItemsSource = null;
            FeedList.ItemsSource = _store.Feeds.ToArray();
            var selected = _store.Feeds.FirstOrDefault(feed => string.Equals(feed.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                ?? _store.Feeds.FirstOrDefault();
            FeedList.SelectedItem = selected;
            LoadEditor(selected);
        }
        finally { _loading = false; }
        UpdateGameSummary();
    }

    void LoadEditor(DataFeed? feed)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            NameBox.IsEnabled = LocationBox.IsEnabled = RefreshBox.IsEnabled = AutoBox.IsEnabled = EnabledBox.IsEnabled = feed is not null;
            BrowseButton.IsEnabled = feed is not null && feed.Kind is DataFeedKind.Csv or DataFeedKind.Json or DataFeedKind.Xml;
            if (feed is null)
            {
                EditorTitle.Text = "SOURCE SETUP";
                EditorHealth.Text = "";
                NameBox.Text = LocationBox.Text = "";
                KindBox.ItemsSource = null;
                MappingPanel.Children.Clear();
                ValueList.ItemsSource = null;
                ViewerSummary.Text = "Add a source to begin.";
                ErrorText.Text = "";
                return;
            }
            EditorTitle.Text = feed.Name.ToUpperInvariant();
            EditorHealth.Text = feed.Health;
            NameBox.Text = feed.Name;
            KindBox.ItemsSource = DataFeedLabels.Kinds.Select(kind => new KindChoice(kind, DataFeedLabels.Kind(kind))).ToArray();
            KindBox.SelectedItem = KindBox.Items.OfType<KindChoice>().FirstOrDefault(choice => choice.Kind == feed.Kind);
            LocationBox.Text = feed.Location;
            RefreshBox.Text = feed.RefreshSeconds.ToString();
            AutoBox.IsChecked = feed.AutoRefresh;
            EnabledBox.IsChecked = feed.Enabled;
            UpdateLocationLabel(feed.Kind);
            BuildMappings(feed);
            ValueList.ItemsSource = feed.Values.Select(value => new ValueRow(value.Key, value.Value)).ToArray();
            ViewerSummary.Text = feed.Values.Count == 0
                ? "No rows yet. Press Test / refresh."
                : $"{feed.Values.Count} row{(feed.Values.Count == 1 ? "" : "s")} · {feed.Health}";
            ErrorText.Text = feed.LastError;
            PinNameBox.Text = feed.Mappings.FirstOrDefault(mapping => mapping.Target == DataFeedTarget.CustomValue)?.CustomName
                ?? feed.Values.FirstOrDefault()?.Key ?? "Feed value";
        }
        finally { _loading = wasLoading; }
    }

    void BuildMappings(DataFeed feed)
    {
        MappingPanel.Children.Clear();
        foreach (var mapping in feed.Mappings)
            MappingPanel.Children.Add(BuildMappingRow(feed, mapping));
        if (feed.Mappings.Count == 0)
            MappingPanel.Children.Add(new TextBlock
            {
                Text = "No mappings yet. Add one to send this source into the game.",
                FontSize = 10, Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            });
    }

    FrameworkElement BuildMappingRow(DataFeed feed, DataFeedMapping mapping)
    {
        var row = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("DataSurface"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("DataBorder"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 5)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var key = Field(mapping.Key, "Source key or JSON path");
        key.TextChanged += (_, _) => { mapping.Key = key.Text; SaveConfig(); };
        Grid.SetColumn(key, 0);
        grid.Children.Add(key);

        var target = new ComboBox
        {
            ItemsSource = DataFeedLabels.Targets.Select(value => new TargetChoice(value, DataFeedLabels.Target(value))).ToArray(),
            Margin = new Thickness(4, 0, 4, 4), MinHeight = 28, MinWidth = 136
        };
        target.SelectedItem = target.Items.OfType<TargetChoice>().FirstOrDefault(choice => choice.Target == mapping.Target);
        Grid.SetColumn(target, 1);
        Grid.SetColumnSpan(target, 2);
        grid.Children.Add(target);

        var custom = Field(mapping.CustomName, "Custom value name (text or number)");
        custom.TextChanged += (_, _) => { mapping.CustomName = custom.Text; SaveConfig(); };
        Grid.SetRow(custom, 1);
        Grid.SetColumn(custom, 0);
        grid.Children.Add(custom);

        var scale = Field(mapping.Scale.ToString("0.###"), "Multiply by");
        scale.TextChanged += (_, _) => { if (double.TryParse(scale.Text, out var value)) mapping.Scale = value; SaveConfig(); };
        Grid.SetRow(scale, 1);
        Grid.SetColumn(scale, 1);
        grid.Children.Add(scale);

        var offset = Field(mapping.Offset.ToString("0.###"), "Add after scaling");
        offset.TextChanged += (_, _) => { if (double.TryParse(offset.Text, out var value)) mapping.Offset = value; SaveConfig(); };
        Grid.SetRow(offset, 1);
        Grid.SetColumn(offset, 2);
        grid.Children.Add(offset);

        var remove = new Button { Content = "×", FontSize = 14, Padding = new Thickness(0), MinWidth = 24, MinHeight = 26, ToolTip = "Remove this mapping" };
        remove.Click += (_, _) => { feed.Mappings.Remove(mapping); SaveConfig(); BuildMappings(feed); };
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        target.SelectionChanged += (_, _) =>
        {
            if (target.SelectedItem is not TargetChoice choice) return;
            mapping.Target = choice.Target;
            custom.Visibility = choice.Target == DataFeedTarget.CustomValue ? Visibility.Visible : Visibility.Collapsed;
            SaveConfig();
        };
        custom.Visibility = mapping.Target == DataFeedTarget.CustomValue ? Visibility.Visible : Visibility.Collapsed;
        row.Child = grid;
        return row;
    }

    static TextBox Field(string text, string tooltip) => new()
    {
        Text = text, MinHeight = 28, MinWidth = 48, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0), ToolTip = tooltip,
        Background = System.Windows.Media.Brushes.Transparent, Foreground = System.Windows.Media.Brushes.White,
        BorderBrush = System.Windows.Media.Brushes.DimGray, VerticalContentAlignment = VerticalAlignment.Center
    };

    void Feed_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SaveConfig();
        LoadEditor(SelectedFeed);
    }

    void Editor_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || SelectedFeed is not { } feed) return;
        feed.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Untitled source" : NameBox.Text.Trim();
        if (KindBox.SelectedItem is KindChoice kind && kind.Kind != feed.Kind)
        {
            feed.Kind = kind.Kind;
            UpdateLocationLabel(kind.Kind);
        }
        feed.Location = LocationBox.Text;
        feed.RefreshSeconds = int.TryParse(RefreshBox.Text, out var seconds) ? Math.Clamp(seconds, 5, 3600) : 30;
        feed.AutoRefresh = AutoBox.IsChecked == true;
        feed.Enabled = EnabledBox.IsChecked == true;
        BrowseButton.IsEnabled = feed.Kind is DataFeedKind.Csv or DataFeedKind.Json or DataFeedKind.Xml;
        EditorTitle.Text = feed.Name.ToUpperInvariant();
        SaveConfig();
        FeedList.Items.Refresh();
    }

    void UpdateLocationLabel(DataFeedKind kind)
    {
        LocationLabel.Text = kind switch
        {
            DataFeedKind.Manual => "Key=value lines",
            DataFeedKind.Csv => "CSV path",
            DataFeedKind.Json => "JSON path",
            DataFeedKind.Xml => "XML path",
            _ => "HTTP or HTTPS address"
        };
        LocationBox.AcceptsReturn = kind == DataFeedKind.Manual;
        LocationBox.Height = kind == DataFeedKind.Manual ? 90 : double.NaN;
        BrowseButton.Visibility = kind is DataFeedKind.Csv or DataFeedKind.Json or DataFeedKind.Xml
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void SaveConfig()
    {
        if (_loading) return;
        _store.Save();
    }

    void AddFeed_Click(object sender, RoutedEventArgs e)
    {
        var feed = _store.Add();
        SaveConfig();
        RefreshView();
        FeedList.SelectedItem = feed;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    void DeleteFeed_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFeed is not { } feed) return;
        if (MessageBox.Show($"Delete the source “{feed.Name}”?", "Data sources",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _store.Remove(feed);
        SaveConfig();
        RefreshView();
    }

    void AddMapping_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFeed is not { } feed) return;
        var mapping = new DataFeedMapping
        {
            Key = feed.Values.FirstOrDefault()?.Key ?? "",
            Target = DataFeedTarget.CustomValue,
            CustomName = feed.Name
        };
        feed.Mappings.Add(mapping);
        SaveConfig();
        BuildMappings(feed);
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFeed is not { } feed) return;
        var dialog = new OpenFileDialog
        {
            Filter = feed.Kind switch
            {
                DataFeedKind.Csv => "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                DataFeedKind.Json => "JSON files (*.json)|*.json|All files (*.*)|*.*",
                _ => "XML files (*.xml)|*.xml|All files (*.*)|*.*"
            }
        };
        if (dialog.ShowDialog() == true) LocationBox.Text = dialog.FileName;
    }

    async void RefreshFeed_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFeed is not { } feed) return;
        await RefreshFeedAsync(feed, apply: false);
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFeed is not { } feed) return;
        if (feed.Values.Count == 0) await RefreshFeedAsync(feed, apply: false);
        var count = DataFeedApplier.Apply(_engine, feed);
        StatusText.Text = count == 0 ? "Nothing mapped or no matching keys." : $"Applied {count} value{(count == 1 ? "" : "s")}.";
        UpdateGameSummary();
    }

    async Task RefreshFeedAsync(DataFeed feed, bool apply)
    {
        SaveConfig();
        StatusText.Text = "Reading…";
        var (values, error) = await DataFeedReader.ReadAsync(feed, _http);
        feed.Values = values.ToList();
        feed.LastError = error ?? "";
        feed.Status = error is null ? "OK" : "Error";
        feed.LastUpdated = DateTimeOffset.Now;
        _lastPoll[feed.Id] = DateTimeOffset.Now;
        if (error is null) feed.LastError = "";
        if (feed.Enabled && apply)
        {
            var count = DataFeedApplier.Apply(_engine, feed);
            StatusText.Text = error is null ? $"Read {values.Count} rows · applied {count}." : error;
        }
        else StatusText.Text = error is null ? $"Read {values.Count} rows." : error;
        ErrorText.Text = error ?? "";
        if (ReferenceEquals(SelectedFeed, feed)) LoadEditor(feed);
        FeedList.Items.Refresh();
        UpdateGameSummary();
    }

    async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            foreach (var feed in _store.Feeds.Where(feed => feed.Enabled && feed.AutoRefresh).ToArray())
            {
                if (_lastPoll.TryGetValue(feed.Id, out var last)
                    && (DateTimeOffset.Now - last).TotalSeconds < Math.Max(5, feed.RefreshSeconds)) continue;
                await RefreshFeedAsync(feed, apply: false);
            }
        }
        finally { _polling = false; }
    }

    async void RefreshAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var feed in _store.Feeds.Where(feed => feed.Enabled).ToArray())
            await RefreshFeedAsync(feed, apply: false);
    }

    void RefreshLeague()
    {
        var filter = (LeagueFilter.Text ?? "").Trim();
        var games = (_engine.State.LeagueGames ?? [])
            .Where(game => filter.Length == 0
                || game.HomeTeam.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || game.AwayTeam.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || game.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var signature = filter + "|" + string.Join("|", games.Select(game => $"{game.GameId}:{game.HomeScore}-{game.AwayScore}:{game.Clock}"));
        if (signature == _leagueSignature) return;
        _leagueSignature = signature;
        var selected = (LeagueList.SelectedItem as LeagueGame)?.GameId;
        LeagueList.ItemsSource = null;
        LeagueList.ItemsSource = games;
        LeagueList.SelectedItem = games.FirstOrDefault(game => game.GameId == selected);
    }

    string _tickerSignature = "";

    void RefreshTicker()
    {
        RefreshTickerGameChoices();
        var items = TickerRotation.Build(_engine.State).Select(item => item.Text).ToArray();
        var signature = string.Join("\n", items);
        if (signature == _tickerSignature) return;
        _tickerSignature = signature;
        TickerList.ItemsSource = items;
        if (CloudSummary is not null)
            CloudSummary.Text = $"LEAGUE CLOUD · {(_leagueCloud?.Enabled == true ? "On" : "Off")} · "
                + $"{_engine.State.LeagueGames.Count} games · TICKER · {items.Length} card{(items.Length == 1 ? "" : "s")}";
    }

    void RefreshCloud()
    {
        if (CloudStatus is null) return;
        if (_leagueCloud is null)
        {
            CloudStatus.Text = "Off · configure the league feed";
            return;
        }
        var status = _leagueCloud.Enabled ? "On" : "Off";
        var count = _engine.State.LeagueGames.Count;
        CloudStatus.Text = $"{status} · {count} league game{(count == 1 ? "" : "s")}"
            + (string.IsNullOrWhiteSpace(_leagueCloud.LastStatus) ? "" : $" · {_leagueCloud.LastStatus}");
        if (CloudSummary is not null)
            CloudSummary.Text = $"LEAGUE CLOUD · {status} · {count} games · TICKER · {TickerList.Items.Count} cards";
    }

    void CloudConfigure_Click(object sender, RoutedEventArgs e) => OpenLeagueCloudRequested?.Invoke();

    async void CloudRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_leagueCloud is null) return;
        CloudStatus.Text = "Fetching league feed…";
        try
        {
            await _leagueCloud.FetchNowAsync();
            RefreshLeague();
        }
        catch (Exception ex)
        {
            CloudStatus.Text = $"Feed error · {ex.Message}";
        }
        RefreshCloud();
    }

    void TickerConfigure_Click(object sender, RoutedEventArgs e) => OpenTickerRequested?.Invoke();

    void LoadCloudControls()
    {
        var preferences = _leagueCloud?.Preferences;
        var available = preferences is not null;
        CloudEnabledBox.IsEnabled = CloudUploadBox.IsEnabled = CloudFeedBox.IsEnabled = available;
        CloudLeagueBox.IsEnabled = CloudPollBox.IsEnabled = CloudKeyBox.IsEnabled = available;
        if (preferences is null)
        {
            CloudStatus.Text = "League cloud is unavailable in this view.";
            return;
        }
        CloudEnabledBox.IsChecked = preferences.LeagueCloudEnabled;
        CloudUploadBox.Text = preferences.LeagueCloudUploadUrl;
        CloudFeedBox.Text = preferences.LeagueCloudFeedUrl;
        CloudLeagueBox.Text = preferences.LeagueCloudLeagueId;
        CloudPollBox.Text = preferences.LeagueCloudPollSeconds.ToString(CultureInfo.InvariantCulture);
        CloudKeyBox.Password = preferences.LeagueCloudApiKey;
    }

    bool SaveCloudControls()
    {
        if (_leagueCloud is null) return false;
        var preferences = _leagueCloud.Preferences;
        preferences.LeagueCloudEnabled = CloudEnabledBox.IsChecked == true;
        preferences.LeagueCloudUploadUrl = CloudUploadBox.Text.Trim();
        preferences.LeagueCloudFeedUrl = CloudFeedBox.Text.Trim();
        preferences.LeagueCloudLeagueId = string.IsNullOrWhiteSpace(CloudLeagueBox.Text) ? "default" : CloudLeagueBox.Text.Trim();
        preferences.LeagueCloudApiKey = CloudKeyBox.Password.Trim();
        if (int.TryParse(CloudPollBox.Text.Trim(), out var seconds))
            preferences.LeagueCloudPollSeconds = Math.Clamp(seconds, 5, 300);
        try { PreferencesStore.Save(preferences); }
        catch (Exception ex) { CloudStatus.Text = $"Could not save league settings · {ex.Message}"; return false; }
        _leagueCloud.ApplySettings();
        if (preferences.LeagueCloudEnabled) _leagueCloud.Start();
        RefreshCloud();
        return true;
    }

    void CloudSave_Click(object sender, RoutedEventArgs e)
    {
        if (SaveCloudControls()) CloudStatus.Text = "League settings saved.";
    }

    async void CloudUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_leagueCloud is null || !SaveCloudControls()) return;
        CloudStatus.Text = "Uploading…";
        await _leagueCloud.UploadNowAsync();
        RefreshCloud();
    }

    async void CloudFetch_Click(object sender, RoutedEventArgs e)
    {
        if (_leagueCloud is null || !SaveCloudControls()) return;
        CloudStatus.Text = "Fetching league feed…";
        await _leagueCloud.FetchNowAsync();
        RefreshCloud();
    }

    void LoadTickerControls()
    {
        var state = _engine.State;
        TickerAutoBox.IsChecked = state.TickerAutoRotate;
        TickerAlertsBox.IsChecked = state.TickerScoreAlerts;
        TickerDwellBox.Text = state.TickerDwellSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        TickerFadeBox.Text = state.TickerTransitionSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        TickerNewsBox.Text = string.Join(Environment.NewLine, state.TickerNews ?? []);
        _tickerControlsLoaded = true;
        _tickerGamesSignature = "";
        RefreshTickerGameChoices();
    }

    void RefreshTickerGameChoices()
    {
        if (!_tickerControlsLoaded) return;
        var games = _engine.State.LeagueGames;
        var signature = string.Join("|", games.Select(game => $"{game.GameId}:{game.TickerLine}"));
        if (signature == _tickerGamesSignature) return;
        _tickerGamesSignature = signature;
        var prior = TickerGamesPanel.Children.OfType<CheckBox>()
            .Where(box => box.Tag is string)
            .ToDictionary(box => (string)box.Tag, box => box.IsChecked == true, StringComparer.OrdinalIgnoreCase);
        TickerGamesPanel.Children.Clear();
        if (games.Count == 0)
        {
            TickerGamesHint.Text = "No league games have arrived yet. Fetch the league feed first.";
            return;
        }
        var selected = (_engine.State.TickerGameIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chooseAll = !_engine.State.TickerGamesChosen;
        TickerGamesHint.Text = "Only ticked games appear in the rotation. News still appears when no games are ticked.";
        foreach (var game in games)
        {
            var key = string.IsNullOrWhiteSpace(game.GameId) ? $"__game-{TickerGamesPanel.Children.Count}" : game.GameId;
            var check = new CheckBox
            {
                Content = game.TickerLine,
                Tag = key,
                IsEnabled = !string.IsNullOrWhiteSpace(game.GameId),
                IsChecked = string.IsNullOrWhiteSpace(game.GameId) || (prior.TryGetValue(key, out var wasChecked)
                    ? wasChecked : chooseAll || selected.Contains(game.GameId)),
                FontSize = 9, Margin = new Thickness(0, 1, 0, 1),
                Foreground = (System.Windows.Media.Brush)FindResource("DataText")
            };
            TickerGamesPanel.Children.Add(check);
        }
    }

    void SaveTickerControls()
    {
        var ids = TickerGamesPanel.Children.OfType<CheckBox>()
            .Where(box => box.IsChecked == true && box.Tag is string key && !key.StartsWith("__game-", StringComparison.Ordinal))
            .Select(box => (string)box.Tag)
            .ToArray();
        var news = (TickerNewsBox.Text ?? "")
            .Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (!double.TryParse(TickerDwellBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dwell))
            dwell = _engine.State.TickerDwellSeconds;
        if (!double.TryParse(TickerFadeBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fade))
            fade = _engine.State.TickerTransitionSeconds;
        _engine.SetTickerSettings(ids, news, TickerAutoBox.IsChecked == true, TickerAlertsBox.IsChecked == true, dwell, fade);
        TickerStatus.Text = $"Saved · {ids.Length} game{(ids.Length == 1 ? "" : "s")} · {news.Length} headline{(news.Length == 1 ? "" : "s")}";
        RefreshTicker();
    }

    void TickerSave_Click(object sender, RoutedEventArgs e) => SaveTickerControls();

    void TickerClear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var box in TickerGamesPanel.Children.OfType<CheckBox>()) box.IsChecked = false;
        SaveTickerControls();
    }

    void Compare_Click(object sender, RoutedEventArgs e)
    {
        new TeamComparisonWindow(_engine, PreferencesStore.Load().Theme) { Owner = Window.GetWindow(this) }.Show();
    }

    void LeagueFilter_Changed(object sender, TextChangedEventArgs e)
    {
        _leagueSignature = "";
        RefreshLeague();
    }

    void League_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (LeagueList.SelectedItem is LeagueGame game)
            ViewerSummary.Text = $"{game.ScoreLine} · {game.StateLine}";
    }

    void UseLeague_Click(object sender, RoutedEventArgs e)
    {
        if (LeagueList.SelectedItem is not LeagueGame game) return;
        var stat = _engine.State.Stats.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, "League matchup", StringComparison.OrdinalIgnoreCase))
            ?? _engine.AddStat("League matchup");
        _engine.SetStatText(stat.Id, $"{game.AwayTeam} {game.AwayScore} @ {game.HomeTeam} {game.HomeScore}");
        StatusText.Text = $"League matchup pinned · {game.AwayTeam} at {game.HomeTeam}";
        UpdateGameSummary();
    }

    void Value_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ValueList.SelectedItem is not ValueRow row) return;
        if (PinNameBox.Text is "" or "Feed value") PinNameBox.Text = row.Key;
        ViewerSummary.Text = $"{row.Key} = {row.Value}";
    }

    void SendStat_Click(object sender, RoutedEventArgs e)
    {
        if (ValueList.SelectedItem is not ValueRow row) return;
        var name = string.IsNullOrWhiteSpace(PinNameBox.Text) ? row.Key : PinNameBox.Text.Trim();
        var stat = _engine.State.Stats.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) ?? _engine.AddStat(name);
        _engine.SetStatText(stat.Id, row.Value);
        StatusText.Text = $"{name} = {row.Value} on the graphic.";
        UpdateGameSummary();
    }

    void UpdateGameSummary()
    {
        if (GameSummary is null) return;
        var state = _engine.State;
        var home = state.HomePlayer is null ? "—" : $"{state.HomePlayer.Name} {state.HomePlayer.Number}".Trim();
        var away = state.AwayPlayer is null ? "—" : $"{state.AwayPlayer.Name} {state.AwayPlayer.Number}".Trim();
        GameSummary.Text = $"{state.AwayAbbreviation} {state.AwayScore} — {state.HomeScore} {state.HomeAbbreviation}\n"
            + $"{PeriodDisplay.Format(state)} · {state.GameClockText} · {state.Stats.Count} custom values\n"
            + $"On graphic: {home} / {away}";
        if (SelectedFeed is { } feed)
        {
            EditorHealth.Text = feed.Health;
            ViewerSummary.Text = feed.Values.Count == 0
                ? "No rows yet. Press Test / refresh."
                : $"{feed.Values.Count} row{(feed.Values.Count == 1 ? "" : "s")} · {feed.Health}";
        }
        RefreshLeague();
        RefreshTicker();
        RefreshCloud();
    }

    /// <summary>The timer is owned by this control; close the tab's window and it is released.</summary>
    public void Stop()
    {
        _poll.Stop();
        _engine.Changed -= UpdateGameSummary;
        _http.Dispose();
    }
}
