using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The camera-only Live Switch. It follows the same grammar as <see cref="LiveSwitchWindow"/> —
/// an eight-box multiview, programme and preview monitors, a source strip and a take/cut panel —
/// but every source is a capture device instead of a layout. A camera is loaded into Fill A or
/// Fill B; the take panel puts that fill on air and publishes the same picture through
/// <see cref="LiveClipsBus"/> for the Show page monitors.
/// </summary>
public sealed class CameraSwitchWindow : Window
{
    readonly MediaSession? _session;
    readonly MediaPlayback? _playback;
    readonly ScorebugCatalog? _catalog;
    readonly VenueStore? _venue;
    readonly UserPreferences _preferences;
    readonly UniformGrid _multiview = new() { Rows = 2, Columns = 4 };
    readonly WrapPanel _cameraPanel = new() { Margin = new Thickness(0, 2, 0, 0) };
    readonly TextBlock _status = new() { FontSize = 11, Foreground = Solid("#B8C4D4"), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _fillAState = new() { FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Solid("#8DEBCB"), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _fillBState = new() { FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Solid("#8DEBCB"), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _onAir = new() { FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Solid("#FF9B9B") };
    readonly TextBlock _multiviewPage = new() { FontSize = 10, Foreground = Solid("#7C8A9C"), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _routeLabel = new() { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Solid("#8DEBCB"), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _routeSummary = new() { FontSize = 10, Foreground = Solid("#8C9AAC"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    readonly Image _fillAImage = new() { Stretch = Stretch.Uniform };
    readonly Image _fillBImage = new() { Stretch = Stretch.Uniform };
    Border _fillACard = null!;
    Border _fillBCard = null!;
    Border _targetABorder = null!;
    Border _targetBBorder = null!;
    Button _takeA = null!;
    Button _takeB = null!;
    readonly List<(CameraFeed Feed, Action<BitmapSource> Handler)> _wallHandlers = [];
    readonly List<TileState> _tileStates = [];
    (CameraFeed? Feed, Action<BitmapSource>? Handler) _aFeed;
    (CameraFeed? Feed, Action<BitmapSource>? Handler) _bFeed;
    MediaEntry? _entryA;
    MediaEntry? _entryB;
    int _target;
    int _onAirFill = -1;
    string _cameraSignature = "\u0000";

    sealed record TileState(MediaEntry Entry, Border ABadge, Border BBadge, Button RouteButton);

    static Brush Solid(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    public CameraSwitchWindow(MediaSession? session, MediaPlayback? playback, ScorebugCatalog? catalog = null,
        VenueStore? venue = null, UserPreferences? preferences = null)
    {
        _session = session;
        _playback = playback;
        _catalog = catalog;
        _venue = venue;
        _preferences = preferences ?? PreferencesStore.Load();
        Title = "ScorePlus — Live Camera Switch";
        Width = 1360;
        Height = 900;
        MinWidth = 1080;
        MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Match Live Clips and the rest of the workspace: neutral black, not the old blue-black
        // operator panels. The camera wall and switch bays are all built from this palette.
        Background = Solid("#050505");
        Foreground = Solid("#F4F7FB");
        FontFamily = new FontFamily("Segoe UI");
        WindowOwnerGuard.Attach(this);

        var root = new Grid { Margin = new Thickness(14), Background = Solid("#050505") };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 10), LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var returnLive = new Button { Content = "RETURN TO LIVE", MinHeight = 32, MinWidth = 126, Margin = new Thickness(0, 0, 6, 0) };
        returnLive.Click += (_, _) =>
        {
            _session?.ReturnToLive();
            CameraRouting.Clear();
            _onAirFill = -1;
            _onAir.Text = "PROGRAMME · layout returned";
            LiveClipsBus.Shared.SetLive("Layout", null);
            _status.Text = "Returned the programme to the published layout.";
            RefreshFillChrome();
        };
        var clear = new Button { Content = "CLEAR TARGET", MinHeight = 32, MinWidth = 110 };
        clear.Click += (_, _) => Clear(_target);
        actions.Children.Add(returnLive);
        actions.Children.Add(clear);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "LIVE CAMERA SWITCH", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Solid("#F4F7FB") });
        titles.Children.Add(new TextBlock
        {
            Text = "Camera-only switching · eight camera inputs · Fill A/B preview · TAKE to programme. Each camera can carry tags and route to any displays or venue channels. Click to load the target fill; double-click to load and take.",
            FontSize = 10, Foreground = Solid("#8C9AAC"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(titles);
        root.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(302) });
        body.Children.Add(BuildStage());
        var side = BuildSwitchPanel();
        Grid.SetColumn(side, 1);
        body.Children.Add(side);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var footer = new DockPanel { Margin = new Thickness(2, 8, 2, 0), LastChildFill = true };
        // _onAir belongs to the programme card in the switch panel. Adding it to the footer as
        // well gave it a second logical parent and stopped the window from opening.
        footer.Children.Add(_status);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        ThemeManager.SetSkipTheme(this, true);

        _onAir.Text = "PROGRAMME · layout";
        _session?.StateChanged += SessionChanged;
        Loaded += (_, _) =>
        {
            // Keep the neutral black window surface even if another window's theme pass has
            // already run in this process.
            Background = Solid("#050505");
            SelectTarget(0);
            RefreshCameras();
            RefreshFillChrome();
        };
        Closed += (_, _) => Shutdown();
        RefreshCameras();
        SelectTarget(0);
    }

    public int CameraTileCount => _cameraPanel.Children.Count;
    public int MultiviewTileCount => _multiview.Children.Count;
    public string? TargetChannel => _target == 0 ? "A" : "B";
    public string? FillALabel => _entryA?.Label;
    public string? FillBLabel => _entryB?.Label;

    UIElement BuildStage()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var title = new StackPanel();
        title.Children.Add(Section("CAMERA MULTIVIEW"));
        title.Children.Add(new TextBlock { Text = "All eight capture inputs · red = programme · green = target fill", FontSize = 10, Foreground = Solid("#7C8A9C") });
        head.Children.Add(title);
        DockPanel.SetDock(_multiviewPage, Dock.Right);
        _multiviewPage.Text = "8 INPUTS";
        head.Children.Add(_multiviewPage);
        stack.Children.Add(head);

        _multiview.Height = 286;
        _multiview.Margin = new Thickness(0, 0, 0, 8);
        var wall = new Border
        {
            Background = Solid("#050505"), BorderBrush = Solid("#2A2A2A"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Child = _multiview
        };
        stack.Children.Add(wall);

        var monitors = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        monitors.ColumnDefinitions.Add(new ColumnDefinition());
        monitors.ColumnDefinitions.Add(new ColumnDefinition());
        (_fillACard, _targetABorder, _takeA) = BuildFillCard("FILL A", 0, _fillAImage, _fillAState);
        (_fillBCard, _targetBBorder, _takeB) = BuildFillCard("FILL B", 1, _fillBImage, _fillBState);
        _fillACard.Margin = new Thickness(0, 0, 5, 0);
        _fillBCard.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(_fillACard, 0);
        Grid.SetColumn(_fillBCard, 1);
        monitors.Children.Add(_fillACard);
        monitors.Children.Add(_fillBCard);
        stack.Children.Add(monitors);

        var sourceHead = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        sourceHead.Children.Add(Section("CAMERA SOURCES"));
        var hint = new TextBlock { Text = "Click to load · double-click to take", FontSize = 9, Foreground = Solid("#7C8A9C"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(hint, Dock.Right);
        sourceHead.Children.Add(hint);
        stack.Children.Add(sourceHead);
        stack.Children.Add(new Border
        {
            Background = Solid("#050505"), BorderBrush = Solid("#2A2A2A"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(5), Height = 126,
            Child = new ScrollViewer
            {
                Content = _cameraPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        });
        return stack;
    }

    UIElement BuildSwitchPanel()
    {
        var card = new Border
        {
            Background = Solid("#101010"), BorderBrush = Solid("#2A2A2A"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12)
        };
        var stack = new StackPanel();
        stack.Children.Add(Section("SWITCH"));
        stack.Children.Add(new TextBlock { Text = "TARGET FILL", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Solid("#7C8A9C") });
        var targetButtons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 10) };
        var a = new Button { Content = "FILL A", MinHeight = 30, Margin = new Thickness(0, 0, 3, 0) };
        var b = new Button { Content = "FILL B", MinHeight = 30, Margin = new Thickness(3, 0, 0, 0) };
        a.Click += (_, _) => SelectTarget(0);
        b.Click += (_, _) => SelectTarget(1);
        targetButtons.Children.Add(a);
        targetButtons.Children.Add(b);
        stack.Children.Add(targetButtons);

        var take = new Button
        {
            Content = "TAKE TARGET → PROGRAMME", MinHeight = 42, FontWeight = FontWeights.Bold,
            Background = Solid("#12352C"), Foreground = Solid("#8DEBCB"), BorderBrush = Solid("#2E6F5D"),
            Margin = new Thickness(0, 0, 0, 6)
        };
        take.Click += (_, _) => Take(_target);
        stack.Children.Add(take);
        var cut = new Button { Content = "CUT TARGET → PROGRAMME", MinHeight = 32, Margin = new Thickness(0, 0, 0, 10) };
        cut.Click += (_, _) => Take(_target, cut: true);
        stack.Children.Add(cut);

        // The A/B take buttons live inside their fill cards. Re-adding them here gave them a
        // second logical parent and made the window fail to open.
        stack.Children.Add(Section("PROGRAMME"));
        stack.Children.Add(new Border
        {
            Background = Solid("#0A0A0A"), BorderBrush = Solid("#2A2A2A"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8),
            Child = _onAir
        });
        stack.Children.Add(new TextBlock
        {
            Text = "The programme monitor follows the fill on air. Returning to Live releases the camera and restores the published layout.",
            FontSize = 10, Foreground = Solid("#7C8A9C"), TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 10), Opacity = 0.3 });
        stack.Children.Add(Section("ROUTING · TARGET FILL"));
        stack.Children.Add(_routeLabel);
        stack.Children.Add(_routeSummary);
        var editRoute = new Button { Content = "EDIT ROUTING", MinHeight = 30, Margin = new Thickness(0, 7, 0, 0) };
        editRoute.ToolTip = "Choose any number of displays, venue channels and tags — or route to all";
        editRoute.Click += (_, _) => EditRouteForTarget();
        stack.Children.Add(editRoute);
        stack.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 10), Opacity = 0.3 });
        stack.Children.Add(Section("QUICK GUIDE"));
        stack.Children.Add(new TextBlock
        {
            Text = "1 · Pick Fill A or Fill B as the target.\n2 · Click a camera to load it into that fill.\n3 · TAKE puts that fill on programme.\n4 · The other fill stays ready as preview.",
            FontSize = 10, Foreground = Solid("#8C9AAC"), TextWrapping = TextWrapping.Wrap, LineHeight = 18
        });
        card.Child = stack;
        return card;
    }

    (Border Card, Border Target, Button Take) BuildFillCard(string title, int fill, Image image, TextBlock state)
    {
        var card = new Border
        {
            Background = Solid("#101010"), BorderBrush = Solid("#2A2A2A"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(7), Cursor = System.Windows.Input.Cursors.Hand
        };
        var stack = new StackPanel();
        var head = new DockPanel { LastChildFill = true };
        var name = new TextBlock { Text = title, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Solid("#F4F7FB") };
        var target = new Border
        {
            Background = Solid("#18211C"), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 1),
            Child = new TextBlock { Text = "TARGET", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = Solid("#8DEBCB") },
            Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(target, Dock.Right);
        head.Children.Add(target);
        head.Children.Add(name);
        stack.Children.Add(head);
        var picture = new Border
        {
            Height = 188, Background = Brushes.Black, CornerRadius = new CornerRadius(4), ClipToBounds = true,
            Margin = new Thickness(0, 5, 0, 5), Child = new Viewbox
            {
                Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = image
            }
        };
        stack.Children.Add(picture);
        state.Text = "No camera loaded";
        stack.Children.Add(state);
        var take = new Button { Content = $"TAKE {title}", MinHeight = 30, Margin = new Thickness(0, 6, 0, 0) };
        take.Click += (_, _) => Take(fill);
        stack.Children.Add(take);
        card.Child = stack;
        card.MouseLeftButtonDown += (_, _) => SelectTarget(fill);
        return (card, target, take);
    }

    void SelectTarget(int fill)
    {
        _target = fill == 0 ? 0 : 1;
        _targetABorder.Visibility = _target == 0 ? Visibility.Visible : Visibility.Collapsed;
        _targetBBorder.Visibility = _target == 1 ? Visibility.Visible : Visibility.Collapsed;
        _fillACard.BorderBrush = _onAirFill == 0 ? Solid("#FF9B9B") : _target == 0 ? Solid("#8DEBCB") : Solid("#2A2A2A");
        _fillBCard.BorderBrush = _onAirFill == 1 ? Solid("#FF9B9B") : _target == 1 ? Solid("#8DEBCB") : Solid("#2A2A2A");
        RefreshRouteSummary();
        _status.Text = $"Loading cameras into FILL {(_target == 0 ? "A" : "B")}. Click a camera, or double-click to take it straight to programme.";
    }

    void RefreshCameras()
    {
        var devices = _session?.CameraDevices() ?? CameraFeed.NamedDevices();
        var cameras = devices.Select(device =>
            _session?.Library.FirstOrDefault(entry => entry.IsCamera && entry.CameraIndex == device.Index)
            ?? new MediaEntry(MediaLibrary.CameraPath(device.Index), "Camera", Name: device.Label))
            .OrderBy(entry => entry.CameraIndex).ToList();
        if (cameras.Count == 0 && _session is not null)
            cameras.AddRange(_session.Library.Where(entry => entry.IsCamera)
                .GroupBy(entry => entry.CameraIndex).Select(group => group.First()).OrderBy(entry => entry.CameraIndex));
        var signature = string.Join("|", cameras.Select(entry => $"{entry.CameraIndex}:{entry.Label}"));
        if (signature == _cameraSignature) return;
        _cameraSignature = signature;
        foreach (var (feed, handler) in _wallHandlers) feed.FrameReady -= handler;
        _wallHandlers.Clear();
        _tileStates.Clear();
        _multiview.Children.Clear();
        _cameraPanel.Children.Clear();
        for (var slot = 0; slot < 8; slot++)
        {
            if (slot < cameras.Count) _multiview.Children.Add(CameraTile(cameras[slot], compact: false));
            else _multiview.Children.Add(EmptyMultiviewCell(slot));
        }
        _multiviewPage.Text = cameras.Count == 0 ? "NO INPUTS" : $"{cameras.Count} INPUTS";
        if (cameras.Count == 0)
        {
            _cameraPanel.Children.Add(new TextBlock
            {
                Text = "No capture devices found. Plug in a camera and it appears here immediately.",
                FontSize = 11, Foreground = Solid("#8A98AA"), Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        foreach (var entry in cameras) _cameraPanel.Children.Add(CameraTile(entry, compact: true));
        RefreshTileBadges();
    }

    UIElement EmptyMultiviewCell(int slot)
    {
        var picture = new Grid { Width = 320, Height = 180, Background = Solid("#0A0A0A") };
        picture.Children.Add(new TextBlock
        {
            Text = $"{slot + 1}  ·  NO CAMERA", FontSize = 9, Foreground = Solid("#3E4C5C"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        return new Border
        {
            Background = Brushes.Transparent, Margin = new Thickness(3),
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both,
                Child = new Border
                {
                    Width = 320, Height = 180, BorderBrush = Solid("#2A2A2A"),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Child = picture
                }
            }
        };
    }

    UIElement CameraTile(MediaEntry entry, bool compact)
    {
        var frameWidth = 320d;
        var frameHeight = 180d;
        var picture = new Grid { Width = frameWidth, Height = frameHeight, Background = Brushes.Black, ClipToBounds = true };
        var image = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        picture.Children.Add(image);
        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4)
        };
        var aBadge = Badge("A", "#8DEBCB");
        var bBadge = Badge("B", "#8DEBCB");
        badges.Children.Add(aBadge);
        badges.Children.Add(bBadge);
        picture.Children.Add(badges);
        var strip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x05, 0x08, 0x0C)),
            VerticalAlignment = VerticalAlignment.Bottom, Height = 23, Padding = new Thickness(6, 0, 6, 0),
            Child = new DockPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = $"CAM {entry.CameraIndex + 1}", FontSize = 9, FontWeight = FontWeights.Bold,
                        Foreground = Solid("#7C8A9C"), Margin = new Thickness(0, 0, 6, 0)
                    },
                    new TextBlock { Text = entry.Label, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            }
        };
        picture.Children.Add(strip);
        var framed = new Border
        {
            Width = frameWidth, Height = frameHeight, Background = Solid("#0A0A0A"),
            BorderBrush = Solid("#2A2A2A"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = picture
        };
        var button = new Button
        {
            Margin = new Thickness(3), Padding = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = framed },
            ToolTip = $"{entry.Label}\nRoute: {RouteSummaryText(entry)}\nClick: load into selected fill · double-click: load and take"
        };
        if (compact)
        {
            button.Width = 198;
            button.Height = 112;
            button.Margin = new Thickness(0, 0, 6, 0);
        }
        ThemeManager.SetSkipTheme(button, true);
        var feed = CameraFeed.Get(entry.CameraIndex);
        Action<BitmapSource> handler = frame =>
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => image.Source = frame); return; }
            image.Source = frame;
        };
        feed.FrameReady += handler;
        _wallHandlers.Add((feed, handler));
        try { feed.Start(); } catch { }
        if (feed.LastFrame is { } last) image.Source = last;
        var routeButton = new Button
        {
            Content = "⚙", FontSize = compact ? 8 : 9, Padding = new Thickness(0),
            Width = compact ? 18 : 20, Height = compact ? 16 : 18, MinHeight = 0, MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(compact ? 4 : 6), Background = Solid("#E6050505"),
            BorderBrush = Solid("#3E3E3E"), BorderThickness = new Thickness(1),
            Foreground = Solid("#B8C4D4"), ToolTip = $"Routing · {RouteSummaryText(entry)}"
        };
        ThemeManager.SetSkipTheme(routeButton, true);
        routeButton.Click += (_, e) => { e.Handled = true; EditRoute(entry); };
        _tileStates.Add(new TileState(entry, aBadge, bBadge, routeButton));
        button.Click += (_, _) => Load(entry, _target);
        button.MouseDoubleClick += (_, _) => { Load(entry, _target); Take(_target); };
        // The routing button is a sibling of the tile button, not its child: a nested button would
        // also fire the tile's click and load the camera every time the operator opened routing.
        var cell = new Grid();
        cell.Children.Add(button);
        cell.Children.Add(routeButton);
        return cell;
    }

    static Border Badge(string text, string colour) => new()
    {
        Background = Solid("#CC0A0A0A"), BorderBrush = Solid(colour), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(3, 0, 0, 0),
        Visibility = Visibility.Collapsed,
        Child = new TextBlock { Text = text, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Solid(colour) }
    };

    void Load(MediaEntry entry, int fill)
    {
        var feed = CameraFeed.Get(entry.CameraIndex);
        try { feed.Start(); } catch { }
        if (fill == 0)
        {
            DetachFill(0);
            _entryA = entry;
            _fillAState.Text = $"{entry.Label} · CAMERA · loaded";
            _fillAState.Foreground = Solid("#8DEBCB");
            _aFeed = AttachFill(_fillAImage, feed);
            _fillAImage.Source = feed.LastFrame;
            LiveClipsBus.Shared.SetChannel(0, $"{entry.Label} · CAMERA", feed.LastFrame);
        }
        else
        {
            DetachFill(1);
            _entryB = entry;
            _fillBState.Text = $"{entry.Label} · CAMERA · loaded";
            _fillBState.Foreground = Solid("#8DEBCB");
            _bFeed = AttachFill(_fillBImage, feed);
            _fillBImage.Source = feed.LastFrame;
            LiveClipsBus.Shared.SetChannel(1, $"{entry.Label} · CAMERA", feed.LastFrame);
        }
        RefreshTileBadges();
        RefreshRouteSummary();
        _status.Text = $"FILL {(fill == 0 ? "A" : "B")} · loaded camera {entry.Label} · press TAKE or double-click the camera.";
    }

    (CameraFeed Feed, Action<BitmapSource> Handler) AttachFill(Image image, CameraFeed feed)
    {
        Action<BitmapSource> handler = frame =>
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => image.Source = frame); return; }
            image.Source = frame;
        };
        feed.FrameReady += handler;
        return (feed, handler);
    }

    void DetachFill(int fill)
    {
        var current = fill == 0 ? _aFeed : _bFeed;
        if (current.Handler is { } handler) current.Feed?.FrameReady -= handler;
        if (fill == 0) _aFeed = default; else _bFeed = default;
    }

    void Take(int fill, bool cut = false)
    {
        var entry = fill == 0 ? _entryA : _entryB;
        if (entry is null)
        {
            _status.Text = $"FILL {(fill == 0 ? "A" : "B")} has no camera loaded.";
            return;
        }
        var route = RouteFor(entry);
        var routed = route?.TagOverride is { } tag ? entry with { Tag = tag } : entry;
        _session?.Play(routed);
        RouteTake(entry, route);
        _onAirFill = fill;
        _onAir.Text = $"ON AIR · FILL {(fill == 0 ? "A" : "B")} · {entry.Label}";
        var frame = (fill == 0 ? _aFeed.Feed : _bFeed.Feed)?.LastFrame;
        LiveClipsBus.Shared.SetLive($"{entry.Label} · LIVE CAMERA", frame);
                    _status.Text = $"{(cut ? "CUT" : "TAKEN")} · camera {entry.Label} is live in fill {(fill == 0 ? "A" : "B")} · {RouteSummaryText(entry)}.";
        RefreshFillChrome();
    }

    void Clear(int fill)
    {
        DetachFill(fill);
        if (fill == 0)
        {
            _entryA = null;
            _fillAState.Text = "No camera loaded";
            _fillAState.Foreground = Solid("#8DEBCB");
            _fillAImage.Source = null;
            LiveClipsBus.Shared.SetChannel(0, "", null);
        }
        else
        {
            _entryB = null;
            _fillBState.Text = "No camera loaded";
            _fillBState.Foreground = Solid("#8DEBCB");
            _fillBImage.Source = null;
            LiveClipsBus.Shared.SetChannel(1, "", null);
        }
        if (_onAirFill == fill)
        {
            _onAirFill = -1;
            CameraRouting.Clear();
        }
        RefreshTileBadges();
        RefreshFillChrome();
        RefreshRouteSummary();
        _status.Text = $"FILL {(fill == 0 ? "A" : "B")} cleared.";
    }

    void RefreshTileBadges()
    {
        foreach (var tile in _tileStates)
        {
            var onA = _entryA is not null && SameCamera(tile.Entry, _entryA);
            var onB = _entryB is not null && SameCamera(tile.Entry, _entryB);
            tile.ABadge.Visibility = onA ? Visibility.Visible : Visibility.Collapsed;
            tile.BBadge.Visibility = onB ? Visibility.Visible : Visibility.Collapsed;
            tile.ABadge.Background = onA && _onAirFill == 0 ? Solid("#E6FF9B9B") : Solid("#CC0A0A0A");
            tile.BBadge.Background = onB && _onAirFill == 1 ? Solid("#E6FF9B9B") : Solid("#CC0A0A0A");
        }
    }

    CameraRoute? RouteFor(MediaEntry entry) =>
        _preferences.CameraRoutes.FirstOrDefault(route => route.CameraIndex == entry.CameraIndex);

    string RouteSummaryText(MediaEntry entry)
    {
        var route = RouteFor(entry);
        return route is null
            ? "own tag · no display or channel routing"
            : route.Summary(_catalog?.Displays.Count ?? 0, _venue?.State.Channels.Count ?? 0);
    }

    void RefreshRouteSummary()
    {
        var entry = _target == 0 ? _entryA : _entryB;
        _routeLabel.Text = entry is null ? "No camera in this fill" : $"{entry.Label} · CAM {entry.CameraIndex + 1}";
        _routeSummary.Text = entry is null
            ? "Load a camera, then route it to displays, channels and tags."
            : RouteSummaryText(entry);
    }

    void EditRouteForTarget()
    {
        var entry = _target == 0 ? _entryA : _entryB;
        if (entry is null)
        {
            _status.Text = "Load a camera into the target fill before routing it.";
            return;
        }
        EditRoute(entry);
    }

    void EditRoute(MediaEntry entry)
    {
        var draft = RouteFor(entry)?.Copy() ?? new CameraRoute { CameraIndex = entry.CameraIndex };
        var dialog = new CameraRouteWindow(entry, draft, _catalog?.Displays.ToList() ?? [], _venue?.State.Channels.ToList() ?? [])
            { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } saved) return;
        _preferences.CameraRoutes.RemoveAll(route => route.CameraIndex == entry.CameraIndex);
        if (saved.IsConfigured) _preferences.CameraRoutes.Add(saved);
        try { PreferencesStore.Save(_preferences); } catch { }
        RefreshRouteSummary();
        foreach (var tile in _tileStates)
            tile.RouteButton.ToolTip = $"Routing · {RouteSummaryText(tile.Entry)}";
        _status.Text = saved.IsConfigured
            ? $"ROUTING · {entry.Label} · {RouteSummaryText(entry)}"
            : $"ROUTING CLEARED · {entry.Label} keeps its own tag";
    }

    /// <summary>Applies a camera's route when it is taken: display overlays and channel cameras.</summary>
    void RouteTake(MediaEntry entry, CameraRoute? route)
    {
        var displays = _catalog is null || route is null
            ? Array.Empty<string>()
            : _catalog.Displays.Where(display => route.MatchesDisplay(display.Id)).Select(display => display.Id).ToArray();
        CameraRouting.Set(displays, entry.CameraIndex);
        if (_venue is null || route is null || !route.RoutesToChannels) return;
        foreach (var channel in _venue.State.Channels.Where(channel => route.MatchesChannel(channel.Id)))
            channel.CameraIndex = entry.CameraIndex;
        try { _venue.Save(); } catch { }
    }

    static bool SameCamera(MediaEntry left, MediaEntry right) =>
        left.CameraIndex == right.CameraIndex
        || string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);

    void RefreshFillChrome()
    {
        _fillACard.BorderBrush = _onAirFill == 0 ? Solid("#FF9B9B") : _target == 0 ? Solid("#8DEBCB") : Solid("#2A2A2A");
        _fillBCard.BorderBrush = _onAirFill == 1 ? Solid("#FF9B9B") : _target == 1 ? Solid("#8DEBCB") : Solid("#2A2A2A");
        _fillAState.Text = _entryA is null ? "No camera loaded"
            : _onAirFill == 0 ? $"{_entryA.Label} · ON AIR" : $"{_entryA.Label} · CAMERA · preview";
        _fillBState.Text = _entryB is null ? "No camera loaded"
            : _onAirFill == 1 ? $"{_entryB.Label} · ON AIR" : $"{_entryB.Label} · CAMERA · preview";
        _fillAState.Foreground = _onAirFill == 0 ? Solid("#FF9B9B") : Solid("#8DEBCB");
        _fillBState.Foreground = _onAirFill == 1 ? Solid("#FF9B9B") : Solid("#8DEBCB");
        _takeA.Content = _onAirFill == 0 ? "ON AIR · A" : "TAKE A";
        _takeB.Content = _onAirFill == 1 ? "ON AIR · B" : "TAKE B";
    }

    void SessionChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(SessionChanged); return; }
        if (_playback?.IsCamera != true && _onAirFill >= 0)
        {
            _onAirFill = -1;
            CameraRouting.Clear();
            _onAir.Text = "PROGRAMME · no camera on air";
            RefreshFillChrome();
        }
    }

    void Shutdown()
    {
        CameraRouting.Clear();
        _session?.StateChanged -= SessionChanged;
        foreach (var (feed, handler) in _wallHandlers) feed.FrameReady -= handler;
        _wallHandlers.Clear();
        DetachFill(0);
        DetachFill(1);
    }

    static TextBlock Section(string text) => new()
    {
        Text = text, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Solid("#7C8A9C"),
        Margin = new Thickness(0, 0, 0, 4)
    };
}
