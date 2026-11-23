using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The one-press team identity switch. It shows exactly what will move between the two sides —
/// names, abbreviations, colours, logos and records — before anything is applied, so a switch can
/// be checked rather than discovered on air.
/// </summary>
public sealed class TeamSwitchWindow : Window
{
    public TeamSwitchWindow(GameEngine engine, string theme)
    {
        var light = ThemeManager.Resolve(theme) == "Light";
        var text = Brush(light ? "#18202B" : "#F4F7FB");
        var muted = Brush(light ? "#586576" : "#93A0B2");
        var surface = Brush(light ? "#F3F5F8" : "#141A23");
        var border = Brush(light ? "#C9D2DC" : "#28323F");

        Title = "Swap team identity";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush(light ? "#FFFFFF" : "#0E1218");
        Foreground = text;
        FontFamily = new FontFamily("Segoe UI");

        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var apply = new Button { Content = "Swap identities", IsDefault = true, Padding = new Thickness(18, 8, 18, 8), MinWidth = 140 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(8, 0, 0, 0) };
        apply.Click += (_, _) =>
        {
            engine.SwapTeamIdentities();
            DialogResult = true;
        };
        cancel.Click += (_, _) => { DialogResult = false; };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock { Text = "TEAM IDENTITY", FontSize = 16, FontWeight = FontWeights.Bold });
        heading.Children.Add(new TextBlock
        {
            Text = "Everything below moves to the other side of the graphic. Palette-bound elements and team logos follow the slots, so every layout updates at once. This is one undo step.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = muted,
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0)
        });
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var rows = new StackPanel();
        var changes = TeamIdentities.Diff(engine.State);
        if (changes.Count == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Text = "Both sides are already identical, so a swap would change nothing.",
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 8, 2, 8)
            });
        }
        foreach (var change in changes)
        {
            var row = new Border
            {
                Background = surface,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 4)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = $"{change.Group.ToUpperInvariant()} · {change.Field}",
                Foreground = muted,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var value = new TextBlock
            {
                Text = $"{Shorten(change.Before)}  →  {Shorten(change.After)}",
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
            row.Child = grid;
            rows.Children.Add(row);
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = rows
        };
        root.Children.Add(scroll);
        Content = root;
    }

    static string Shorten(string value) => value.Length <= 46 ? value : value[..17] + "…" + value[^26..];

    static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
}
