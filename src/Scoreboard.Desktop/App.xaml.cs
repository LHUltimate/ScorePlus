using System.Windows;
using System.Windows.Input;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

public partial class App : Application
{
    // Held for the life of the process; the OS releases it when ScorePlus exits.
    static Mutex? _singleInstance;
    static NodeAgent? _nodeAgent;

    /// <summary>
    /// The smoke harness sets this environment variable before creating the App. It is read in
    /// every build, because the harness is normally run with -c Release; a Debug-only hook let
    /// the real launcher open over the operator's full-screen app during a release smoke run.
    /// </summary>
    public static bool SuppressStartupForSmoke =>
        Environment.GetEnvironmentVariable("SCOREPLUS_SMOKE") == "1";

    protected override void OnStartup(StartupEventArgs e)
    {
        if (SuppressStartupForSmoke) return;
        // Node mode is the same executable with no console and no single-instance mutex, so an
        // output PC can run beside the control room. It is opt-in; a normal launch never sees it.
        if (NodeAgent.TryParseLaunch(e.Args, out var nodeOptions))
        {
            StartNode(nodeOptions);
            return;
        }
        // Media decoders and other native components can fail on threads the dispatcher never
        // sees. Log those too, so a window that closes on its own leaves a trace to diagnose.
        // F11 belongs to the application, not to one window: the studio, output, media panel and
        // dialogs all get the same true fullscreen behavior.
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(HandleWindowKey));
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            NoticeWindow.Tell(MainWindow, "ScorePlus hit an unexpected error",
                "Your game autosave was kept.\n\n" + args.Exception.Message, warning: true);
            args.Handled = true;
        };
        base.OnStartup(e);

        // One copy per desktop. Two copies share %LOCALAPPDATA%\ScorePlus, and each keeps its own
        // layout and preferences in memory — so the older one silently overwrites the newer one's
        // edits, which reads as accents and backgrounds snapping back on their own.
        _singleInstance = new Mutex(true, "ScorePlus.SingleInstance", out var isFirstCopy);
        if (!isFirstCopy)
        {
            NoticeWindow.Tell(null, "ScorePlus is already running",
                "Close the other copy first.\n\nTwo copies share the same layouts and preferences, so the older one undoes the newer one's edits.",
                warning: true);
            Shutdown();
            return;
        }

        // The main window opens first and stays locked behind the launch box, so the operator can
        // see the workspace they are about to load into before choosing a game.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow window;
        try { window = new MainWindow(waitForLaunch: true); }
        catch (Exception ex)
        {
            LogCrash(ex);
            NoticeWindow.Tell(null, "ScorePlus could not start", ex.Message, warning: true);
            Shutdown();
            return;
        }
        window.Closed += (_, _) => Shutdown();
        // The console opens filling the screen — full windowed, so the title bar and taskbar stay —
        // and ScreenFit keeps that true on a portrait monitor as well as a wide one.
        window.WindowState = WindowState.Maximized;
        window.Show();

        var launcher = new LauncherWindow { Owner = window };
        if (launcher.ShowDialog() != true) { window.Close(); return; }
        window.StartFromLauncher(launcher.Mode, launcher.GamePath, launcher.SportId, launcher.GameName, launcher.TemplateId);
    }

    void StartNode(NodeAgentOptions options)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _nodeAgent = new NodeAgent(options, Dispatcher);
        if (!_nodeAgent.Start())
        {
            NoticeWindow.Tell(null, "ScorePlus node could not start",
                _nodeAgent.Error ?? "The node port is not available.", warning: true);
            Shutdown();
            return;
        }
        if (!options.Headless) _nodeAgent.ShowOutput(options.Fullscreen);
    }

    static void HandleWindowKey(object sender, KeyEventArgs e)
    {
        if (sender is not Window window || e.Handled) return;
        if (e.Key == Key.Escape && WindowFullscreen.IsFullscreen(window))
        {
            WindowFullscreen.Exit(window);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.F11) return;
        // F11 on the console, a detached panel or the audience output all mean real fullscreen:
        // no title bar, no frame and no taskbar. The old maximize toggle made a maximized console
        // look like it had been returned to a window at the side of the screen.
        WindowFullscreen.Toggle(window);
        e.Handled = true;
    }

    /// <summary>
    /// A wheel over a closed picker should scroll the surrounding panel, not change its value. The
    /// panel is scrolled directly: forwarding a synthetic event was occasionally dropped when a
    /// property row sat at the edge of a ScrollViewer, which made the property list stop until the
    /// pointer was moved off the picker.
    /// </summary>
    void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox { IsDropDownOpen: false } combo || e.Handled) return;
        e.Handled = true;
        for (var parent = VisualTreeHelper.GetParent(combo); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer scroller) continue;
            // Skip a horizontal-only strip and keep walking to the vertical property panel. This is
            // what lets a wheel over a picker in a wide row still move the editor.
            if (scroller.ScrollableHeight > 0)
            {
                scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta / 3.0);
                return;
            }
            if (scroller.ScrollableWidth > 0)
            {
                scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta / 3.0);
                return;
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _nodeAgent?.Dispose(); } catch { }
        base.OnExit(e);
        // The smoke harness shares this App but creates and closes windows all run; don't terminate
        // it before its own cleanup and result reporting.
        if (SuppressStartupForSmoke) return;
        // A native codec, capture driver or audio endpoint can keep a thread alive after WPF has
        // closed every window. The operator closed ScorePlus, so the process must leave Task Manager
        // too, rather than sitting there with no window.
        Environment.Exit(0);
    }

    /// <summary>Appends a timestamped error to <c>%LOCALAPPDATA%\ScorePlus\logs\errors.log</c>.</summary>
    static void LogCrash(Exception? error)
    {
        try
        {
            var folder = AppPaths.Combine("logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "errors.log"), $"{DateTimeOffset.Now:u} {error}\n\n");
        }
        catch
        {
            // Logging must never be the reason the app fails to report a problem.
        }
    }
}
