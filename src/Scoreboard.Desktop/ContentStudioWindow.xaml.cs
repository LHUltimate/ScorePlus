using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

public partial class ContentStudioWindow : Window
{
    enum ContentSelection { Template, Brand, Storyboard, Sponsors, Packs }

    readonly ScorebugCatalog _catalog;
    readonly GameEngine _engine;
    readonly string _theme;
    readonly NodeService? _nodes;
    readonly DispatcherTimer _playback = new() { Interval = TimeSpan.FromMilliseconds(100) };
    ContentSelection _selection = ContentSelection.Template;
    ContentTemplate? _template;
    ScorebugLayout? _previewLayout;
    ScorebugCanvasView? _previewView;
    bool _refreshing;
    bool _playing;
    int _sceneIndex;
    double _sceneElapsed;
    readonly Dictionary<string, TextBox> _brandFields = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ComboBox> _brandFonts = new(StringComparer.OrdinalIgnoreCase);

    public ContentStudioWindow(ScorebugCatalog catalog, GameEngine engine, string theme = "Dark", NodeService? nodes = null)
    {
        InitializeComponent();
        _catalog = catalog;
        _engine = engine;
        _theme = theme;
        _nodes = nodes;
        ThemeManager.Apply(this, theme);
        _catalog.Changed += CatalogChanged;
        _engine.Changed += EngineChanged;
        _playback.Tick += Playback_Tick;
        BuildTree();
        SelectTemplate(ContentTemplateCatalog.All.FirstOrDefault());
        RefreshStoryboard();
        BrandStatus.Text = $"BRAND · {_catalog.Brand.Name}";
        Closed += (_, _) =>
        {
            _playback.Stop();
            _catalog.Changed -= CatalogChanged;
            _engine.Changed -= EngineChanged;
            DisposePreview();
        };
    }

