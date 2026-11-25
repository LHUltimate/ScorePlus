using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Scoreboard.Desktop;

/// <summary>
/// One clip-server channel inside the Vision page. It is a real player with its own picture,
/// transport and level, so channel A and channel B can run independent clips. The picture is a
/// VideoDrawing, which keeps the frame visible to WPF's compositor and lets the same visual move
/// from a preview host to the programme host without reopening the decoder.
/// </summary>
public sealed class ClipChannelPlayer : IDisposable
{
    readonly string _channelName;
    readonly MediaPlayer _player = new() { ScrubbingEnabled = true };
    readonly VideoDrawing _drawing;
    readonly DrawingBrush _brush;
    readonly Rectangle _picture;
    readonly Grid _visual = new() { Background = Brushes.Black, ClipToBounds = true };
    readonly Border _namePlate;
    readonly TextBlock _nameText;
    readonly TextBlock _timeText;
    readonly TextBlock _placeholder;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(180) };
    MediaEntry? _entry;
    bool _muted;
    bool _normalize;
    bool _disposed;
    double _volume = 1;

    public ClipChannelPlayer(string channelName)
    {
        _channelName = string.IsNullOrWhiteSpace(channelName) ? "CLIP" : channelName.Trim().ToUpperInvariant();
        _drawing = new VideoDrawing { Player = _player, Rect = new Rect(0, 0, 1, 1) };
        _brush = new DrawingBrush(_drawing)
        {
            Stretch = Stretch.Uniform,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center
        };
        _picture = new Rectangle { Fill = _brush, Stretch = Stretch.Fill, Visibility = Visibility.Hidden };
        _visual.Children.Add(_picture);

        _placeholder = new TextBlock
        {
            Text = $"{_channelName} · NO CLIP",
            Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x7D, 0x90)),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _visual.Children.Add(_placeholder);

        _nameText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _timeText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x66)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var plate = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 0, 8, 0) };
        DockPanel.SetDock(_timeText, Dock.Right);
        plate.Children.Add(_timeText);
        plate.Children.Add(_nameText);
        _namePlate = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0, 0, 0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(7),
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = plate,
            Visibility = Visibility.Collapsed
        };
        _visual.Children.Add(_namePlate);

        _player.MediaOpened += (_, _) => { _picture.Visibility = Visibility.Visible; _placeholder.Visibility = Visibility.Collapsed; UpdateTime(); Changed?.Invoke(); };
        _player.MediaEnded += (_, _) => { _player.Position = TimeSpan.Zero; _player.Pause(); Changed?.Invoke(); };
        _player.MediaFailed += (_, e) => { Error = e.ErrorException?.Message ?? "the clip could not be decoded"; _player.Close(); _picture.Visibility = Visibility.Hidden; _placeholder.Visibility = Visibility.Visible; Changed?.Invoke(); };
        _tick.Tick += (_, _) => UpdateTime();
        _tick.Start();
    }

    public Grid Visual => _visual;
    public string ChannelName => _channelName;
    public MediaEntry? Entry => _entry;
    public string? Error { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsMuted => _muted;
    public double Volume => _volume;
    public double Position { get { try { return Math.Max(0, _player.Position.TotalSeconds); } catch { return 0; } } }
    public double Duration { get { try { return _player.NaturalDuration.HasTimeSpan ? Math.Max(0, _player.NaturalDuration.TimeSpan.TotalSeconds) : 0; } catch { return 0; } } }
    public string TimeLabel
    {
        get
        {
            var total = Duration;
            var position = Position;
            string Stamp(double seconds) => $"{(int)seconds / 60:0}:{(int)seconds % 60:00}";
            return total > 0 ? $"{Stamp(position)} / {Stamp(total)}" : Stamp(position);
        }
    }

    public event Action? Changed;

    public void Open(MediaEntry? entry, bool play = true)
    {
        Error = null;
        _entry = entry;
        _player.Stop();
        _player.Close();
        _picture.Visibility = Visibility.Hidden;
        _placeholder.Visibility = Visibility.Visible;
        _namePlate.Visibility = Visibility.Collapsed;
        if (entry is null)
        {
            IsPlaying = false;
            _placeholder.Text = $"{_channelName} · NO CLIP";
            Changed?.Invoke();
            return;
        }
        _placeholder.Text = $"{_channelName} · LOADING";
        _nameText.Text = $"{_channelName} · {entry.Label}";
        _namePlate.Visibility = Visibility.Visible;
        FitName();
        try
        {
            _player.Open(new Uri(entry.Path));
            _player.Volume = _muted ? 0 : Math.Clamp(_volume * (_normalize ? 0.89 : 1), 0, 1);
            if (play) _player.Play(); else _player.Pause();
            IsPlaying = play;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _placeholder.Text = $"{_channelName} · ERROR";
        }
        Changed?.Invoke();
    }

    public void TogglePlay()
    {
        if (_entry is null) return;
        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
        }
        else
        {
            _player.Play();
            IsPlaying = true;
        }
        Changed?.Invoke();
    }

    public void Play()
    {
        if (_entry is null) return;
        _player.Play();
        IsPlaying = true;
        Changed?.Invoke();
    }

    public void Pause()
    {
        _player.Pause();
        IsPlaying = false;
        Changed?.Invoke();
    }

    public void Stop()
    {
        IsPlaying = false;
        _player.Stop();
        _player.Position = TimeSpan.Zero;
        Changed?.Invoke();
    }

    public void Seek(double seconds)
    {
        if (_entry is null) return;
        var target = Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, Math.Max(0, Duration));
        try { _player.Position = TimeSpan.FromSeconds(target); } catch { }
        Changed?.Invoke();
    }

    public void SetVolume(double volume)
    {
        _volume = Math.Clamp(double.IsFinite(volume) ? volume : 1, 0, 1);
        _player.Volume = _muted ? 0 : Math.Clamp(_volume * (_normalize ? 0.89 : 1), 0, 1);
        Changed?.Invoke();
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        _player.Volume = muted ? 0 : Math.Clamp(_volume * (_normalize ? 0.89 : 1), 0, 1);
        Changed?.Invoke();
    }

    public void SetNormalize(bool normalize)
    {
        _normalize = normalize;
        _player.Volume = _muted ? 0 : Math.Clamp(_volume * (_normalize ? 0.89 : 1), 0, 1);
        Changed?.Invoke();
    }

    void UpdateTime()
    {
        _timeText.Text = TimeLabel;
        _namePlate.Visibility = _entry is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void FitName()
    {
        // Shrink long names instead of letting the bottom plate grow over the picture. The plate
        // still truncates at the edge, with the full name in the tooltip.
        _nameText.FontSize = 17;
        _nameText.ToolTip = _nameText.Text;
        var available = Math.Max(80, _visual.ActualWidth - 120);
        var desired = _nameText.Text.Length * 8.4;
        if (desired > available) _nameText.FontSize = Math.Max(10, 17 * available / desired);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tick.Stop();
        try { _player.Stop(); _player.Close(); } catch { }
    }
}
