using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The house routing every camera starts on. It chooses the displays and venue channels a take
/// reaches when a camera says "Default (programme)".
/// </summary>
public sealed class ProgrammeRouteWindow : Window
{
    readonly List<(string Id, CheckBox Box)> _displayChecks = [];
    readonly List<(string Id, CheckBox Box)> _channelChecks = [];
    readonly CheckBox _allDisplays = new() { Content = "All displays", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) };
    readonly CheckBox _allChannels = new() { Content = "All channels", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) };

    static Brush Solid(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    public ProgrammeRouteWindow(ProgrammeRoute draft, IReadOnlyList<ScorebugDisplay> displays,
        IReadOnlyList<VenueChannel> channels)
    {
        Title = "ScorePlus — Programme Routing";
        Width = 580;
        Height = 610;
        MinWidth = 480;
        MinHeight = 480;
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
            Text = "PROGRAMME ROUTING", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Solid("#8DEBCB")
        });
        header.Children.Add(new TextBlock
        {
            Text = "Every camera set to Default follows these displays and channels. A camera can still override them.",
            FontSize = 10, Foreground = Solid("#8C9AAC"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0)
        });
        root.Children.Add(header);

        var body = new StackPanel();
        body.Children.Add(Section("DISPLAYS", "Every chosen display shows the programme camera full-frame when it is taken."));
        body.Children.Add(_allDisplays);
        var displayPanel = new WrapPanel();
        foreach (var display in displays)
        {
            var box = new CheckBox
            {
                Content = display.Name, Tag = display.Id, FontSize = 11,
                Margin = new Thickness(0, 0, 14, 4),
                IsChecked = draft.AllDisplays || draft.Displays.Contains(display.Id, StringComparer.OrdinalIgnoreCase)
            };
            _displayChecks.Add((display.Id, box));
            displayPanel.Children.Add(box);
        }
        body.Children.Add(displayPanel);
        if (displays.Count == 0) body.Children.Add(Empty("No displays in this show yet."));

        body.Children.Add(Section("VENUE CHANNELS", "Every chosen channel's stream follows the programme camera."));
        body.Children.Add(_allChannels);
        var channelPanel = new WrapPanel();
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
            channelPanel.Children.Add(box);
        }
        body.Children.Add(channelPanel);
        if (channels.Count == 0) body.Children.Add(Empty("No venue channels configured. Add them in Venue."));

        _allDisplays.IsChecked = draft.AllDisplays;
        _allChannels.IsChecked = draft.AllChannels;
        _allDisplays.Checked += (_, _) => SyncEnabled();
        _allDisplays.Unchecked += (_, _) => SyncEnabled();
        _allChannels.Checked += (_, _) => SyncEnabled();
        _allChannels.Unchecked += (_, _) => SyncEnabled();
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
    }

    public ProgrammeRoute? Result { get; private set; }

    void SyncEnabled()
    {
        var displays = _allDisplays.IsChecked != true;
        var channels = _allChannels.IsChecked != true;
        foreach (var (_, box) in _displayChecks) box.IsEnabled = displays;
        foreach (var (_, box) in _channelChecks) box.IsEnabled = channels;
    }

    void Finish(bool? clear)
    {
        if (clear is null) { Close(); return; }
        if (clear == true)
        {
            _allDisplays.IsChecked = false;
            _allChannels.IsChecked = false;
            foreach (var (_, box) in _displayChecks) box.IsChecked = false;
            foreach (var (_, box) in _channelChecks) box.IsChecked = false;
        }
        Result = new ProgrammeRoute
        {
            AllDisplays = _allDisplays.IsChecked == true,
            Displays = _displayChecks.Where(check => check.Box.IsChecked == true).Select(check => check.Id).ToList(),
            AllChannels = _allChannels.IsChecked == true,
            Channels = _channelChecks.Where(check => check.Box.IsChecked == true).Select(check => check.Id).ToList()
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
        Text = text, FontSize = 10, Foreground = Solid("#7C8A9C"), TextWrapping = TextWrapping.Wrap
    };
}
