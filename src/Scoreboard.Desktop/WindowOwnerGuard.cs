using System.Windows;

namespace Scoreboard.Desktop;

/// <summary>
/// Windows opens a modeless child owned by a maximised window in the restored state. Closing that
/// child then leaves the owner minimised, which read as "closing Live Channel minimised the whole
/// program". This guard remembers how the owner was shown and puts it back if Windows minimised it
/// during the close.
/// </summary>
public static class WindowOwnerGuard
{
    public static void Attach(Window window)
    {
        Window? owner = null;
        WindowState? state = null;
        window.Loaded += (_, _) =>
        {
            owner = window.Owner;
            state = owner?.WindowState;
        };
        window.Closed += (_, _) =>
        {
            if (owner is not { WindowState: WindowState.Minimized }) return;
            if (state is null or WindowState.Minimized) return;
            owner.WindowState = state.Value;
            try { owner.Activate(); } catch { }
        };
    }
}