    void CatalogChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(CatalogChanged); return; }
        if (_refreshing) return;
        RefreshStoryboard();
        BrandStatus.Text = $"BRAND · {_catalog.Brand.Name}";
    }

    void EngineChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(EngineChanged); return; }
        // The preview follows live game data through the view itself; a scene's label only needs the
        // score when the window is rebuilt.
    }

    // ---------- gallery tree ----------

    void BuildTree()
    {
        _refreshing = true;
        LibraryTree.Items.Clear();
        var search = (LibrarySearch.Text ?? "").Trim();
        bool Matches(ContentTemplate template) =>
            search.Length == 0
            || template.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || template.Sports.Any(sport => sport.Contains(search, StringComparison.OrdinalIgnoreCase))
            || template.Slot.Contains(search, StringComparison.OrdinalIgnoreCase);

        var templates = new TreeViewItem { Header = Header("TEMPLATES", "#38D7A3"), IsExpanded = true };
        foreach (var slot in ContentTemplateCatalog.Slots)
        {
            var matches = ContentTemplateCatalog.All.Where(template =>
                string.Equals(template.Slot, slot, StringComparison.OrdinalIgnoreCase) && Matches(template)).ToArray();
            if (matches.Length == 0) continue;
            var group = new TreeViewItem { Header = Header(slot.ToUpperInvariant(), "#93A0B2"), IsExpanded = true };
            foreach (var template in matches)
            {
                var item = new TreeViewItem
                {
                    Header = Header(template.Name, template.BroadcastStandard ? "#F4F7FB" : "#B7C3D2"),
                    Tag = template,
                    ToolTip = $"{template.Slot} · {template.SportLabel}\n{template.Description}"
                };
                group.Items.Add(item);
            }
            templates.Items.Add(group);
        }
        LibraryTree.Items.Add(templates);
        // A second root groups the same one catalogue by sport, so an operator can browse
        // "Basketball" instead of remembering which slot a board lives under.
        var bySport = new TreeViewItem { Header = Header("TEMPLATES BY SPORT", "#7FD1FF"), IsExpanded = true };
        foreach (var sport in ContentTemplateCatalog.Sports)
        {
            var matches = ContentTemplateCatalog.All
                .Where(template => template.Sports.Contains(sport, StringComparer.OrdinalIgnoreCase) && Matches(template))
                .OrderBy(template => template.Slot).ThenBy(template => template.Name).ToArray();
            if (matches.Length == 0) continue;
            var group = new TreeViewItem { Header = Header(sport.ToUpperInvariant(), "#93A0B2"), IsExpanded = true };
            foreach (var template in matches)
                group.Items.Add(new TreeViewItem
                {
                    Header = Header(template.Name, template.BroadcastStandard ? "#F4F7FB" : "#B7C3D2"),
                    Tag = template, ToolTip = $"{template.Slot} · {template.SportLabel}\n{template.Description}"
                });
            bySport.Items.Add(group);
        }
        LibraryTree.Items.Add(bySport);
        LibraryTree.Items.Add(new TreeViewItem { Header = Header("BRAND KIT", "#C8B6FF"), Tag = "brand" });
        LibraryTree.Items.Add(new TreeViewItem { Header = Header("STORYBOARD", "#F4C15D"), Tag = "storyboard" });
        LibraryTree.Items.Add(new TreeViewItem { Header = Header("SPONSOR INVENTORY", "#F59AB2"), Tag = "sponsors" });
        LibraryTree.Items.Add(new TreeViewItem { Header = Header("CONTENT PACKS", "#7FD1FF"), Tag = "packs" });
        _refreshing = false;
    }

    static StackPanel Header(string text, string colour)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Border
        {
            Width = 3, Height = 12, CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour)), Margin = new Thickness(0, 0, 7, 0)
        });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold });
        return panel;
    }

    void LibrarySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_refreshing) BuildTree();
    }

    void LibraryTree_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_refreshing || e.NewValue is not TreeViewItem item) return;
        if (item.Tag is ContentTemplate template) { SelectTemplate(template); return; }
        switch (item.Tag as string)
        {
            case "brand": ShowBrand(); break;
            case "storyboard": ShowStoryboardInspector(); break;
            case "sponsors": ShowSponsors(); break;
            case "packs": ShowPacks(); break;
        }
    }

    void LibraryTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_selection == ContentSelection.Template) AddToDisplay_Click(sender, e);
    }

    // ---------- template preview ----------

    void SelectTemplate(ContentTemplate? template)
    {
        if (template is null) return;
        if (_playing) Stop();
        _selection = ContentSelection.Template;
        _template = template;
        _previewLayout = ContentTemplateCatalog.Build(template.Id, _catalog.Brand);
        PreviewTitle.Text = template.Name;
        PreviewMeta.Text = $"   {template.SportLabel}";
        PreviewDescription.Text = template.Description;
        SlotBadgeText.Text = template.Slot.ToUpperInvariant();
        SlotBadge.Background = new SolidColorBrush(template.BroadcastStandard
            ? Color.FromRgb(0x16, 0x3C, 0x32) : Color.FromRgb(0x2B, 0x1F, 0x4A));
        SlotBadgeText.Foreground = new SolidColorBrush(template.BroadcastStandard
            ? Color.FromRgb(0x8D, 0xEB, 0xCB) : Color.FromRgb(0xC8, 0xB6, 0xFF));
        InspectorTitle.Text = "TEMPLATE";
        BuildTemplateInspector(template);
        RefreshPreview();
        SetStatus($"{template.Name} · {template.Slot} · {template.SportLabel}");
    }

    void BuildTemplateInspector(ContentTemplate template)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text(template.Name, 17, FontWeights.Bold));
        panel.Children.Add(Text($"{template.Slot}  ·  {template.SportLabel}", 11, foreground: "#93A0B2", margin: new Thickness(0, 2, 0, 8)));
        panel.Children.Add(Text(template.Description, 11, foreground: "#B7C3D2", wrap: true));
        panel.Children.Add(Title("BRAND ROLES"));
        panel.Children.Add(Text("Marks on this template follow the show brand kit. Change the kit once and every marked part follows.",
            10, foreground: "#7C8A9C", wrap: true));
        var roles = _previewLayout?.Elements.Count(element => element.BrandRole != BrandRole.None) ?? 0;
        panel.Children.Add(Row("Marked elements", roles.ToString()));
        panel.Children.Add(Row("Canvas size", $"{_previewLayout?.Width ?? 0} × {_previewLayout?.Height ?? 0}"));
        panel.Children.Add(Row("Broadcast standard", template.BroadcastStandard ? "Yes" : "House template"));
        panel.Children.Add(Title("EFFECTS BANK"));
        panel.Children.Add(Text("Layer effects available on every output: blur, glow and the scene transitions below.",
            10, foreground: "#7C8A9C", wrap: true));
        var effects = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var effect in new[] { "Blur", "Glow" })
            effects.Children.Add(SmallButton(effect, () => SetTemplateEffect(effect)));
        panel.Children.Add(effects);
        panel.Children.Add(Title("ACTIONS"));
        panel.Children.Add(FullButton("ADD TO DISPLAY", AddToDisplay_Click));
        panel.Children.Add(FullButton("USE IN STORYBOARD", UseInStoryboard_Click));
        panel.Children.Add(FullButton("PUBLISH NOW", Publish_Click));
        InspectorHost.Children.Clear();
        InspectorHost.Children.Add(panel);
    }

    /// <summary>Applies a preview-only effect to every layer, so the operator can see the bank.</summary>
    void SetTemplateEffect(string effect)
    {
        if (_previewLayout is null) return;
        foreach (var element in _previewLayout.Elements)
        {
            element.Effect = effect == "Blur" ? LayerEffectKind.Blur : LayerEffectKind.Glow;
            element.EffectAmount = 0.35;
        }
        RefreshPreview();
        SetStatus($"{effect} effect previewed on {_template?.Name ?? "the template"}.");
    }

    void RefreshPreview()
    {
        DisposePreview();
        if (_previewLayout is null) return;
        _previewView = new ScorebugCanvasView(_catalog, _engine, stateProvider: () => _engine.State,
            playback: null, layoutProvider: () => _previewLayout, liveValues: true)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        PreviewHost.Content = _previewView;
        if (_playing && _selection == ContentSelection.Storyboard) _previewView.SetTimeline(_sceneElapsed);
        else _previewView.ShowAllLayers();
    }

    void DisposePreview()
    {
        if (_previewView is null) return;
        PreviewHost.Content = null;
        _previewView.Dispose();
        _previewView = null;
    }

    // ---------- actions ----------

    void AddToDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (_template is null) { SetStatus("Choose a template first."); return; }
        var index = _catalog.AddTemplate(_template.Id);
        _catalog.SelectLayout(index);
        var layout = _catalog.Layout;
        SetStatus($"Added {layout.Name} to {_catalog.ActiveDisplay.Name}.");
        RefreshStoryboard();
        ShowStoryboardInspector();
    }

    void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (_selection == ContentSelection.Storyboard && CurrentScene() is { } scene)
        {
            if (_catalog.PublishStoryboardScene(scene)) SetStatus($"Published {scene.Name}.");
            else SetStatus("This scene points at a layout that no longer exists.");
            return;
        }
        if (_template is null) { SetStatus("Choose a template first."); return; }
        var index = _catalog.AddTemplate(_template.Id, publish: true);
        _catalog.SelectLayout(index);
        SetStatus($"{_catalog.Layout.Name} is on air.");
        RefreshStoryboard();
    }

    void UseInStoryboard_Click(object sender, RoutedEventArgs e)
    {
        ScorebugLayout? layout = null;
        if (_selection == ContentSelection.Template && _template is not null)
        {
            var index = _catalog.AddTemplate(_template.Id);
            _catalog.SelectLayout(index);
            layout = _catalog.Layout;
        }
        layout ??= _catalog.Layout;
        var scenes = _catalog.Storyboard.Scenes;
        var scene = new StoryboardScene
        {
            Name = layout.Name, LayoutId = layout.Id,
            Transition = scenes.Count == 0 ? LayoutTransition.Fade : LayoutTransition.Fade,
            TransitionSeconds = 0.5, HoldSeconds = 5
        };
        scenes.Add(scene);
        _catalog.SaveStoryboard(_catalog.Storyboard);
        _sceneIndex = scenes.Count - 1;
        RefreshStoryboard();
        ShowScene(scenes.Count - 1);
        SetStatus($"Added {scene.Name} to the storyboard.");
    }

    // ---------- storyboard ----------

    StoryboardScene? CurrentScene() =>
        _sceneIndex >= 0 && _sceneIndex < _catalog.Storyboard.Scenes.Count ? _catalog.Storyboard.Scenes[_sceneIndex] : null;

    void RefreshStoryboard()
    {
        SceneStrip.Children.Clear();
        var scenes = _catalog.Storyboard.Scenes;
        if (scenes.Count == 0)
        {
            var empty = new Border
            {
                Width = 360, Height = 92, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x15, 0x1D)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x3B, 0x4A)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(2, 3, 0, 0)
            };
            var copy = new StackPanel();
            copy.Children.Add(Text("NO SCENES YET", 11, FontWeights.Bold, "#93A0B2"));
            copy.Children.Add(Text("Pick a template and press USE IN STORYBOARD, or add the layout being edited.", 10, foreground: "#5C6B7E", wrap: true, margin: new Thickness(0, 3, 0, 0)));
            empty.Child = copy;
            SceneStrip.Children.Add(empty);
            StoryboardStatus.Text = "0 scenes · add the layout being edited or a gallery template.";
            LoopInput.IsChecked = _catalog.Storyboard.Loop;
            return;
        }
        _sceneIndex = Math.Clamp(_sceneIndex, 0, scenes.Count - 1);
        for (var index = 0; index < scenes.Count; index++)
        {
            var scene = scenes[index];
            var layout = _catalog.FindLayout(scene.LayoutId);
            var tile = new Button
            {
                Style = (Style)FindResource("SceneCard"),
                BorderThickness = new Thickness(index == _sceneIndex ? 2 : 1),
                ToolTip = $"{scene.Name}\n{TransitionName(scene.Transition)} · {scene.TransitionSeconds:0.##}s · hold {scene.HoldSeconds:0.#}s"
            };
            if (index == _sceneIndex) tile.SetResourceReference(Border.BorderBrushProperty, "Accent");
            var stack = new StackPanel();
            var image = new Image
            {
                Source = layout is null ? null : Thumbnail(layout, 156, 88),
                Width = 156, Height = 88, Stretch = Stretch.UniformToFill
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            stack.Children.Add(new Border
            {
                Height = 88, Background = new SolidColorBrush(Color.FromRgb(7, 11, 16)),
                Child = image, ClipToBounds = true
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{index + 1}. {scene.Name}", FontSize = 10, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 4, 2, 0),
                Foreground = layout is null ? new SolidColorBrush(Color.FromRgb(0xFF, 0x72, 0x72)) : null
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{TransitionName(scene.Transition)} · {scene.TransitionSeconds:0.##}s · hold {scene.HoldSeconds:0.#}s",
                FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0x7E)),
                Margin = new Thickness(2, 1, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis
            });
            tile.Content = stack;
            ThemeManager.SetSkipTheme(tile, true);
            var target = index;
            tile.Click += (_, _) => ShowScene(target);
            SceneStrip.Children.Add(tile);
        }
        StoryboardStatus.Text = $"{scenes.Count} scene{(scenes.Count == 1 ? "" : "s")} · {_catalog.Storyboard.Name}" +
            (_catalog.Storyboard.Scenes.Any(scene => _catalog.FindLayout(scene.LayoutId) is null)
                ? " · a scene points at a deleted layout" : "");
        LoopInput.IsChecked = _catalog.Storyboard.Loop;
    }

    void ShowScene(int index)
    {
        if (index < 0 || index >= _catalog.Storyboard.Scenes.Count) return;
        _sceneIndex = index;
        _selection = ContentSelection.Storyboard;
        var scene = _catalog.Storyboard.Scenes[index];
        var layout = _catalog.FindLayout(scene.LayoutId);
        _previewLayout = layout;
        PreviewTitle.Text = scene.Name;
        PreviewMeta.Text = layout is null ? "   missing layout" : $"   {_catalog.FindDisplayOfLayout(layout.Id)?.Name ?? "display"}";
        PreviewDescription.Text = layout is null
            ? "This scene points at a layout that no longer exists. Pick another layout or remove the scene."
            : $"Storyboard scene {index + 1} · {TransitionName(scene.Transition)} over {scene.TransitionSeconds:0.##}s · holds {scene.HoldSeconds:0.#}s.";
        SlotBadgeText.Text = "SCENE";
        InspectorTitle.Text = "STORYBOARD SCENE";
        BuildSceneInspector(scene);
        RefreshPreview();
        RefreshStoryboard();
        SetStatus($"Scene {index + 1} · {scene.Name}");
    }

    void BuildSceneInspector(StoryboardScene scene)
    {
        var layout = _catalog.FindLayout(scene.LayoutId);
        var panel = new StackPanel();
        panel.Children.Add(Text(scene.Name, 17, FontWeights.Bold));
        panel.Children.Add(Title("SCENE"));
        panel.Children.Add(Label("Name"));
        var name = Box(scene.Name);
        name.TextChanged += (_, _) => { scene.Name = name.Text.Trim(); SaveStoryboard(); };
        panel.Children.Add(name);
        panel.Children.Add(Label("Layout"));
        var layouts = new ComboBox { MinHeight = 30, Margin = new Thickness(0, 0, 0, 5) };
        var choices = _catalog.Displays.SelectMany(display => display.Layouts.Select(layout =>
            new LayoutChoice(display.Name, layout.Id, layout.Name))).ToArray();
        layouts.ItemsSource = choices;
        layouts.SelectedItem = choices.FirstOrDefault(choice => choice.Id == scene.LayoutId);
        layouts.SelectionChanged += (_, _) =>
        {
            if (layouts.SelectedItem is not LayoutChoice choice) return;
            scene.LayoutId = choice.Id;
            SaveStoryboard();
            ShowScene(_sceneIndex);
        };
        panel.Children.Add(layouts);
        panel.Children.Add(Label("Transition in"));
        var transition = new ComboBox { MinHeight = 30, Margin = new Thickness(0, 0, 0, 5) };
        var transitions = TransitionOptions();
        transition.ItemsSource = transitions;
        transition.SelectedItem = transitions.FirstOrDefault(choice => scene.TransitionClip is { } clip
                && choice.Clip is { } candidate && string.Equals(candidate.Id, clip.Id, StringComparison.OrdinalIgnoreCase))
            ?? transitions.FirstOrDefault(choice => choice.Clip is null && choice.Kind == scene.Transition)
            ?? transitions[1];
        transition.SelectionChanged += (_, _) =>
        {
            if (transition.SelectedItem is TransitionChoice choice)
            {
                // A saved hand-over from the Transition Builder replaces the preset for this scene.
                scene.TransitionClip = choice.Clip?.Snapshot();
                scene.Transition = choice.Kind;
                scene.TransitionSeconds = choice.Clip is { } custom ? custom.TotalSeconds
                    : scene.Transition == LayoutTransition.Cut ? 0 : Math.Max(0.1, scene.TransitionSeconds);
                SaveStoryboard();
                RefreshStoryboard();
                ShowScene(_sceneIndex);
            }
        };
        panel.Children.Add(transition);
        panel.Children.Add(Label("Transition seconds (0–5)"));
        var seconds = Box(scene.TransitionSeconds.ToString("0.##"));
        seconds.TextChanged += (_, _) =>
        {
            if (double.TryParse(seconds.Text, out var value))
            {
                scene.TransitionSeconds = Math.Clamp(value, 0, 5);
                SaveStoryboard();
                RefreshStoryboard();
            }
        };
        panel.Children.Add(seconds);
        panel.Children.Add(Label("Hold seconds"));
        var hold = Box(scene.HoldSeconds.ToString("0.#"));
        hold.TextChanged += (_, _) =>
        {
            if (double.TryParse(hold.Text, out var value))
            {
                scene.HoldSeconds = Math.Clamp(value, 0.25, 3600);
                SaveStoryboard();
                RefreshStoryboard();
            }
        };
        panel.Children.Add(hold);
        if (layout is not null) AddLayerTimeline(panel, scene, layout);
        panel.Children.Add(Title("PLAYBACK"));
        panel.Children.Add(FullButton("PLAY FROM THIS SCENE", Play_Click));
        panel.Children.Add(FullButton("PUBLISH THIS SCENE", Publish_Click));
        panel.Children.Add(FullButton("REMOVE SCENE", RemoveScene_Click));
        InspectorHost.Children.Clear();
        InspectorHost.Children.Add(panel);
    }

    /// <summary>
    /// One editable row per layer: when it enters and leaves the scene, plus the real WPF effect
    /// the layer carries. Times are seconds from the scene start; Out 0 means the whole scene.
    /// </summary>
    void AddLayerTimeline(StackPanel panel, StoryboardScene scene, ScorebugLayout layout)
    {
        panel.Children.Add(Title("LAYER TIMELINE"));
        panel.Children.Add(Text("Seconds from the scene start. Out 0 keeps the layer up until the scene ends. Only Blur and Glow are offered because those are what WPF draws.",
            10, foreground: "#7C8A9C", wrap: true));
        panel.Children.Add(SmallButton("CLEAR ALL IN/OUT", () =>
        {
            _catalog.UpdateLayout(scene.LayoutId, edit =>
            {
                foreach (var element in edit.Elements)
                {
                    element.TimelineIn = 0;
                    element.TimelineOut = 0;
                }
            });
            BuildSceneInspector(scene);
            SetStatus("Cleared every layer's in/out point.");
        }));
        foreach (var element in layout.Elements.AsEnumerable().Reverse())
        {
            var item = element;
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0x15, 0x1D)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x31, 0x3E)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 4, 6, 5), Margin = new Thickness(0, 2, 0, 2)
            };
            var stack = new StackPanel();
            var head = new DockPanel();
            var effectBox = new ComboBox
            {
                Width = 76, MinHeight = 24, FontSize = 10, Margin = new Thickness(5, 0, 0, 0),
                ItemsSource = new[] { "None", "Blur", "Glow" }, SelectedIndex = (int)item.Effect,
                ToolTip = "Real-time layer effect, drawn by the WPF compositor"
            };
            DockPanel.SetDock(effectBox, Dock.Right);
            var strength = new TextBox
            {
                Width = 36, MinHeight = 24, FontSize = 10, Margin = new Thickness(4, 0, 0, 0),
                Text = item.EffectAmount.ToString("0.##"), TextAlignment = TextAlignment.Center,
                ToolTip = "Effect strength from 0 to 1"
            };
            DockPanel.SetDock(strength, Dock.Right);
            head.Children.Add(strength);
            head.Children.Add(effectBox);
            head.Children.Add(new TextBlock
            {
                Text = item.Name, FontSize = 10, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(head);

            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var inBox = new TextBox
            {
                Width = 42, MinHeight = 23, FontSize = 10, Text = item.TimelineIn.ToString("0.##"),
                TextAlignment = TextAlignment.Center, ToolTip = "Seconds after the scene starts"
            };
            var outBox = new TextBox
            {
                Width = 42, MinHeight = 23, FontSize = 10,
                Text = item.TimelineOut > 0 ? item.TimelineOut.ToString("0.##") : "",
                TextAlignment = TextAlignment.Center, ToolTip = "Seconds after the scene starts; empty or 0 means the scene end"
            };
            var bar = new Grid { Width = 76, Height = 8, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x25, 0x31, 0x3E)), CornerRadius = new CornerRadius(2) });
            var fill = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x38, 0xD7, 0xA3)),
                CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left
            };
            bar.Children.Add(fill);
            void PaintBar()
            {
                var total = Math.Max(0.1, scene.HoldSeconds);
                var start = Math.Clamp(item.TimelineIn, 0, total);
                var end = item.TimelineOut > 0 ? Math.Clamp(item.TimelineOut, start, total) : total;
                fill.Margin = new Thickness(start / total * 76, 0, 0, 0);
                fill.Width = Math.Max(2, (end - start) / total * 76);
            }
            PaintBar();
            line.Children.Add(new TextBlock { Text = "IN", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x8A, 0x9C)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
            line.Children.Add(inBox);
            line.Children.Add(bar);
            line.Children.Add(new TextBlock { Text = "OUT", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x8A, 0x9C)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 3, 0) });
            line.Children.Add(outBox);
            stack.Children.Add(line);
            card.Child = stack;

            void SaveTiming()
            {
                if (!double.TryParse(inBox.Text, out var start)) return;
                var hasOut = !string.IsNullOrWhiteSpace(outBox.Text);
                if (hasOut && !double.TryParse(outBox.Text, out _)) return;
                double.TryParse(outBox.Text, out var end);
                _catalog.UpdateLayout(scene.LayoutId, edit =>
                {
                    var target = edit.Elements.FirstOrDefault(candidate => candidate.Id == item.Id);
                    if (target is null) return;
                    target.TimelineIn = Math.Clamp(start, 0, scene.HoldSeconds);
                    target.TimelineOut = hasOut ? Math.Clamp(end, target.TimelineIn, scene.HoldSeconds) : 0;
                });
                item.TimelineIn = Math.Clamp(start, 0, scene.HoldSeconds);
                item.TimelineOut = hasOut ? Math.Clamp(end, item.TimelineIn, scene.HoldSeconds) : 0;
                PaintBar();
            }
            inBox.TextChanged += (_, _) => SaveTiming();
            outBox.TextChanged += (_, _) => SaveTiming();
            effectBox.SelectionChanged += (_, _) =>
            {
                var effect = (LayerEffectKind)Math.Clamp(effectBox.SelectedIndex, 0, 2);
                _catalog.UpdateLayout(scene.LayoutId, edit =>
                {
                    var target = edit.Elements.FirstOrDefault(candidate => candidate.Id == item.Id);
                    if (target is null) return;
                    target.Effect = effect;
                    target.EffectAmount = double.TryParse(strength.Text, out var value) ? Math.Clamp(value, 0, 1) : 0.5;
                });
                item.Effect = effect;
                SetStatus($"{item.Name} · {effect.ToString().ToLowerInvariant()} effect.");
            };
            strength.TextChanged += (_, _) =>
            {
                if (effectBox.SelectedIndex <= 0) return;
                if (!double.TryParse(strength.Text, out var value)) return;
                _catalog.UpdateLayout(scene.LayoutId, edit =>
                {
                    var target = edit.Elements.FirstOrDefault(candidate => candidate.Id == item.Id);
                    if (target is not null) target.EffectAmount = Math.Clamp(value, 0, 1);
                });
            };
            panel.Children.Add(card);
        }
    }

    void ShowStoryboardInspector()
    {
        _selection = ContentSelection.Storyboard;
        InspectorTitle.Text = "STORYBOARD";
        var panel = new StackPanel();
        panel.Children.Add(Text(_catalog.Storyboard.Name, 17, FontWeights.Bold));
        panel.Children.Add(Text("A storyboard is a sequence of real layouts. Publishing a scene publishes its layout with the scene's transition.",
            11, foreground: "#B7C3D2", wrap: true, margin: new Thickness(0, 4, 0, 8)));
        panel.Children.Add(Row("Scenes", _catalog.Storyboard.Scenes.Count.ToString()));
        panel.Children.Add(Row("Loop", _catalog.Storyboard.Loop ? "On" : "Off"));
        panel.Children.Add(Title("SEQUENCE"));
        if (_catalog.Storyboard.Scenes.Count == 0)
            panel.Children.Add(Text("No scenes yet. Pick a template and press USE IN STORYBOARD, or add the layout being edited.",
                10, foreground: "#7C8A9C", wrap: true));
        for (var index = 0; index < _catalog.Storyboard.Scenes.Count; index++)
        {
            var scene = _catalog.Storyboard.Scenes[index];
            var target = index;
            panel.Children.Add(SmallButton($"{index + 1}. {scene.Name}", () => ShowScene(target)));
        }
        panel.Children.Add(FullButton("+ ADD CURRENT LAYOUT", AddScene_Click));
        InspectorHost.Children.Clear();
        InspectorHost.Children.Add(panel);
        if (_sceneIndex < _catalog.Storyboard.Scenes.Count) ShowScene(_sceneIndex);
    }

    void AddScene_Click(object sender, RoutedEventArgs e)
    {
        var layout = _catalog.Layout;
        _catalog.Storyboard.Scenes.Add(new StoryboardScene
        {
            Name = layout.Name, LayoutId = layout.Id, Transition = LayoutTransition.Fade,
            TransitionSeconds = 0.5, HoldSeconds = 5
        });
        _catalog.SaveStoryboard(_catalog.Storyboard);
        ShowScene(_catalog.Storyboard.Scenes.Count - 1);
        SetStatus($"Added {layout.Name} to the storyboard.");
    }

    void RemoveScene_Click(object sender, RoutedEventArgs e)
    {
        if (_sceneIndex < 0 || _sceneIndex >= _catalog.Storyboard.Scenes.Count) return;
        var removed = _catalog.Storyboard.Scenes[_sceneIndex];
        _catalog.Storyboard.Scenes.RemoveAt(_sceneIndex);
        _sceneIndex = Math.Clamp(_sceneIndex, 0, Math.Max(0, _catalog.Storyboard.Scenes.Count - 1));
        _catalog.SaveStoryboard(_catalog.Storyboard);
        RefreshStoryboard();
        if (_catalog.Storyboard.Scenes.Count > 0) ShowScene(_sceneIndex);
        else SelectTemplate(_template ?? ContentTemplateCatalog.All[0]);
        SetStatus($"Removed {removed.Name}.");
    }

    void MoveSceneLeft_Click(object sender, RoutedEventArgs e) => MoveScene(-1);
    void MoveSceneRight_Click(object sender, RoutedEventArgs e) => MoveScene(1);

    void MoveScene(int delta)
    {
        var scenes = _catalog.Storyboard.Scenes;
        var next = _sceneIndex + delta;
        if (_sceneIndex < 0 || _sceneIndex >= scenes.Count || next < 0 || next >= scenes.Count) return;
        (scenes[_sceneIndex], scenes[next]) = (scenes[next], scenes[_sceneIndex]);
        _sceneIndex = next;
        _catalog.SaveStoryboard(_catalog.Storyboard);
        RefreshStoryboard();
    }

    void Loop_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        _catalog.Storyboard.Loop = LoopInput.IsChecked == true;
        _catalog.SaveStoryboard(_catalog.Storyboard);
    }

    void SaveStoryboard() => _catalog.UpdateStoryboard(_ => { });

    void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog.Storyboard.Scenes.Count == 0) AddScene_Click(sender, e);
        if (_catalog.Storyboard.Scenes.Count == 0) return;
        _playing = true;
        _sceneElapsed = 0;
        PlayScene(_sceneIndex);
        _playback.Start();
        SetStatus($"Playing storyboard from scene {_sceneIndex + 1}.");
    }

    void PlayScene(int index)
    {
        if (index < 0 || index >= _catalog.Storyboard.Scenes.Count) { Stop(); return; }
        _sceneIndex = index;
        var scene = _catalog.Storyboard.Scenes[index];
        if (!_catalog.PublishStoryboardScene(scene))
        {
            SetStatus($"{scene.Name} points at a deleted layout; skipping.");
            return;
        }
        ShowScene(index);
        StoryboardStatus.Text = $"PLAYING · scene {index + 1} · {scene.Name}";
    }

    void Stop_Click(object sender, RoutedEventArgs e) => Stop();

    void Stop()
    {
        _playing = false;
        _playback.Stop();
        _previewView?.ShowAllLayers();
        SetStatus("Storyboard stopped.");
        RefreshStoryboard();
    }

    void Playback_Tick(object? sender, EventArgs e)
    {
        if (!_playing) { _playback.Stop(); return; }
        _sceneElapsed += _playback.Interval.TotalSeconds;
        _previewView?.SetTimeline(_sceneElapsed);
        var scene = CurrentScene();
        if (scene is null) { Stop(); return; }
        if (_sceneElapsed < scene.HoldSeconds) return;
        _sceneElapsed = 0;
        var next = _sceneIndex + 1;
        if (next >= _catalog.Storyboard.Scenes.Count)
        {
            if (_catalog.Storyboard.Loop) next = 0;
            else { Stop(); return; }
        }
        PlayScene(next);
    }

    // ---------- brand kit ----------

    void ShowBrand()
    {
        _selection = ContentSelection.Brand;
        _template = null;
        InspectorTitle.Text = "BRAND KIT";
        BuildBrandInspector();
        // A full-frame board shows the brand background, logo and wordmark together; a transparent
        // scorebug would hide most of what the kit changes.
        _previewLayout = ContentTemplateCatalog.Build(ScorebugPresets.Stadium, _catalog.Brand);
        PreviewTitle.Text = "Brand kit";
        PreviewMeta.Text = "   applied to the selected template";
        PreviewDescription.Text = "Colours, fonts, logo, wordmark and safe margin shared by every template and marked layout.";
        SlotBadgeText.Text = "BRAND";
        RefreshPreview();
    }

    void BuildBrandInspector()
    {
        _brandFields.Clear();
        _brandFonts.Clear();
        var brand = _catalog.Brand;
        var panel = new StackPanel();
        panel.Children.Add(Text("Brand kit", 17, FontWeights.Bold));
        panel.Children.Add(Text("One identity for every screen. Elements marked with a brand role follow these values.",
            11, foreground: "#B7C3D2", wrap: true, margin: new Thickness(0, 4, 0, 8)));
        AddBrandText(panel, "Name", "Name", brand.Name);
        AddBrandColor(panel, "Primary", "Primary", brand.Primary);
        AddBrandColor(panel, "Surface", "Surface", brand.Surface);
        AddBrandColor(panel, "Accent", "Accent", brand.Accent);
        AddBrandColor(panel, "Background", "Background", brand.Background);
        AddBrandColor(panel, "Text", "Text", brand.Text);
        AddBrandFont(panel, "Heading font", "HeadingFont", brand.HeadingFont);
        AddBrandFont(panel, "Body font", "BodyFont", brand.BodyFont);
        AddBrandText(panel, "Wordmark", "Wordmark", brand.Wordmark);
        panel.Children.Add(Label("Logo"));
        var logoRow = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var logo = new TextBox { Text = brand.LogoPath, MinHeight = 28 };
        logo.SetValue(DockPanel.DockProperty, Dock.Left);
        var browse = new Button { Content = "…", Width = 32, Margin = new Thickness(4, 0, 0, 0), MinHeight = 28 };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "Choose the brand logo", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) == true) logo.Text = dialog.FileName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        logoRow.Children.Add(browse);
        logoRow.Children.Add(logo);
        panel.Children.Add(logoRow);
        _brandFields["LogoPath"] = logo;
        AddBrandText(panel, "Safe margin (px)", "SafeMargin", brand.SafeMargin.ToString("0"));
        panel.Children.Add(Title("APPLY"));
        panel.Children.Add(Text("Saving updates the kit. Applying repaints every layout element with a brand role and every brand-backed canvas.",
            10, foreground: "#7C8A9C", wrap: true));
        panel.Children.Add(FullButton("SAVE BRAND KIT", SaveBrand_Click));
        panel.Children.Add(FullButton("SAVE + APPLY TO EVERY LAYOUT", ApplyBrand_Click));
        InspectorHost.Children.Clear();
        InspectorHost.Children.Add(panel);
    }

    void AddBrandText(StackPanel panel, string label, string key, string value)
    {
        panel.Children.Add(Label(label));
        var box = Box(value);
        _brandFields[key] = box;
        panel.Children.Add(box);
    }

    void AddBrandColor(StackPanel panel, string label, string key, string value)
    {
        panel.Children.Add(Label(label));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var box = new TextBox { Text = value, MinHeight = 28 };
        var swatch = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(4), Margin = new Thickness(5, 0, 5, 0),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x45, 0x52)), BorderThickness = new Thickness(1)
        };
        try { swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); } catch { }
        var pick = new Button { Content = "Pick…", MinHeight = 28, Padding = new Thickness(7, 3, 7, 3) };
        pick.Click += (_, _) =>
        {
            var chosen = ColorPickerWindow.Pick(this, box.Text);
            if (chosen is null) return;
            box.Text = chosen;
            try { swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(chosen)); } catch { }
        };
        DockPanel.SetDock(pick, Dock.Right);
        DockPanel.SetDock(swatch, Dock.Right);
        row.Children.Add(pick);
        row.Children.Add(swatch);
        row.Children.Add(box);
        _brandFields[key] = box;
        panel.Children.Add(row);
    }

    void AddBrandFont(StackPanel panel, string label, string key, string value)
    {
        panel.Children.Add(Label(label));
        var combo = new ComboBox { MinHeight = 30, Margin = new Thickness(0, 0, 0, 5), ItemsSource = FontCatalog.All };
        combo.SelectedItem = FontCatalog.All.FirstOrDefault(font =>
            string.Equals(font.Source, value, StringComparison.OrdinalIgnoreCase))
            ?? FontCatalog.All.FirstOrDefault(font => string.Equals(font.Label, value, StringComparison.OrdinalIgnoreCase));
        _brandFonts[key] = combo;
        panel.Children.Add(combo);
    }

    BrandKit ReadBrand()
    {
        string Value(string key, string fallback) => _brandFields.TryGetValue(key, out var box) && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : fallback;
        string Font(string key, string fallback) =>
            _brandFonts.TryGetValue(key, out var combo) && combo.SelectedItem is FontChoice choice ? choice.Source : fallback;
        var brand = new BrandKit
        {
            Name = Value("Name", _catalog.Brand.Name),
            Primary = Value("Primary", _catalog.Brand.Primary),
            Surface = Value("Surface", _catalog.Brand.Surface),
            Accent = Value("Accent", _catalog.Brand.Accent),
            Background = Value("Background", _catalog.Brand.Background),
            Text = Value("Text", _catalog.Brand.Text),
            HeadingFont = Font("HeadingFont", _catalog.Brand.HeadingFont),
            BodyFont = Font("BodyFont", _catalog.Brand.BodyFont),
            LogoPath = Value("LogoPath", _catalog.Brand.LogoPath),
            Wordmark = Value("Wordmark", _catalog.Brand.Wordmark)
        };
        if (double.TryParse(Value("SafeMargin", "48"), out var margin)) brand.SafeMargin = margin;
        brand.Normalize();
        return brand;
    }

    void SaveBrand_Click(object sender, RoutedEventArgs e)
    {
        _catalog.SaveBrand(ReadBrand());
        BrandStatus.Text = $"BRAND · {_catalog.Brand.Name}";
        SetStatus("Brand kit saved. Existing artwork is unchanged until you apply it.");
        ShowBrand();
    }

    void ApplyBrand_Click(object sender, RoutedEventArgs e)
    {
        _catalog.SaveBrand(ReadBrand());
        var painted = _catalog.ApplyBrandToAll();
        BrandStatus.Text = $"BRAND · {_catalog.Brand.Name}";
        SetStatus($"Brand applied to {painted} layout{(painted == 1 ? "" : "s")}.");
        RefreshStoryboard();
        ShowBrand();
    }

    // ---------- sponsors ----------

    void ShowSponsors()
    {
        _selection = ContentSelection.Sponsors;
        _template = null;
        _previewLayout = null;
        InspectorTitle.Text = "SPONSOR INVENTORY";
        PreviewTitle.Text = "Sponsor inventory";
        PreviewMeta.Text = "   tagged regions and assets";
        PreviewDescription.Text = "Name the regions on a board, attach the sponsor asset and the same package travels in a content pack.";
        SlotBadgeText.Text = "SPONSOR";
        DisposePreview();
        BuildSponsorInspector();
    }

    void BuildSponsorInspector()
    {
        var panel = new StackPanel();
        panel.Children.Add(Text("Sponsor inventory", 17, FontWeights.Bold));
        panel.Children.Add(Text("A sponsor slot maps a sold package to a named region on a template. Elements carry the same tag.",
            11, foreground: "#B7C3D2", wrap: true, margin: new Thickness(0, 4, 0, 8)));
        panel.Children.Add(Label("Name"));
        var name = Box("");
        panel.Children.Add(name);
        panel.Children.Add(Label("Region tag"));
        var tag = Box("Presenting sponsor");
        panel.Children.Add(tag);
        panel.Children.Add(Label("Asset"));
        var asset = Box("");
        var assetRow = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var browse = new Button { Content = "…", Width = 32, MinHeight = 28, Margin = new Thickness(4, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "Choose the sponsor asset", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) == true) asset.Text = dialog.FileName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        assetRow.Children.Add(browse);
        assetRow.Children.Add(asset);
        panel.Children.Add(assetRow);
        panel.Children.Add(FullButton("APPLY SLOTS TO THIS DISPLAY", (_, _) =>
        {
            var painted = _catalog.ActiveDisplay.Layouts.Sum(_catalog.ApplySponsors);
            _catalog.Update(_ => { });
            SetStatus(painted == 0
                ? "No tagged sponsor regions found on this display."
                : $"Filled {painted} sponsor region{(painted == 1 ? "" : "s")} on {_catalog.ActiveDisplay.Name}.");
        }));
        panel.Children.Add(FullButton("ADD SPONSOR SLOT", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { SetStatus("Give the sponsor a name first."); return; }
            var sponsors = _catalog.Sponsors.Select(slot => slot.Copy()).ToList();
            sponsors.Add(new SponsorSlot { Name = name.Text, Tag = tag.Text, AssetPath = asset.Text });
            _catalog.SaveSponsors(sponsors);
            BuildSponsorInspector();
            SetStatus($"Added sponsor {name.Text.Trim()}.");
        }));
        panel.Children.Add(Title("SLOTS"));
        if (_catalog.Sponsors.Count == 0)
            panel.Children.Add(Text("No sponsor slots yet.", 10, foreground: "#7C8A9C"));
        foreach (var sponsor in _catalog.Sponsors)
        {
            var slot = sponsor;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = new Button { Content = "Remove", MinHeight = 26, FontSize = 10, Padding = new Thickness(6, 2, 6, 2) };
            remove.Click += (_, _) =>
            {
                _catalog.SaveSponsors(_catalog.Sponsors.Where(item => item.Id != slot.Id));
                BuildSponsorInspector();
            };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var text = new StackPanel();
            text.Children.Add(Text(slot.Name, 12, FontWeights.SemiBold));
            text.Children.Add(Text($"{slot.Tag} · {(string.IsNullOrWhiteSpace(slot.AssetPath) ? "no asset" : Path.GetFileName(slot.AssetPath))}",
                9, foreground: "#7C8A9C"));
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        InspectorHost.Children.Clear();
        InspectorHost.Children.Add(panel);
    }

    // ---------- packs ----------

    void ShowPacks()
    {
        _selection = ContentSelection.Packs;
        _template = null;
        InspectorTitle.Text = "CONTENT PACKS";
        PreviewTitle.Text = "Content packs";
        PreviewMeta.Text = "   one file, every asset";
        PreviewDescription.Text = "Bundle the active display, brand, storyboard, sponsor inventory and every image into one portable .spk file.";
        SlotBadgeText.Text = "PACK";
        DisposePreview();
        var panel = new StackPanel();
        panel.Children.Add(Text("Content packs", 17, FontWeights.Bold));
        panel.Children.Add(Text("A pack exported here opens on another machine with its layouts, brand, fonts and images. Missing assets are reported before export and again on import.",
            11, foreground: "#B7C3D2", wrap: true, margin: new Thickness(0, 4, 0, 8)));
        panel.Children.Add(Row("Active display", _catalog.ActiveDisplay.Name));
        panel.Children.Add(Row("Layouts", _catalog.ActiveDisplay.Layouts.Count.ToString()));
        panel.Children.Add(Row("Storyboard scenes", _catalog.Storyboard.Scenes.Count.ToString()));
        panel.Children.Add(Row("Sponsor slots", _catalog.Sponsors.Count.ToString()));
        panel.Children.Add(Title("PORTABLE CONTENT"));
        panel.Children.Add(FullButton("EXPORT ACTIVE DISPLAY…", ExportPack_Click));
        panel.Children.Add(FullButton("IMPORT A PACK…", ImportPack_Click));
        if (_nodes is not null)
            panel.Children.Add(FullButton("SEND ACTIVE DISPLAY + ASSETS TO NODES", SendToNodes_Click));
        InspectorHost.Children.Clear();
        InspectorHost.Children.Add(panel);
    }

    async void SendToNodes_Click(object sender, RoutedEventArgs e)
    {
        if (_nodes is null) return;
        SetStatus("Building the node package with every referenced media file…");
        var result = await _nodes.SendToAllAsync("packs");
        SetStatus(result.Message);
    }

    void ExportPack_Click(object sender, RoutedEventArgs e)
    {
        var display = _catalog.SnapshotDisplay(_catalog.ActiveDisplayIndex);
        if (display is null) { SetStatus("There is no active display to export."); return; }
        var dialog = new SaveFileDialog
        {
            Title = "Export a content pack",
            Filter = "ScorePlus Content Pack (*.spk)|*.spk|All files (*.*)|*.*",
            FileName = $"{SafeFileName(display.Name)}.spk"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var ids = display.Layouts.Select(layout => layout.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var storyboard = _catalog.Storyboard.Copy();
            storyboard.Scenes.RemoveAll(scene => !ids.Contains(scene.LayoutId));
            var pack = new ContentPack
            {
                Name = $"{display.Name} · {_catalog.Brand.Name}",
                Brand = _catalog.Brand.Copy(),
                Storyboard = storyboard,
                Displays = [display],
                Sponsors = _catalog.Sponsors.Select(slot => slot.Copy()).ToList(),
                Fonts = display.Layouts.SelectMany(layout => layout.Elements)
                    .Select(element => element.FontFamily)
                    .Where(font => !string.IsNullOrWhiteSpace(font))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
            if (!string.IsNullOrWhiteSpace(_catalog.Brand.HeadingFont)) pack.Fonts.Add(_catalog.Brand.HeadingFont);
            if (!string.IsNullOrWhiteSpace(_catalog.Brand.BodyFont)) pack.Fonts.Add(_catalog.Brand.BodyFont);
            foreach (var font in FontCatalog.All.Where(font => font.Imported && font.FilePath is not null))
            {
                pack.Fonts.Add(font.Source);
                pack.FontFiles[font.Source] = font.FilePath!;
            }
            ContentPacks.Export(dialog.FileName, pack);
            SetStatus(pack.MissingAssets.Count == 0
                ? $"Exported {display.Layouts.Count} layouts, {storyboard.Scenes.Count} scenes and their assets."
                : $"Exported, but {pack.MissingAssets.Count} asset(s) were already missing: {string.Join(", ", pack.MissingAssets.Take(3).Select(Path.GetFileName))}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}");
        }
    }

    void ImportPack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a content pack",
            Filter = "ScorePlus Content Pack (*.spk)|*.spk|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = ContentPacks.Import(dialog.FileName);
            if (imported.RestoredFonts.Count > 0) FontCatalog.Refresh();
            // A file-backed font's family string contains the old machine's file path. Match the
            // restored file to the freshly scanned font list and rewrite the layouts before saving.
            var fontMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (family, target) in imported.RestoredFonts)
            {
                var restored = FontCatalog.All.FirstOrDefault(font =>
                    font.FilePath is not null && string.Equals(Path.GetFullPath(font.FilePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase));
                if (restored is not null) fontMap[family] = restored.Source;
            }
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in imported.Pack.Displays)
            {
                var index = _catalog.ImportDisplay(source);
                var target = _catalog.Displays[index];
                for (var i = 0; i < Math.Min(source.Layouts.Count, target.Layouts.Count); i++)
                    map[source.Layouts[i].Id] = target.Layouts[i].Id;
            }
            if (fontMap.Count > 0)
            {
                foreach (var display in _catalog.Displays)
                foreach (var layout in display.Layouts)
                foreach (var element in layout.Elements)
                    if (fontMap.TryGetValue(element.FontFamily, out var mapped)) element.FontFamily = mapped;
                _catalog.Update(_ => { });
            }
            var scenes = _catalog.Storyboard.Copy();
            foreach (var scene in imported.Pack.Storyboard.Scenes)
            {
                if (!map.TryGetValue(scene.LayoutId, out var layoutId)) continue;
                var copy = scene.Copy();
                copy.Id = Guid.NewGuid().ToString("N");
                copy.LayoutId = layoutId;
                scenes.Scenes.Add(copy);
            }
            scenes.Normalize();
            _catalog.SaveStoryboard(scenes);
            var importedBrand = imported.Pack.Brand;
            if (importedBrand.HeadingFont is { Length: > 0 } heading && fontMap.TryGetValue(heading, out var mappedHeading))
                importedBrand.HeadingFont = mappedHeading;
            if (importedBrand.BodyFont is { Length: > 0 } body && fontMap.TryGetValue(body, out var mappedBody))
                importedBrand.BodyFont = mappedBody;
            _catalog.SaveBrand(importedBrand);
            var sponsors = _catalog.Sponsors.Select(slot => slot.Copy()).ToList();
            sponsors.AddRange(imported.Pack.Sponsors);
            _catalog.SaveSponsors(sponsors);
            BrandStatus.Text = $"BRAND · {_catalog.Brand.Name}";
            BuildTree();
            RefreshStoryboard();
            SetStatus(imported.Warnings.Count == 0
                ? $"Imported {imported.Pack.Displays.Count} display(s), {scenes.Scenes.Count - imported.Pack.Storyboard.Scenes.Count + imported.Pack.Storyboard.Scenes.Count} scenes and {imported.RestoredAssets.Count} assets."
                : $"Imported with {imported.Warnings.Count} warning(s): {imported.Warnings[0]}");
            ShowPacks();
        }
        catch (Exception ex)
        {
            SetStatus($"Import refused: {ex.Message}");
        }
    }

    // ---------- helpers ----------

    static readonly TransitionChoice[] TransitionChoices =
    [
        new(LayoutTransition.Cut, "Cut — instant"),
        new(LayoutTransition.Fade, "Dissolve"),
        new(LayoutTransition.SlideLeft, "Slide left"),
        new(LayoutTransition.SlideRight, "Slide right"),
        new(LayoutTransition.SlideUp, "Slide up"),
        new(LayoutTransition.SlideDown, "Slide down"),
        new(LayoutTransition.Zoom, "Zoom"),
        new(LayoutTransition.Move, "Move items")
    ];

    sealed record TransitionChoice(LayoutTransition Kind, string Label, MotionClip? Clip = null) { public override string ToString() => Label; }

    /// <summary>Built-in hand-overs plus every transition saved in the builder.</summary>
    IReadOnlyList<TransitionChoice> TransitionOptions()
    {
        var options = new List<TransitionChoice>(TransitionChoices);
        options.AddRange(_catalog.Transitions
            .Where(clip => clip is not null && !clip.IsEmpty)
            .Select(clip => new TransitionChoice(LayoutTransition.Fade, $"Custom · {clip.Name}", clip)));
        return options;
    }
    sealed record LayoutChoice(string Display, string Id, string Name) { public override string ToString() => $"{Display} · {Name}"; }

    static string TransitionName(LayoutTransition transition) =>
        TransitionChoices.FirstOrDefault(choice => choice.Kind == transition)?.Label.Split(' ')[0] ?? transition.ToString();

    void SetStatus(string text) => ContentStatus.Text = text;

    static TextBlock Text(string text, double size, FontWeight? weight = null, string? foreground = null,
        bool wrap = false, Thickness margin = default)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, Margin = margin, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        if (weight is not null) block.FontWeight = weight.Value;
        if (foreground is not null) block.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground));
        return block;
    }

    static TextBlock Title(string text) => new()
    {
        Text = text, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)),
        Margin = new Thickness(0, 12, 0, 4)
    };

    static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x8A, 0x9C)),
        Margin = new Thickness(0, 4, 0, 2)
    };

    TextBox Box(string value) => new()
    {
        Text = value, MinHeight = 28, Margin = new Thickness(0, 0, 0, 5)
    };

    static Grid Row(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = Text(label, 11, foreground: "#93A0B2");
        var valueText = Text(value, 11, FontWeights.SemiBold);
        valueText.HorizontalAlignment = HorizontalAlignment.Right;
        valueText.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(valueText, 1);
        row.Children.Add(name);
        row.Children.Add(valueText);
        return row;
    }

    Button FullButton(string label, RoutedEventHandler handler)
    {
        var button = new Button { Content = label, MinHeight = 32, Margin = new Thickness(0, 3, 0, 0), FontSize = 11 };
        button.Click += handler;
        return button;
    }

    static Button SmallButton(string label, Action action)
    {
        var button = new Button { Content = label, MinHeight = 26, FontSize = 10, Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 2, 4, 2) };
        button.Click += (_, _) => action();
        return button;
    }

    static string SafeFileName(string name)
    {
        var cleaned = new string(name.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray()).Trim();
        return cleaned.Length == 0 ? "content" : cleaned;
    }

    ImageSource Thumbnail(ScorebugLayout layout, int width, int height)
    {
        try
        {
            var view = new ScorebugCanvasView(_catalog, _engine, stateProvider: () => _engine.State,
                playback: null, layoutProvider: () => layout)
            {
                Width = width, Height = height
            };
            view.ShowAllLayers();
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            bitmap.Freeze();
            view.Dispose();
            return bitmap;
        }
        catch { return new DrawingImage(); }
    }
}
