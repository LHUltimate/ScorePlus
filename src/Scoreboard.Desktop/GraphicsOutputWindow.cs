using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The dedicated graphics-output role. It renders the published layout (or a pinned one) through
/// the same renderer as every other surface, then exposes a programme JPEG and separate fill/key
/// PNGs from the local headend. The console still owns the data; this window only renders and
/// publishes it.
/// </summary>
public sealed class GraphicsOutputWindow : Window
{
    readonly ScorebugCatalog _catalog;
    readonly GameEngine _engine;
    readonly MediaPlayback? _playback;
    readonly VenueStore _venue;
    readonly BroadcastHeadend _headend = BroadcastHeadend.Shared;
    readonly DispatcherTimer _render = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly ComboBox _layoutBox = new() { MinHeight = 30, Margin = new Thickness(0, 0, 0, 7) };
    readonly ComboBox _standardBox = new() { MinHeight = 30, Margin = new Thickness(0, 0, 0, 7) };
    readonly TextBox _safeAction = new() { Text = "90", MinHeight = 28, Width = 58 };
    readonly TextBox _safeTitle = new() { Text = "80", MinHeight = 28, Width = 58 };
    readonly Image _fillImage = new() { Stretch = Stretch.Uniform };
    readonly Image _keyImage = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _fillUrl = new() { FontFamily = new FontFamily("Consolas"), FontSize = 10, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _keyUrl = new() { FontFamily = new FontFamily("Consolas"), FontSize = 10, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _streamUrl = new() { FontFamily = new FontFamily("Consolas"), FontSize = 10, TextWrapping = TextWrapping.Wrap };
    readonly Button _startButton = new() { Content = "Start output", MinHeight = 36, MinWidth = 110 };
    readonly TextBlock _frames = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)) };
    readonly string _theme;
    bool _running;
    bool _rendering;
    long _frameCount;
    long _missed;
    double _lastRenderMs;
    string _outputId = "router-fill-key";
    DateTime _lastTick = DateTime.UtcNow;

    sealed record LayoutChoice(string LayoutId, string Label)
    {
        public override string ToString() => Label;
    }

    sealed record StandardChoice(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    public GraphicsOutputWindow(ScorebugCatalog catalog, GameEngine engine, MediaPlayback? playback,
        VenueStore venue, string theme = "Dark")
    {
        _catalog = catalog;
        _engine = engine;
        _playback = playback;
        _venue = venue;
        _theme = theme;
        Title = "ScorePlus — Graphics Output";
        Width = 1180;
        Height = 760;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x12, 0x18));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB));
        Content = BuildChrome();
        ThemeManager.Apply(this, theme);
        _layoutBox.SelectionChanged += (_, _) => SelectLayout();
        _standardBox.SelectionChanged += (_, _) => SelectStandard();
        _startButton.Click += (_, _) => ToggleOutput();
        _engine.Changed += EngineChanged;
        _catalog.Changed += CatalogChanged;
        _render.Tick += (_, _) => RenderFrame();
        Loaded += (_, _) => { RefreshChoices(); _render.Start(); };
        Closed += (_, _) =>
        {
            _render.Stop();
            _engine.Changed -= EngineChanged;
            _catalog.Changed -= CatalogChanged;
            _headend.Unregister(_outputId);
        };
    }

    public bool IsOutputRunning => _running;
    public int RenderedFrames => (int)_frameCount;
    public double LastRenderMilliseconds => _lastRenderMs;
    public string OutputId => _outputId;

    UIElement BuildChrome()
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 10), LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _startButton.Margin = new Thickness(0, 0, 6, 0);
        actions.Children.Add(_startButton);
        var close = new Button { Content = "Close", MinHeight = 36, MinWidth = 70 };
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xC4, 0x0F)),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0, 0, 9, 0),
            Child = new TextBlock { Text = "GFX", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.Black }
        });
        title.Children.Add(new TextBlock { Text = "GRAPHICS OUTPUT · FILL + KEY", FontSize = 17, FontWeight = FontWeights.Bold });
        title.Children.Add(new TextBlock
        {
            Text = "   the router feed, triggered by the same live data",
            Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)), VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(title);
        root.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        body.Children.Add(PreviewCard("FILL", _fillImage, 0));
        body.Children.Add(PreviewCard("KEY · MATTE", _keyImage, 2));
        var side = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x25)),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(12)
        };
        Grid.SetColumn(side, 4);
        var panel = new StackPanel();
        panel.Children.Add(Section("OUTPUT LAYOUT"));
        panel.Children.Add(_layoutBox);
        panel.Children.Add(Section("STANDARD"));
        panel.Children.Add(_standardBox);
        var safeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        safeRow.Children.Add(new TextBlock { Text = "SAFE ACTION", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)), VerticalAlignment = VerticalAlignment.Center, Width = 86 });
        safeRow.Children.Add(_safeAction);
        safeRow.Children.Add(new TextBlock { Text = "%", Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) });
        safeRow.Children.Add(new TextBlock { Text = "TITLE", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)), VerticalAlignment = VerticalAlignment.Center, Width = 42 });
        safeRow.Children.Add(_safeTitle);
        safeRow.Children.Add(new TextBlock { Text = "%", Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        panel.Children.Add(safeRow);
        var apply = new Button { Content = "Apply to this display channel", MinHeight = 32 };
        apply.Click += (_, _) => ApplyProfile();
        panel.Children.Add(apply);
        panel.Children.Add(Section("LOCAL SIGNAL"));
        panel.Children.Add(UrlRow("FILL", _fillUrl));
        panel.Children.Add(UrlRow("KEY", _keyUrl));
        panel.Children.Add(UrlRow("PROGRAMME", _streamUrl));
        panel.Children.Add(new TextBlock
        {
            Text = "Point a router, capture device, VLC or OBS at these URLs. Fill is straight colour; key is a white-on-black matte.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x8A, 0x9C)), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0)
        });
        panel.Children.Add(Section("HEALTH"));
        panel.Children.Add(_frames);
        side.Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(side);
        root.Children.Add(body);
        Grid.SetRow(body, 1);

        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x25)),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 10, 0, 0),
            Child = _status
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    Border PreviewCard(string title, Image image, int column)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x25)),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(9)
        };
        Grid.SetColumn(card, column);
        var stack = new DockPanel { LastChildFill = true };
        var label = new TextBlock
        {
            Text = title, FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)), Margin = new Thickness(1, 0, 1, 6)
        };
        DockPanel.SetDock(label, Dock.Top);
        stack.Children.Add(label);
        var frame = new Border
        {
            Background = Brushes.Black, BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x35, 0x42)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), ClipToBounds = true,
            Child = image
        };
        stack.Children.Add(frame);
        card.Child = stack;
        return card;
    }

    static TextBlock Section(string text) => new()
    {
        Text = text, FontSize = 10, FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)),
        Margin = new Thickness(0, 6, 0, 4)
    };

    static UIElement UrlRow(string label, TextBlock url)
    {
        var host = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        host.Children.Add(new TextBlock
        {
            Text = label, FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2))
        });
        host.Children.Add(url);
        return host;
    }

    void RefreshChoices()
    {
        var previous = (_layoutBox.SelectedItem as LayoutChoice)?.LayoutId
            ?? _venue.State.Broadcast.GraphicsLayoutId;
        var choices = new List<LayoutChoice>();
        foreach (var display in _catalog.Displays)
            foreach (var layout in display.Layouts)
                choices.Add(new LayoutChoice(layout.Id, $"{display.Name} · {layout.Name}"));
        _layoutBox.ItemsSource = choices;
        var selected = choices.FirstOrDefault(choice => choice.LayoutId == previous)
            ?? choices.FirstOrDefault(choice => choice.LayoutId == _catalog.PublishedLayoutOf(_catalog.ActiveDisplay.Id).Id)
            ?? choices.FirstOrDefault();
        _layoutBox.SelectedItem = selected;
        _standardBox.ItemsSource = BroadcastStandards.All.Select(item => new StandardChoice(item.Key, item.Label)).ToArray();
        SyncProfile();
        RefreshUrls();
    }

    void SelectLayout()
    {
        SyncProfile();
        if (_layoutBox.SelectedItem is LayoutChoice choice)
        {
            _venue.State.Broadcast.GraphicsLayoutId = choice.LayoutId;
            _venue.Save();
        }
    }

    /// <summary>The broadcast channel belongs to the display that owns the rendered layout.</summary>
    ScorebugDisplay? CurrentDisplay() => CurrentLayout() is { } layout ? _catalog.FindDisplayOfLayout(layout.Id) : null;

    void SelectStandard()
    {
        if (_standardBox.SelectedItem is StandardChoice choice && CurrentDisplay() is { } display)
        {
            _catalog.UpdateDisplay(display.Id, edit =>
            {
                edit.Broadcast ??= new BroadcastProfile();
                edit.Broadcast.Standard = choice.Key;
                edit.Broadcast.Normalize();
            });
            ApplyProfile();
        }
    }

    void SyncProfile()
    {
        if (CurrentDisplay() is not { } display) return;
        var profile = display.Broadcast ?? new BroadcastProfile();
        profile.Normalize();
        var standard = _standardBox.Items.OfType<StandardChoice>()
            .FirstOrDefault(choice => choice.Key == profile.Standard);
        _standardBox.SelectedItem = standard;
        if (!_safeAction.IsKeyboardFocusWithin) _safeAction.Text = profile.SafeActionPercent.ToString("0");
        if (!_safeTitle.IsKeyboardFocusWithin) _safeTitle.Text = profile.SafeTitlePercent.ToString("0");
    }

    void ApplyProfile()
    {
        if (CurrentDisplay() is not { } display) return;
        var selected = _standardBox.SelectedItem as StandardChoice;
        var current = display.Broadcast ?? new BroadcastProfile();
        var action = double.TryParse(_safeAction.Text, out var actionValue) ? actionValue : current.SafeActionPercent;
        var title = double.TryParse(_safeTitle.Text, out var titleValue) ? titleValue : current.SafeTitlePercent;
        _catalog.UpdateDisplay(display.Id, edit =>
        {
            edit.Broadcast ??= new BroadcastProfile();
            edit.Broadcast.Standard = selected?.Key ?? edit.Broadcast.Standard;
            edit.Broadcast.SafeActionPercent = action;
            edit.Broadcast.SafeTitlePercent = title;
            edit.Broadcast.Normalize();
        });
        _status.Text = $"BROADCAST CHANNEL · {display.Name} · {selected?.Label ?? "standard"} · safe {action:0}/{title:0}%";
        SyncProfile();
    }

    ScorebugLayout? CurrentLayout()
    {
        if (_layoutBox.SelectedItem is LayoutChoice choice)
            return _catalog.Displays.SelectMany(display => display.Layouts)
                .FirstOrDefault(layout => layout.Id == choice.LayoutId);
        return _catalog.PublishedLayoutOf(_catalog.ActiveDisplay.Id);
    }

    void ToggleOutput()
    {
        _running = !_running;
        _startButton.Content = _running ? "Stop output" : "Start output";
        if (_running)
        {
            var settings = _venue.State.Broadcast;
            settings.Normalize();
            if (!_headend.Start(settings.PreferredPort))
            {
                _running = false;
                _startButton.Content = "Start output";
                _status.Text = "OUTPUT ERROR · " + (_headend.Error ?? "the local headend could not start");
                return;
            }
            var destination = settings.Destinations.FirstOrDefault(item => item.Kind == BroadcastDestinationKind.FillKey);
            if (destination is null)
            {
                destination = new BroadcastDestination { Name = "Router fill + key", Kind = BroadcastDestinationKind.FillKey };
                settings.Destinations.Add(destination);
                _venue.Save();
            }
            destination.Normalize();
            _outputId = destination.Id;
            var channelDisplay = CurrentDisplay();
            if (channelDisplay is not null) destination.DisplayId = channelDisplay.Id;
            _headend.Register(_outputId, destination.Name, destination.Kind, CurrentLayout()?.Name ?? "Published layout",
                channelDisplay?.Name ?? "Program");
            _venue.Save();
            _status.Text = "OUTPUT ON · fill and key are live · " + (_headend.IsRunning ? _headend.BaseUrl : "headend unavailable");
        }
        else
        {
            _status.Text = "OUTPUT STOPPED · the last fill/key frame is still on the local headend";
        }
        RefreshUrls();
        RenderFrame();
    }

    void RefreshUrls()
    {
        if (CurrentLayout() is not { } layout) return;
        var destination = _venue.State.Broadcast.Destinations.FirstOrDefault(item => item.Id == _outputId)
            ?? _venue.State.Broadcast.Destinations.FirstOrDefault(item => item.Kind == BroadcastDestinationKind.FillKey);
        if (destination is not null) _outputId = destination.Id;
        _fillUrl.Text = _headend.FillUrl(_outputId);
        _keyUrl.Text = _headend.KeyUrl(_outputId);
        _streamUrl.Text = _headend.StreamUrl(_outputId);
        _fillUrl.ToolTip = _fillUrl.Text;
        _keyUrl.ToolTip = _keyUrl.Text;
        _streamUrl.ToolTip = _streamUrl.Text;
    }

    void RenderFrame()
    {
        if (!_running || _rendering) return;
        if (CurrentLayout() is not { } layout) return;
        _rendering = true;
        try
        {
            var frame = BroadcastRenderer.Render(_catalog, _engine, _playback, layout);
            _fillImage.Source = ImageFrom(frame.FillPng);
            _keyImage.Source = ImageFrom(frame.KeyPng);
            _headend.SetFrames(_outputId, frame);
            _lastRenderMs = frame.Milliseconds;
            _headend.NoteRender(frame.Milliseconds);
            _frameCount++;
            var now = DateTime.UtcNow;
            var expected = 0.25;
            var elapsed = (now - _lastTick).TotalSeconds;
            if (elapsed > expected * 2.2) _missed += (long)(elapsed / expected) - 1;
            _lastTick = now;
            var destination = _venue.State.Broadcast.Destinations.FirstOrDefault(item => item.Id == _outputId);
            if (destination is not null)
            {
                _headend.RefreshDestination(destination);
                _frames.Text = $"FILL + KEY · {frame.Width}×{frame.Height}\n"
                    + $"{_lastRenderMs:0.0} ms render · {_frameCount} frames · {_missed} dropped\n"
                    + $"{destination.StateLabel} · genlock {_venue.State.Broadcast.GenlockSource}\n"
                    + $"viewers {_headend.ViewerCount(_outputId)}";
            }
        }
        catch (Exception ex)
        {
            _status.Text = "OUTPUT ERROR · " + ex.Message;
        }
        finally { _rendering = false; }
    }

    static ImageSource ImageFrom(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    void EngineChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(EngineChanged); return; }
        // The render timer owns the actual frame; the event is here so a score change is reflected
        // on the next tick rather than waiting for the next clock tick.
    }

    void CatalogChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(CatalogChanged); return; }
        if (_layoutBox.SelectedItem is null) RefreshChoices();
    }
}
