using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// Builds every brush the graphic paints with: flat colours, multi-stop gradients, radial gradients,
/// the material finish on top of them, and the per-layer opacity that lets a plate, its outline and
/// its text fade independently. The studio, the previews and the audience output all come through
/// here, so a finish looks the same on every screen.
/// </summary>
internal static class Paint
{
    public static Color ColorOf(string? value, Color fallback = default)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            var parsed = (Color)ColorConverter.ConvertFromString(value)!;
            return parsed;
        }
        catch { return fallback; }
    }

    public static Color Fade(Color color, double factor)
    {
        factor = Math.Clamp(double.IsFinite(factor) ? factor : 1, 0, 1);
        return Color.FromArgb((byte)Math.Round(color.A * factor), color.R, color.G, color.B);
    }

    public static Brush WithAlpha(Brush brush, double factor)
    {
        factor = Math.Clamp(double.IsFinite(factor) ? factor : 1, 0, 1);
        if (factor >= 0.999) return brush;
        switch (brush)
        {
            case SolidColorBrush solid:
                var faded = new SolidColorBrush(Fade(solid.Color, factor));
                faded.Freeze();
                return faded;
            case GradientBrush gradient:
                var copy = gradient.Clone();
                foreach (var stop in copy.GradientStops) stop.Color = Fade(stop.Color, factor);
                copy.Freeze();
                return copy;
            case ImageBrush image:
                var fadedImage = image.Clone();
                fadedImage.Opacity = image.Opacity * factor;
                fadedImage.Freeze();
                return fadedImage;
            case DrawingBrush drawing:
                var fadedDrawing = drawing.Clone();
                fadedDrawing.Opacity = drawing.Opacity * factor;
                fadedDrawing.Freeze();
                return fadedDrawing;
            default:
                var translucent = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * factor), 255, 255, 255));
                translucent.Freeze();
                return translucent;
        }
    }

    /// <summary>
    /// The text fill: a flat colour, a gradient, or either of those carrying a material finish.
    /// The layer's own opacity is applied by the text block (so it fades exactly once), which is
    /// why the brushes here are built at full alpha.
    /// </summary>
    public static Brush Text(ScorebugElement item, GameState? state)
    {
        var flat = ColorOf(state?.ColorFor(item.ForegroundSource) ?? item.Foreground, Colors.White);
        var paint = item.TextGradientPaint;
        var brush = paint is { IsUsable: true }
            ? Gradient(paint, flat, 1)
            : item.TextTexture is { Enabled: true }
                ? Linear([(0, flat), (1, flat)], 90, 1)
                : Solid(flat, 1);
        if (item.TextTexture is { Enabled: true } texture) return ApplyTextureToBrush(brush, texture, flat);
        return brush;
    }

    /// <summary>The background: flat, multi-stop or radial, carrying its finish and its own opacity.</summary>
    public static Brush Background(ScorebugElement item, GameState? state)
    {
        var slot = state?.ColorFor(item.BackgroundSource);
        var flat = ColorOf(string.IsNullOrWhiteSpace(slot) ? item.Background : slot, Colors.Transparent);
        var opacity = item.BackgroundOpacity;
        var paint = item.BackgroundGradientPaint;
        Brush brush;
        if (paint is { IsUsable: true })
            brush = Gradient(paint, flat, opacity);
        else if (item.BackgroundGradient)
        {
            // A layout written before multi-stop gradients: two colours and an angle.
            var startSlot = state?.ColorFor(item.BackgroundSource);
            var endSlot = state?.ColorFor(item.BackgroundSource2);
            var start = ColorOf(string.IsNullOrWhiteSpace(startSlot) ? item.Background : startSlot, Colors.Transparent);
            var end = ColorOf(string.IsNullOrWhiteSpace(endSlot) ? item.BackgroundColor2 : endSlot, Colors.Transparent);
            brush = Linear([(0, start), (1, end)], item.GradientAngle, opacity);
        }
        else
            brush = Solid(flat, opacity);
        if (item.BackgroundTexture is { Enabled: true } texture) return TexturedBackground(brush, texture, flat);
        return brush;
    }

    /// <summary>A material wash that can be clipped to a logo's own pixels. It uses the same
    /// finish knobs as a plate, so moving a slider also restyles the crest instead of only the
    /// rectangle behind it.</summary>
    public static Brush? TextureWash(ScorebugTexture? texture, Color fallback)
    {
        if (texture is not { Enabled: true }) return null;
        var colour = ColorOf(texture.Tint, fallback);
        return TexturedBackground(Brushes.Transparent, texture, colour);
    }

    /// <summary>The outline, faded by its own opacity rather than the element's.</summary>
    public static Brush Border(ScorebugElement item, GameState? state)
    {
        if (item.BorderThickness <= 0) return Brushes.Transparent;
        var colour = ColorOf(state?.ColorFor(item.BorderSource) ?? item.BorderColor, Colors.White);
        return Solid(colour, item.BorderOpacity);
    }

    /// <summary>A comparable description so views only rebuild a brush when the paint changed.</summary>
    public static string Signature(ScorebugElement item, GameState? state) =>
        $"{item.BackgroundOpacity:0.###}|{item.BackgroundTexture?.Signature() ?? "off"}|" +
        (item.BackgroundGradientPaint is { IsUsable: true } paint
            ? paint.Signature()
            : item.BackgroundGradient
                ? $"legacy|{state?.ColorFor(item.BackgroundSource) ?? item.Background}|{state?.ColorFor(item.BackgroundSource2) ?? item.BackgroundColor2}|{item.GradientAngle:0.###}"
                : state?.ColorFor(item.BackgroundSource) ?? item.Background);

    /// <summary>A comparable description of the text fill, for the same reason.</summary>
    public static string TextSignature(ScorebugElement item, GameState? state) =>
        $"{item.TextOpacity:0.###}|{item.TextTexture?.Signature() ?? "off"}|" +
        (item.TextGradientPaint is { IsUsable: true } paint
            ? paint.Signature()
            : state?.ColorFor(item.ForegroundSource) ?? item.Foreground);

    static Brush Solid(Color colour, double opacity)
    {
        var brush = new SolidColorBrush(Fade(colour, opacity));
        brush.Freeze();
        return brush;
    }

    static Brush Gradient(ScorebugGradient paint, Color fallback, double opacity)
    {
        var stops = paint.Stops
            .Where(stop => stop is not null)
            .Select(stop => (stop.Offset, ColorOf(stop.Color, fallback)))
            .ToList();
        if (stops.Count < 2) return Solid(fallback, opacity);
        if (!paint.Radial) return Linear(stops, paint.Angle, opacity);
        var brush = new RadialGradientBrush
        {
            Center = new Point(paint.CenterX, paint.CenterY),
            GradientOrigin = new Point(paint.CenterX, paint.CenterY),
            RadiusX = paint.RadiusX,
            RadiusY = paint.RadiusY
        };
        foreach (var (offset, colour) in stops)
            brush.GradientStops.Add(new GradientStop(Fade(colour, opacity), Math.Clamp(offset, 0, 1)));
        brush.Freeze();
        return brush;
    }

    static Brush Linear(IReadOnlyList<(double Offset, Color Colour)> stops, double angle, double opacity)
    {
        var radians = (((angle % 360) + 360) % 360) * Math.PI / 180.0;
        var dx = Math.Cos(radians) / 2;
        var dy = Math.Sin(radians) / 2;
        var brush = new LinearGradientBrush(
            new GradientStopCollection(stops.Select(stop => new GradientStop(Fade(stop.Colour, opacity), Math.Clamp(stop.Offset, 0, 1)))),
            new Point(0.5 - dx, 0.5 - dy),
            new Point(0.5 + dx, 0.5 + dy));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The material finish on a plate: a highlight band, an ambient wash, a satin sheen, a metallic
    /// edge, an iridescent shift and a tiled grain. WPF has no shading pipeline here, so these layer
    /// into a drawing that reads as a surface rather than a flat rectangle.
    /// </summary>
    static Brush TexturedBackground(Brush baseBrush, ScorebugTexture texture, Color flat)
    {
        // A tint is only a cast. Treating its default white as the surface colour washed every
        // material with the same white bands, which made eleven different kinds look identical.
        var textureColour = texture.TintAmount > 0.001
            ? Mix(flat, ColorOf(texture.Tint, flat), texture.TintAmount)
            : flat;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(baseBrush, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));

        // The broad bands: a lit edge, a shadowed body, a highlight and a reflected ambience.
        var bands = new GradientStopCollection();
        void Band(double offset, double light)
        {
            var colour = MotionColors.Shift(ToHex(textureColour), light);
            var alpha = (byte)Math.Round(255 * Math.Clamp(texture.Opacity * (0.35 + Math.Abs(light) * 0.65), 0, 1));
            bands.Add(new GradientStop(Color.FromArgb(alpha, ColorOf(colour).R, ColorOf(colour).G, ColorOf(colour).B), Math.Clamp(offset, 0, 1)));
        }
        var satin = Math.Clamp(texture.Satin, 0, 1);
        var shine = Math.Clamp(texture.Shine + texture.Glossiness * 0.5, 0, 1);
        bands.Add(new GradientStop(Colors.Transparent, 0));
        Band(0.14, 0.35 + shine * 0.45);
        Band(0.34 + satin * 0.08, -(0.2 + texture.Roughness * 0.45));
        Band(0.52, 0.15 + texture.Ambience * 0.35 + satin * 0.25);
        Band(0.72, -(0.1 + texture.Roughness * 0.25));
        Band(0.92, 0.25 + shine * 0.55 + texture.Metallic * 0.2);
        bands.Add(new GradientStop(Colors.Transparent, 1));
        var radians = (((texture.Angle % 360) + 360) % 360) * Math.PI / 180.0;
        var dx = Math.Cos(radians) / 2;
        var dy = Math.Sin(radians) / 2;
        var bandBrush = new LinearGradientBrush(bands, new Point(0.5 - dx, 0.5 - dy), new Point(0.5 + dx, 0.5 + dy));
        bandBrush.Freeze();
        group.Children.Add(new GeometryDrawing(bandBrush, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));

        // Emboss / bevel: a thin lit edge on one side and a shadow on the other.
        var relief = Math.Clamp(texture.Emboss + texture.Bevel * 0.6, 0, 1);
        if (relief > 0.01)
        {
            var light = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * relief * 0.55), 255, 255, 255));
            var shadow = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * relief * 0.6), 0, 0, 0));
            light.Freeze(); shadow.Freeze();
            var edge = Math.Max(0.004, relief * 0.035);
            group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 1, edge))));
            group.Children.Add(new GeometryDrawing(shadow, null, new RectangleGeometry(new Rect(0, 1 - edge, 1, edge))));
        }

        // Iridescence: a hue-shifted wash across the surface.
        if (texture.Iridescence > 0.01)
        {
            var iridescent = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            var baseHex = ToHex(flat);
            iridescent.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            iridescent.GradientStops.Add(new GradientStop(ColorOf(MotionColors.Hue(baseHex, 0.18), flat) is var h1
                ? Color.FromArgb((byte)Math.Round(255 * texture.Iridescence * 0.55), h1.R, h1.G, h1.B) : Colors.Transparent, 0.38));
            iridescent.GradientStops.Add(new GradientStop(ColorOf(MotionColors.Hue(baseHex, 0.55), flat) is var h2
                ? Color.FromArgb((byte)Math.Round(255 * texture.Iridescence * 0.5), h2.R, h2.G, h2.B) : Colors.Transparent, 0.68));
            iridescent.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            iridescent.Freeze();
            group.Children.Add(new GeometryDrawing(iridescent, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        }

        // Grain: a stable speckle, so the same finish looks the same every time it is drawn.
        if (texture.Grain > 0.01)
        {
            var dots = new GeometryGroup();
            var random = new Random(texture.Signature().GetHashCode(StringComparison.Ordinal));
            var count = (int)Math.Round(30 + texture.Grain * 220);
            var size = 0.0016 * Math.Max(0.4, texture.Scale);
            for (var index = 0; index < count; index++)
            {
                var x = random.NextDouble();
                var y = random.NextDouble();
                dots.Children.Add(new EllipseGeometry(new Point(x, y), size * (1 + random.NextDouble()), size * (1 + random.NextDouble())));
            }
            var grainColour = texture.Metallic > 0.4 ? Colors.White : Colors.Black;
            var grain = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(texture.Grain * 0.45, 0, 1)), grainColour.R, grainColour.G, grainColour.B));
            grain.Freeze();
            group.Children.Add(new GeometryDrawing(grain, null, dots));
        }

        // The motif that makes each material recognisable at a glance.
        AddMaterialPattern(group, texture, textureColour);

        // Contrast: a soft black wash at the foot of the plate.
        if (texture.Contrast > 0.01)
        {
            var wash = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            wash.GradientStops.Add(new GradientStop(Colors.Transparent, 0.45));
            wash.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(255 * texture.Contrast * 0.55), 0, 0, 0), 1));
            wash.Freeze();
            group.Children.Add(new GeometryDrawing(wash, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        }

        group.Freeze();
        var brush = new DrawingBrush(group)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = new Rect(0, 0, 1, 1)
        };
        brush.Freeze();
        return brush;
    }

    static byte Alpha(double value) => (byte)Math.Round(255 * Math.Clamp(value, 0, 1));

    static double Luma(Color colour) => 0.299 * colour.R + 0.587 * colour.G + 0.114 * colour.B;

    /// <summary>
    /// The motif that belongs to one material: paper fibres, a fabric weave, stone mottling,
    /// concrete pitting, brushed metal, chrome bands, glass streaks, frost haze, velvet pile,
    /// hologram bands. The shared highlight bands alone made every preset read as the same
    /// glossy wash; this is what makes "rough paper" look like paper.
    /// </summary>
    static void AddMaterialPattern(DrawingGroup group, ScorebugTexture texture, Color surface)
    {
        var kind = (texture.Kind ?? "Matte").Trim();
        var scale = Math.Clamp(texture.Scale, 0.1, 12);
        var strength = Math.Clamp(0.25 + texture.Opacity * 0.9, 0, 1);
        var ink = Luma(surface) >= 140 ? Colors.Black : Colors.White;
        var random = new Random($"material|{kind}|{Math.Round(scale, 2)}|{Math.Round(texture.Grain, 2)}".GetHashCode(StringComparison.Ordinal));

        void Fill(Geometry geometry, Color colour, double alpha)
        {
            if (geometry is GeometryGroup shape && shape.Children.Count == 0) return;
            var brush = new SolidColorBrush(Color.FromArgb(Alpha(alpha), colour.R, colour.G, colour.B));
            brush.Freeze();
            geometry.Freeze();
            group.Children.Add(new GeometryDrawing(brush, null, geometry));
        }

        void Stroke(GeometryGroup geometry, Color colour, double alpha, double thickness)
        {
            if (geometry.Children.Count == 0) return;
            var brush = new SolidColorBrush(Color.FromArgb(Alpha(alpha), colour.R, colour.G, colour.B));
            brush.Freeze();
            var pen = new Pen(brush, Math.Max(0.0008, thickness));
            pen.Freeze();
            geometry.Freeze();
            group.Children.Add(new GeometryDrawing(null, pen, geometry));
        }

        switch (kind)
        {
            case "Paper":
            {
                // Fibres lie in every direction, with a few longer strands and soft pulp blotches.
                var fibres = new GeometryGroup();
                for (var i = 0; i < 160; i++)
                {
                    var x = random.NextDouble();
                    var y = random.NextDouble();
                    var length = (0.006 + random.NextDouble() * 0.05) / Math.Max(0.6, scale);
                    var angle = (random.NextDouble() - 0.5) * 1.1;
                    fibres.Children.Add(new LineGeometry(new Point(x, y),
                        new Point(x + Math.Cos(angle) * length, y + Math.Sin(angle) * length)));
                }
                Stroke(fibres, ink, 0.17 * strength, 0.0014 * Math.Max(0.7, scale));
                var pulp = new GeometryGroup();
                for (var i = 0; i < 10; i++)
                    pulp.Children.Add(new EllipseGeometry(new Point(random.NextDouble(), random.NextDouble()),
                        (0.012 + random.NextDouble() * 0.06) * scale, (0.008 + random.NextDouble() * 0.04) * scale));
                Fill(pulp, ink, 0.035 * strength);
                break;
            }
            case "Fabric":
            {
                var rows = new GeometryGroup();
                var columns = new GeometryGroup();
                const int threads = 34;
                var thickness = 0.0016 * Math.Max(0.6, scale);
                for (var i = 0; i <= threads; i++)
                {
                    var p = (double)i / threads;
                    rows.Children.Add(new LineGeometry(new Point(0, p), new Point(1, p)));
                    columns.Children.Add(new LineGeometry(new Point(p, 0), new Point(p, 1)));
                }
                Stroke(rows, ink, 0.09 * strength, thickness);
                Stroke(columns, ink, 0.07 * strength, thickness);
                // A heavier slub every so often stops the weave reading as graph paper.
                var slub = new GeometryGroup();
                for (var i = 0; i < 14; i++)
                {
                    var p = random.NextDouble();
                    slub.Children.Add(random.Next(2) == 0
                        ? new LineGeometry(new Point(0, p), new Point(1, p))
                        : new LineGeometry(new Point(p, 0), new Point(p, 1)));
                }
                Stroke(slub, ink, 0.06 * strength, thickness * 2.2);
                break;
            }
            case "Velvet":
            {
                // Pile: fine vertical hairs plus soft folds, with no hard grain.
                var pile = new GeometryGroup();
                for (var i = 0; i < 90; i++)
                {
                    var x = random.NextDouble();
                    var y = random.NextDouble();
                    var length = 0.02 + random.NextDouble() * 0.1;
                    pile.Children.Add(new LineGeometry(new Point(x, y), new Point(x + (random.NextDouble() - 0.5) * 0.006, y + length)));
                }
                Stroke(pile, ink, 0.05 * strength, 0.0012);
                var folds = new GeometryGroup();
                for (var i = 0; i < 9; i++)
                    folds.Children.Add(new RectangleGeometry(new Rect(i / 9.0, 0, 0.055, 1)));
                Fill(folds, ink, 0.022 * strength);
                break;
            }
            case "Stone":
            {
                var mottle = new GeometryGroup();
                for (var i = 0; i < 20; i++)
                    mottle.Children.Add(new EllipseGeometry(new Point(random.NextDouble(), random.NextDouble()),
                        (0.02 + random.NextDouble() * 0.1) * scale, (0.012 + random.NextDouble() * 0.07) * scale));
                Fill(mottle, ink, 0.055 * strength);
                var cracks = new GeometryGroup();
                for (var i = 0; i < 22; i++)
                {
                    var x = random.NextDouble();
                    var y = random.NextDouble();
                    var dx = (random.NextDouble() - 0.5) * 0.12;
                    var dy = (random.NextDouble() - 0.5) * 0.12;
                    cracks.Children.Add(new LineGeometry(new Point(x, y), new Point(x + dx, y + dy)));
                }
                Stroke(cracks, ink, 0.05 * strength, 0.0011 * Math.Max(0.7, scale));
                break;
            }
            case "Concrete":
            {
                var pits = new GeometryGroup();
                for (var i = 0; i < 340; i++)
                    pits.Children.Add(new EllipseGeometry(new Point(random.NextDouble(), random.NextDouble()),
                        0.0015 + random.NextDouble() * 0.0035, 0.0015 + random.NextDouble() * 0.0035));
                Fill(pits, ink, 0.11 * strength);
                var patches = new GeometryGroup();
                for (var i = 0; i < 14; i++)
                    patches.Children.Add(new EllipseGeometry(new Point(random.NextDouble(), random.NextDouble()),
                        (0.03 + random.NextDouble() * 0.08) * scale, (0.02 + random.NextDouble() * 0.06) * scale));
                Fill(patches, ink, 0.032 * strength);
                break;
            }
            case "Metal":
            case "Chrome":
            {
                // Brushed lines run with the light angle; chrome adds wide mirror bands.
                var brushed = new GeometryGroup();
                var count = kind == "Chrome" ? 44 : 110;
                for (var i = 0; i < count; i++)
                {
                    var y = random.NextDouble();
                    brushed.Children.Add(new LineGeometry(new Point(0, y), new Point(1, y + (random.NextDouble() - 0.5) * 0.006)));
                }
                Stroke(brushed, ink, (kind == "Chrome" ? 0.055 : 0.10) * strength,
                    (kind == "Chrome" ? 0.0022 : 0.0012) * Math.Max(0.7, scale));
                if (kind == "Chrome")
                {
                    var bands = new GeometryGroup();
                    for (var i = 0; i < 4; i++)
                    {
                        var y = -0.15 + i * 0.32;
                        bands.Children.Add(new LineGeometry(new Point(-0.15, y), new Point(1.15, y + 0.38)));
                    }
                    Stroke(bands, Colors.White, 0.11 * strength, 0.055);
                }
                break;
            }
            case "Glass":
            {
                var streaks = new GeometryGroup();
                for (var i = 0; i < 3; i++)
                {
                    var offset = -0.25 + i * 0.42;
                    streaks.Children.Add(new LineGeometry(new Point(offset, 0), new Point(offset + 0.7, 1)));
                }
                Stroke(streaks, Colors.White, 0.10 * strength, 0.05);
                var edge = new GeometryGroup();
                edge.Children.Add(new LineGeometry(new Point(0, 0.025), new Point(1, 0.025)));
                edge.Children.Add(new LineGeometry(new Point(0.018, 0), new Point(0.018, 1)));
                Stroke(edge, Colors.White, 0.18 * strength, 0.028);
                break;
            }
            case "Frost":
            {
                var haze = new GeometryGroup();
                for (var i = 0; i < 85; i++)
                    haze.Children.Add(new EllipseGeometry(new Point(random.NextDouble(), random.NextDouble()),
                        (0.006 + random.NextDouble() * 0.026) * scale, (0.006 + random.NextDouble() * 0.026) * scale));
                Fill(haze, Colors.White, 0.075 * strength);
                var sparkle = new GeometryGroup();
                for (var i = 0; i < 80; i++)
                    sparkle.Children.Add(new EllipseGeometry(new Point(random.NextDouble(), random.NextDouble()),
                        0.0012 + random.NextDouble() * 0.0018, 0.0012 + random.NextDouble() * 0.0018));
                Fill(sparkle, Colors.White, 0.20 * strength);
                break;
            }
            case "Hologram":
            {
                var hues = new[] { 0.0, 0.22, 0.44, 0.66, 0.88 };
                for (var i = 0; i < hues.Length; i++)
                {
                    var band = new GeometryGroup();
                    var y = -0.3 + i * 0.3;
                    band.Children.Add(new LineGeometry(new Point(-0.2, y), new Point(1.2, y + 0.5)));
                    Stroke(band, ColorOf(MotionColors.Hue(ToHex(surface), hues[i]), surface), 0.12 * strength, 0.075);
                }
                break;
            }
            default:
            {
                // Matte: a quiet edge darkening, so it reads as a surface rather than a flat fill.
                var edges = new GeometryGroup();
                edges.Children.Add(new RectangleGeometry(new Rect(0, 0, 1, 0.018)));
                edges.Children.Add(new RectangleGeometry(new Rect(0, 0.982, 1, 0.018)));
                Fill(edges, Colors.Black, 0.05 * strength);
                break;
            }
        }
    }

    /// <summary>Text keeps its shape, so its finish is folded into the gradient's stops.</summary>
    static Brush ApplyTextureToBrush(Brush brush, ScorebugTexture texture, Color flat)
    {
        if (brush is not GradientBrush gradient) return brush;
        var copy = gradient.Clone();
        copy.GradientStops.Clear();
        var baseHex = ToHex(flat);
        var textureColour = ColorOf(texture.Tint, flat);
        void Add(double offset, string colour, double amount)
        {
            var blended = MotionColors.Mix(baseHex, ToHex(textureColour), texture.TintAmount);
            var shifted = ColorOf(MotionColors.Shift(blended, amount), flat);
            copy.GradientStops.Add(new GradientStop(Color.FromArgb(shifted.A, shifted.R, shifted.G, shifted.B), Math.Clamp(offset, 0, 1)));
        }
        // Text can only carry the finish through its gradient stops, so the kind changes the
        // profile: polished faces get a narrow hard highlight, soft ones a broad tinted wash.
        var soft = texture.Kind is "Paper" or "Fabric" or "Velvet" or "Stone" or "Concrete" or "Frost";
        var polished = texture.Kind is "Metal" or "Chrome" or "Glass" or "Hologram";
        var highlight = polished ? 1.15 : soft ? 0.3 : 0.8;
        Add(0, baseHex, (soft ? 0.04 : 0.1) * texture.Ambience);
        Add(polished ? 0.24 : 0.18, baseHex, (texture.Shine + texture.Glossiness * 0.5) * highlight);
        Add(0.44, baseHex, -(texture.Roughness * (soft ? 0.34 : 0.55)));
        Add(0.58, baseHex, (texture.Satin + texture.Ambience) * (soft ? 0.55 : 0.4));
        Add(polished ? 0.72 : 0.8, baseHex, (texture.Metallic * 0.6 + texture.Shine * 0.4) * highlight);
        Add(1, baseHex, (soft ? 0.04 : 0.1) * texture.Ambience);
        if (texture.Iridescence > 0.01)
            foreach (var stop in copy.GradientStops.ToList())
                stop.Color = ColorOf(MotionColors.Hue(ToHex(stop.Color), texture.Iridescence * 0.3), stop.Color);
        copy.Opacity = Math.Clamp(texture.Opacity + (soft ? 0.32 : 0.45), 0, 1);
        copy.Freeze();
        return copy;
    }

    static Color Mix(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(a.A * (1 - amount) + b.A * amount),
            (byte)Math.Round(a.R * (1 - amount) + b.R * amount),
            (byte)Math.Round(a.G * (1 - amount) + b.G * amount),
            (byte)Math.Round(a.B * (1 - amount) + b.B * amount));
    }

    public static string ToHex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>The image for a still or logo, loaded without holding the file open.</summary>
    public static BitmapImage? TryImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }
}
