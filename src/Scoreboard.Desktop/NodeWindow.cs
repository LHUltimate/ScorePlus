using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The visible end of a node. It uses the same ScorebugCanvasView as the console and output
/// windows, so a node cannot drift into a second renderer. The last received state stays on screen
/// when the controller is gone; the console's health poll marks the node stale instead.
/// </summary>
public sealed class NodeWindow : Window
{
    readonly NodeAgent _agent;
    readonly Grid _root = new() { Background = Brushes.Black };
    readonly Grid _stage = new();
    readonly ContentControl _host = new();
    readonly NodeProjection _projection;
    readonly Border _testPattern;
    readonly Border _monitorBadge;
    readonly TextBlock _monitorText = new()
    {
        FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xBF, 0xE7, 0xFA)),
        FontFamily = new FontFamily("Consolas")
    };
    readonly Stopwatch _frameClock = Stopwatch.StartNew();
    readonly Stopwatch _projectionClock = Stopwatch.StartNew();
    ScorebugCanvasView? _view;
    ScorebugCatalog? _viewCatalog;
    long _frameCount;

    public NodeWindow(NodeAgent agent)
    {
        _agent = agent;
        Title = $"ScorePlus — Node · {agent.Options.Name}";
        Width = Math.Max(640, agent.Output.Width / 2.0);
        Height = Math.Max(360, agent.Output.Height / 2.0);
        MinWidth = 480;
        MinHeight = 270;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Black;
        Content = _root;

        _host.HorizontalAlignment = HorizontalAlignment.Stretch;
        _host.VerticalAlignment = VerticalAlignment.Stretch;
        _stage.IsHitTestVisible = false;
        _stage.Children.Add(_host);
        _testPattern = BuildTestPattern();
        _stage.Children.Add(_testPattern);
        _root.Children.Add(_stage);
        // The stage stays in the visual tree so the render target can capture its live animations;
        // the opaque projection sits over it and is the only thing the audience sees.
        _projection = new NodeProjection(_stage);
        _root.Children.Add(_projection);
        _monitorBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x08, 0x0A, 0x0D)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x33)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Visibility = agent.Options.Role == NodeRole.Monitor ? Visibility.Visible : Visibility.Collapsed,
            Child = _monitorText
        };
        _root.Children.Add(_monitorBadge);

        _agent.OutputChanged += AgentOutputChanged;
        // Node mode does not run the console's global shortcut handler, so the output window owns
        // its own fullscreen escape. Without this an output PC can be trapped fullscreen.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && WindowFullscreen.IsFullscreen(this))
            {
                WindowFullscreen.Exit(this);
                e.Handled = true;
            }
            else if (e.Key == Key.F11)
            {
                WindowFullscreen.Toggle(this);
                e.Handled = true;
            }
        };
        Loaded += (_, _) =>
        {
            BuildView();
            CompositionTarget.Rendering += Rendering;
            _frameClock.Restart();
        };
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= Rendering;
            _agent.OutputChanged -= AgentOutputChanged;
            _view?.Dispose();
            _view = null;
        };
    }

    void AgentOutputChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(AgentOutputChanged); return; }
        if (_view is null || !ReferenceEquals(_viewCatalog, _agent.Catalog)) BuildView();
        else _view.Refresh();
        ApplyOutput();
    }

    void BuildView()
    {
        _view?.Dispose();
        _viewCatalog = _agent.Catalog;
        _view = new ScorebugCanvasView(_agent.Catalog, _agent.Engine,
            stateProvider: () => _agent.Engine.State,
            playback: null,
            layoutProvider: () => _agent.Catalog.PublishedCompositeOf(_agent.NodeDisplayId),
            displayId: _agent.NodeDisplayId)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _host.Content = _view;
        ApplyOutput();
    }

    void ApplyOutput()
    {
        _testPattern.Visibility = _agent.TestPattern ? Visibility.Visible : Visibility.Collapsed;
        var monitor = _agent.Options.Role == NodeRole.Monitor;
        _monitorBadge.Visibility = monitor ? Visibility.Visible : Visibility.Collapsed;
        if (monitor)
        {
            var health = _agent.HealthReport();
            _monitorText.Text = $"{health.Name.ToUpperInvariant()} · {health.Role} · seq {health.Sequence} · "
                + $"{health.LastFrameMs:0.0} ms · drift {health.DriftMs:0.0} ms · pkg {health.PackageVersion}";
        }
        if (_view is null) return;
        var output = _agent.Output;
        // The projector maps the entire stage, so the programme and the test pattern share exactly
        // the same curved/keystoned geometry. Scale, rotation and offset live in the same mesh.
        _projection.Configure(_agent.Calibration, output.Width, output.Height);
        _projection.Refresh();
        if (output.Hdr) _root.Background = new SolidColorBrush(Color.FromRgb(0x05, 0x07, 0x0A));
        if (!output.LowLatency) _view.Opacity = 0.999;
        if (_agent.Options.Role == NodeRole.Monitor)
            _monitorBadge.Opacity = 1;
    }

    void Rendering(object? sender, EventArgs e)
    {
        _frameCount++;
        // 30 fps is enough for the processor texture while keeping the render-target copy off the
        // UI thread's critical path. The test pattern is captured by the same refresh.
        if (_projectionClock.Elapsed.TotalMilliseconds >= 33)
        {
            _projection.Refresh();
            _projectionClock.Restart();
        }
        if (_frameClock.Elapsed.TotalMilliseconds < 120) return;
        _agent.NoteFrame(_frameClock.Elapsed.TotalMilliseconds);
        _frameClock.Restart();
        if (_agent.Options.Role == NodeRole.Monitor && _frameCount % 20 == 0) ApplyOutput();
    }

    Border BuildTestPattern()
    {
        var grid = new Grid { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var background = new Border { Background = new SolidColorBrush(Color.FromRgb(12, 15, 20)) };
        Grid.SetColumnSpan(background, 3);
        Grid.SetRowSpan(background, 2);
        grid.Children.Add(background);
        var colours = new[] { "#E63B3B", "#2ECC71", "#3D8FB8" };
        for (var index = 0; index < colours.Length; index++)
        {
            var bar = new Border
            {
                Background = (Brush)new BrushConverter().ConvertFromString(colours[index])!,
                Margin = new Thickness(index == 0 ? 0 : 2, 0, index == colours.Length - 1 ? 0 : 2, 0)
            };
            Grid.SetColumn(bar, index);
            grid.Children.Add(bar);
        }
        var label = new TextBlock
        {
            Text = $"SCOREPLUS NODE TEST PATTERN\n{_agent.Options.Name} · {_agent.Options.Role}\n"
                + $"{_agent.Output.ResolutionLabel} · {_agent.Catalog.Displays.Count} displays\n"
                + "Use calibration to fit the processor to the physical display.",
            Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumnSpan(label, 3);
        Grid.SetRow(label, 1);
        grid.Children.Add(label);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x04, 0x06, 0x09)),
            Child = grid
        };
    }
}
