using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The routing editor for one Camera Switch input. Displays, venue channels and tags are all
/// multi-select, and each group has an "All" choice for a camera that should reach everything.
/// </summary>
public sealed class CameraRouteWindow : Window
{
    readonly List<(string Id, CheckBox Box)> _displayChecks = [];
    readonly List<(string Id, CheckBox Box)> _channelChecks = [];
    readonly List<(string Tag, CheckBox Box)> _tagChecks = [];
    readonly CheckBox _defaultDisplays = new() { Content = "Default (programme)", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
    readonly CheckBox _allDisplays = new() { Content = "All displays", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) };
    readonly CheckBox _defaultChannels = new() { Content = "Default (programme)", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
    readonly CheckBox _allChannels = new() { Content = "All channels", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) };
    readonly CheckBox _allTags = new() { Content = "All tags (GLOBAL)", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) };
    readonly ProgrammeRoute _programme;
    readonly WrapPanel _displayPanel = new();
    readonly WrapPanel _channelPanel = new();
    readonly WrapPanel _tagPanel = new();
    readonly TextBox _tagInput = new() { MinHeight = 28, Margin = new Thickness(0, 6, 6, 0), Width = 210 };
    readonly TextBlock _tagHint = new() { FontSize = 9, Foreground = Solid("#7C8A9C"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    readonly int _cameraIndex;
    readonly int _displayCount;
    readonly int _channelCount;

    static Brush Solid(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    public CameraRouteWindow(MediaEntry camera, CameraRoute draft, IReadOnlyList<ScorebugDisplay> displays,
        IReadOnlyList<VenueChannel> channels, ProgrammeRoute? programme = null)
    {
        _cameraIndex = camera.CameraIndex;
        _displayCount = displays.Count;
        _channelCount = channels.Count;
        _programme = programme?.Copy() ?? new ProgrammeRoute();
        Title = $"ScorePlus — Route {camera.Label}";
        Width = 620;
        Height = 690;
        MinWidth = 520;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Solid("#050505");
        Foreground = Solid("#F4F7FB");
        FontFamily = new FontFamily("Segoe UI");
        WindowOwnerGuard.Attach(this);

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = "CAMERA ROUTING", FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = Solid("#8DEBCB")
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{camera.Label} · CAM {camera.CameraIndex + 1} · pick any number of displays, channels and tags, or route to all.",
            FontSize = 10, Foreground = Solid("#8C9AAC"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0)
        });
        root.Children.Add(header);

        var body = new StackPanel();
        body.Children.Add(Section("DISPLAYS", "Default follows the programme routing. Otherwise every chosen display shows this camera full-frame when it is taken."));
        body.Children.Add(_defaultDisplays);
        body.Children.Add(_allDisplays);
        body.Children.Add(_displayPanel);
        foreach (var display in displays)
        {
            var box = new CheckBox
            {
                Content = display.Name, Tag = display.Id, FontSize = 11,
                Margin = new Thickness(0, 0, 14, 4),
                IsChecked = draft.AllDisplays || draft.Displays.Contains(display.Id, StringComparer.OrdinalIgnoreCase)
            };
            _displayChecks.Add((display.Id, box));
            _displayPanel.Children.Add(box);
        }
        if (displays.Count == 0)
            body.Children.Add(Empty("No displays in this show yet."));

        body.Children.Add(Section("VENUE CHANNELS", "Default follows the programme routing. Otherwise the chosen channels are re-pointed at this camera."));
        body.Children.Add(_defaultChannels);
        body.Children.Add(_allChannels);
        body.Children.Add(_channelPanel);
        foreach (var channel in channels)
        {
            var display = displays.FirstOrDefault(candidate => candidate.Id == channel.DisplayId);
            var box = new CheckBox
            {
                Content = display is null ? channel.Name : $"{channel.Name} · {display.Name}",
                Tag = channel.Id, FontSize = 11, Margin = new Thickness(0, 0, 14, 4),
                IsChecked = draft.AllChannels || draft.Channels.Contains(channel.Id, StringComparer.OrdinalIgnoreCase)
            };
            _channelChecks.Add((channel.Id, box));
            _channelPanel.Children.Add(box);
        }
        if (channels.Count == 0)
            body.Children.Add(Empty("No venue channels configured. Add them in Venue."));

        body.Children.Add(Section("TAGS", "A scorebug video box shows this camera when its tag matches any chosen tag."));
        body.Children.Add(_allTags);
        body.Children.Add(_tagPanel);
        var knownTags = MediaLibrary.SuggestedTags.Where(tag => !MediaLibrary.IsAnyTag(tag)).ToList();
        foreach (var tag in draft.Tags.Where(tag => !knownTags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            knownTags.Add(tag);
        foreach (var tag in knownTags)
        {
            var box = new CheckBox
            {
                Content = tag, Tag = tag, FontSize = 11, Margin = new Thickness(0, 0, 14, 4),
                IsChecked = draft.AllTags || draft.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)
            };
            _tagChecks.Add((tag, box));
            _tagPanel.Children.Add(box);
        }
        var addRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var add = new Button { Content = "ADD TAG", MinHeight = 28, MinWidth = 82, Margin = new Thickness(0, 6, 0, 0) };
        add.Click += (_, _) => AddTag(_tagInput.Text);
        _tagInput.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            AddTag(_tagInput.Text);
            e.Handled = true;
        };
        DockPanel.SetDock(add, Dock.Right);
        addRow.Children.Add(add);
        addRow.Children.Add(_tagInput);
        body.Children.Add(addRow);
        body.Children.Add(_tagHint);
        _tagHint.Text = "Type a tag the list does not show, then press ADD TAG. Tags are comma-safe.";

        _defaultDisplays.IsChecked = draft.UseProgrammeDisplays;
        _defaultChannels.IsChecked = draft.UseProgrammeChannels;
        _allDisplays.IsChecked = draft.AllDisplays;
        _allChannels.IsChecked = draft.AllChannels;
        _allTags.IsChecked = draft.AllTags;
        foreach (var box in new[] { _defaultDisplays, _allDisplays, _defaultChannels, _allChannels, _allTags })
        {
            box.Checked += (_, _) => SyncEnabled();
            box.Unchecked += (_, _) => SyncEnabled();
        }
        SyncEnabled();

        var scroll = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        var save = new Button
        {
            Content = "SAVE ROUTING", MinHeight = 34, MinWidth = 128, FontWeight = FontWeights.Bold,
            Background = Solid("#12352C"), Foreground = Solid("#8DEBCB"), BorderBrush = Solid("#2E6F5D")
        };
        save.Click += (_, _) => Finish(clear: false);
        var cancel = new Button { Content = "CANCEL", MinHeight = 34, MinWidth = 82, Margin = new Thickness(6, 0, 0, 0) };
        cancel.Click += (_, _) => Finish(clear: null);
        var clear = new Button { Content = "CLEAR ROUTING", MinHeight = 34, MinWidth = 118 };
        clear.Click += (_, _) => Finish(clear: true);
        DockPanel.SetDock(clear, Dock.Left);
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(clear);
        footer.Children.Add(save);
        footer.Children.Add(cancel);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        ThemeManager.SetSkipTheme(this, true);
        Loaded += (_, _) => ThemeManager.SetSkipTheme(this, true);
    }

    /// <summary>The route the operator saved, or null when the dialog was cancelled.</summary>
    public CameraRoute? Result { get; private set; }

    void AddTag(string? text)
    {
        var tag = MediaLibrary.NormalizeTag(text);
        if (string.IsNullOrWhiteSpace(text) || MediaLibrary.IsAnyTag(tag))
        {
            _tagHint.Text = "Type a tag name first. GLOBAL is the All tags choice above.";
            return;
        }
        var existing = _tagChecks.FirstOrDefault(check => string.Equals(check.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (existing.Box is not null)
        {
            existing.Box.IsChecked = true;
            _tagHint.Text = $"{tag} is already in the list and is now selected.";
        }
        else
        {
            var box = new CheckBox
            {
                Content = tag, Tag = tag, FontSize = 11, IsChecked = true,
                Margin = new Thickness(0, 0, 14, 4)
            };
            _tagChecks.Add((tag, box));
            _tagPanel.Children.Add(box);
            _tagHint.Text = $"{tag} added and selected.";
        }
        _tagInput.Text = "";
        SyncEnabled();
    }

    void SyncEnabled()
    {
        var ownDisplays = _defaultDisplays.IsChecked != true;
        var ownChannels = _defaultChannels.IsChecked != true;
        var displays = ownDisplays && _allDisplays.IsChecked != true;
        var channels = ownChannels && _allChannels.IsChecked != true;
        var tags = _allTags.IsChecked != true;
        _allDisplays.IsEnabled = ownDisplays;
        _allChannels.IsEnabled = ownChannels;
        foreach (var (_, box) in _displayChecks) box.IsEnabled = displays;
        foreach (var (_, box) in _channelChecks) box.IsEnabled = channels;
        foreach (var (_, box) in _tagChecks) box.IsEnabled = tags;
        _tagPanel.IsEnabled = tags;
        _tagInput.IsEnabled = tags;
        _allTags.Foreground = tags ? Solid("#F4F7FB") : Solid("#8DEBCB");
        _defaultDisplays.Foreground = ownDisplays ? Solid("#F4F7FB") : Solid("#8DEBCB");
        _defaultChannels.Foreground = ownChannels ? Solid("#F4F7FB") : Solid("#8DEBCB");
        _defaultDisplays.ToolTip = $"Programme default · {_programme.DisplaySummary(_displayCount)}";
        _defaultChannels.ToolTip = $"Programme default · {_programme.ChannelSummary(_channelCount)}";
    }

    void Finish(bool? clear)
    {
        if (clear is null)
        {
            Close();
            return;
        }
        if (clear == true)
        {
            _allDisplays.IsChecked = false;
            _allChannels.IsChecked = false;
            _allTags.IsChecked = false;
            foreach (var (_, box) in _displayChecks) box.IsChecked = false;
            foreach (var (_, box) in _channelChecks) box.IsChecked = false;
            foreach (var (_, box) in _tagChecks) box.IsChecked = false;
        }
        Result = new CameraRoute
        {
            CameraIndex = _cameraIndex,
            UseProgrammeDisplays = _defaultDisplays.IsChecked == true,
            UseProgrammeChannels = _defaultChannels.IsChecked == true,
            AllDisplays = _allDisplays.IsChecked == true,
            Displays = _displayChecks.Where(check => check.Box.IsChecked == true).Select(check => check.Id).ToList(),
            AllChannels = _allChannels.IsChecked == true,
            Channels = _channelChecks.Where(check => check.Box.IsChecked == true).Select(check => check.Id).ToList(),
            AllTags = _allTags.IsChecked == true,
            Tags = _tagChecks.Where(check => check.Box.IsChecked == true).Select(check => check.Tag).ToList()
        };
        Result.Normalize();
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); }
    }

    static TextBlock Section(string title, string hint) => new()
    {
        Text = $"{title}\n{hint}", FontSize = 10, FontWeight = FontWeights.Bold,
        Foreground = Solid("#B8C4D4"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 5)
    };

    static TextBlock Empty(string text) => new()
    {
        Text = text, FontSize = 10, Foreground = Solid("#7C8A9C"), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 2)
    };
}
