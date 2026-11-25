using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>One rendered broadcast frame: programme JPEG plus the alpha-aware fill and key PNGs.</summary>
public sealed record BroadcastRenderResult(
    byte[] Jpeg,
    byte[] FillPng,
    byte[] KeyPng,
    int Width,
    int Height,
    double Milliseconds);

/// <summary>
/// The one alpha-aware render path for Stage 6. It uses the same <see cref="ScorebugCanvasView"/>
/// as the audience output, so a router fill/key, a stream overlay and a venue screen cannot drift
/// into three different graphics. The overlay is rendered with a transparent backdrop, the matte is
/// lifted from its alpha channel, and the fill is un-premultiplied for an external keyer.
/// </summary>
public static class BroadcastRenderer
{
    public static BroadcastRenderResult Render(
        ScorebugCatalog catalog,
        GameEngine engine,
        MediaPlayback? playback,
        ScorebugLayout layout,
        int width,
        int height,
        ImageSource? background = null,
        bool includeScorebug = true)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
            throw new InvalidOperationException("Broadcast rendering must run on the UI thread.");
        width = Math.Clamp(width, 320, 3840);
        height = Math.Clamp(height, 240, 2160);
        var clock = Stopwatch.StartNew();

        BitmapSource? overlayBitmap = null;
        ScorebugCanvasView? overlay = null;
        if (includeScorebug)
        {
            overlay = new ScorebugCanvasView(catalog, engine,
                stateProvider: () => engine.State,
                playback: playback,
                layoutProvider: () => layout,
                liveValues: true,
                fillBox: true,
                transparentBackdrop: true)
            {
                Width = width,
                Height = height
            };
            overlay.ShowAllLayers();
            overlay.Measure(new Size(width, height));
            overlay.Arrange(new Rect(0, 0, width, height));
            overlay.UpdateLayout();
            overlayBitmap = Snapshot(overlay, width, height);
            overlay.Dispose();
        }

        var (fill, key) = overlayBitmap is null
            ? (TransparentPng(width, height), BlackKeyPng(width, height))
            : LiftAlpha(overlayBitmap);

        var programme = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            if (background is not null)
                context.DrawImage(background, new Rect(0, 0, width, height));
            else
                context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            if (overlayBitmap is not null)
                context.DrawImage(overlayBitmap, new Rect(0, 0, width, height));
        }
        programme.Render(visual);
        programme.Freeze();

        var jpeg = EncodeJpeg(programme);
        return new BroadcastRenderResult(jpeg, fill, key, width, height, clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>A frame at the standard requested by the owning display's broadcast channel.</summary>
    public static BroadcastRenderResult Render(
        ScorebugCatalog catalog,
        GameEngine engine,
        MediaPlayback? playback,
        ScorebugLayout layout,
        ImageSource? background = null,
        bool includeScorebug = true)
    {
        var display = catalog.FindDisplayOfLayout(layout.Id);
        var profile = display?.Broadcast ?? new BroadcastProfile();
        var standard = profile.Raster;
        return Render(catalog, engine, playback, layout, standard.Width, standard.Height, background, includeScorebug);
    }

    static BitmapSource Snapshot(FrameworkElement element, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Produces external fill and key planes. The key is white where the graphic is opaque and black
    /// where it is transparent; the fill uses straight (not premultiplied) colour so a keyer can
    /// multiply it by the matte without making semi-transparent edges too dark.
    /// </summary>
    static (byte[] Fill, byte[] Key) LiftAlpha(BitmapSource source)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);
        var fill = new byte[pixels.Length];
        var key = new byte[pixels.Length];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var alpha = pixels[offset + 3];
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            if (alpha > 0)
            {
                // Pbgra32 is premultiplied; 255/alpha turns it back into a straight fill colour.
                blue = (byte)Math.Min(255, blue * 255 / alpha);
                green = (byte)Math.Min(255, green * 255 / alpha);
                red = (byte)Math.Min(255, red * 255 / alpha);
            }
            fill[offset] = blue;
            fill[offset + 1] = green;
            fill[offset + 2] = red;
            fill[offset + 3] = 255;

            key[offset] = alpha;
            key[offset + 1] = alpha;
            key[offset + 2] = alpha;
            key[offset + 3] = 255;
        }
        return (EncodePng(fill, width, height), EncodePng(key, width, height));
    }

    static byte[] TransparentPng(int width, int height) =>
        EncodePng(new byte[width * height * 4], width, height);

    static byte[] BlackKeyPng(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
        return EncodePng(pixels, width, height);
    }

    static byte[] EncodePng(byte[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static byte[] EncodeJpeg(BitmapSource source)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
