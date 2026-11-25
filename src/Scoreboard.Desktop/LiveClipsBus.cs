using System.Windows.Media;

namespace Scoreboard.Desktop;

/// <summary>
/// The four pictures the Show page monitors: the two clip-server fills, the live programme and the
/// staged preview. Live Clips owns the fills, the macro board owns preview and live, and this shared
/// bus is how the two windows agree without either one owning the other.
/// </summary>
public sealed class LiveClipsBus
{
    public static LiveClipsBus Shared { get; } = new();

    public event Action? Changed;

    public string FillALabel { get; private set; } = "No clip loaded";
    public string FillBLabel { get; private set; } = "No clip loaded";
    public string LiveLabel { get; private set; } = "Layout";
    public string PreviewLabel { get; private set; } = "Nothing staged";
    public ImageSource? FillAPicture { get; private set; }
    public ImageSource? FillBPicture { get; private set; }
    public ImageSource? LivePicture { get; private set; }
    public ImageSource? PreviewPicture { get; private set; }

    public void SetChannel(int channel, string label, ImageSource? picture)
    {
        if (channel == 0)
        {
            FillALabel = string.IsNullOrWhiteSpace(label) ? "No clip loaded" : label;
            FillAPicture = picture;
        }
        else
        {
            FillBLabel = string.IsNullOrWhiteSpace(label) ? "No clip loaded" : label;
            FillBPicture = picture;
        }
        Changed?.Invoke();
    }

    public void SetLive(string label, ImageSource? picture)
    {
        LiveLabel = string.IsNullOrWhiteSpace(label) ? "Layout" : label;
        LivePicture = picture;
        Changed?.Invoke();
    }

    public void SetPreview(string label, ImageSource? picture)
    {
        PreviewLabel = string.IsNullOrWhiteSpace(label) ? "Nothing staged" : label;
        PreviewPicture = picture;
        Changed?.Invoke();
    }
}
