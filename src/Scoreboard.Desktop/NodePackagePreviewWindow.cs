using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>Shows exactly what a Send package will carry, including the media files embedded for
/// the layouts and venue rotation.</summary>
public sealed class NodePackagePreviewWindow : Window
{
    public NodePackagePreviewWindow(DisplayNode node, NodePackagePreview preview, string theme)
    {
        Title = $"ScorePlus — Package preview · {node.Name}";
        Width = 720;
        Height = 620;
        MinWidth = 540;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x12, 0x18));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB));
        FontFamily = new FontFamily("Segoe UI");
        ThemeManager.Apply(this, theme);

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = $"PACKAGE {preview.Version} · {node.Name}",
            FontSize = 16,
            FontWeight = FontWeights.Bold
        });
        heading.Children.Add(new TextBlock
        {
            Text = $"{preview.Summary} · {preview.AssetCount} media asset{(preview.AssetCount == 1 ? "" : "s")} · {preview.AssetBytes / 1024.0 / 1024.0:0.0} MB",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)),
            Margin = new Thickness(0, 4, 0, 10)
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var facts = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        facts.Children.Add(Fact("Displays", preview.DisplayCount.ToString()));
        facts.Children.Add(Fact("Layouts", preview.LayoutCount.ToString()));
        facts.Children.Add(Fact("Assets", preview.AssetCount.ToString()));
        facts.Children.Add(Fact("Size", $"{preview.AssetBytes / 1024.0 / 1024.0:0.0} MB"));
        Grid.SetRow(facts, 1);
        root.Children.Add(facts);

        var list = new ListBox
        {
            Margin = new Thickness(0, 0, 0, 10),
            FontSize = 11,
            ItemsSource = preview.Assets
                .OrderBy(asset => asset.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(asset => $"{asset.FileName}   ·   {asset.Size / 1024.0:0.0} KB   ·   {asset.SourcePath}")
                .ToList()
        };
        if (preview.Assets.Count == 0)
            list.ItemsSource = new[] { "No separate media files are referenced by this package." };
        Grid.SetRow(list, 2);
        root.Children.Add(list);

        var footer = new StackPanel();
        if (preview.MissingAssets.Count > 0)
        {
            footer.Children.Add(new TextBlock
            {
                Text = $"MISSING ON THIS PC ({preview.MissingAssets.Count})",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A)),
                Margin = new Thickness(0, 0, 0, 3)
            });
            footer.Children.Add(new TextBlock
            {
                Text = string.Join("\n", preview.MissingAssets.Take(8)),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xB4)),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 90
            });
        }
        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(18, 7, 18, 7),
            Margin = new Thickness(0, 10, 0, 0),
            IsCancel = true
        };
        close.Click += (_, _) => Close();
        footer.Children.Add(close);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Content = root;
    }

    static Border Fact(string label, string value)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2))
        });
        stack.Children.Add(new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold });
        return new Border { Child = stack };
    }
}
