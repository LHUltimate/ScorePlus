using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>The node's own log endpoint rendered in a read-only window, so an operator can see
/// package installs, failures and sequence applications without leaving the console.</summary>
public sealed class NodeLogWindow : Window
{
    public NodeLogWindow(DisplayNode node, string log, string theme)
    {
        Title = $"ScorePlus — Node log · {node.Name}";
        Width = 760;
        Height = 560;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x12, 0x18));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB));
        FontFamily = new FontFamily("Segoe UI");
        ThemeManager.Apply(this, theme);

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        heading.Children.Add(new TextBlock
        {
            Text = $"NODE LOG · {node.Name}",
            FontSize = 16,
            FontWeight = FontWeights.Bold
        });
        heading.Children.Add(new TextBlock
        {
            Text = $"{node.Address}:{node.Port} · package {node.Health.PackageVersion} · {node.Output.ResolutionLabel}",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)),
            Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var box = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(log) ? "No log lines yet." : log,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Padding = new Thickness(8)
        };
        Grid.SetRow(box, 1);
        root.Children.Add(box);

        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(18, 7, 18, 7),
            Margin = new Thickness(0, 10, 0, 0),
            IsCancel = true
        };
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 2);
        root.Children.Add(close);

        Content = root;
    }
}
