using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Scoreboard.Desktop;

/// <summary>
/// Sends a clip's sound to a chosen speaker. WPF's MediaElement can only use the Windows default
/// output, so routing to a second sound card is done here with WASAPI: the clip's MediaElement is
/// muted and the same file is played through this router on the device the operator picked.
/// A clip left on "Default output" keeps using the MediaElement, so nothing changes for the
/// common case and formats WASAPI cannot decode still play normally.
/// </summary>
public sealed class AudioRouter : IDisposable
{
    public sealed record OutputDevice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    IWavePlayer? _output;
    AudioFileReader? _reader;
    LevelReader? _meter;
    float _volume = 1;

    /// <summary>
    /// This stream's own level, 0–1, measured from the samples it is sending. A speaker bus can
    /// read its device's loopback, but a clip must show only its own sound — reading the device
    /// mixed every clip on that speaker into one bar.
    /// </summary>
    public double Level => IsPlaying && _meter is { } meter ? Math.Clamp(meter.Level, 0, 1) : 0;

    /// <summary>Measures the peak of the samples it passes through, with a short fall-off so a
    /// bar reads the music instead of flickering on every frame.</summary>
    sealed class LevelReader(ISampleProvider source) : ISampleProvider
    {
        float _peak;

        public WaveFormat WaveFormat => source.WaveFormat;

        public float Level => Volatile.Read(ref _peak);

        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(new Span<float>(buffer, offset, count));
            Measure(new ReadOnlySpan<float>(buffer, offset, read));
            return read;
        }

        public int Read(Span<float> buffer)
        {
            var read = source.Read(buffer);
            Measure(buffer[..read]);
            return read;
        }

        void Measure(ReadOnlySpan<float> samples)
        {
            var peak = 0f;
            foreach (var sample in samples)
            {
                var value = Math.Abs(sample);
                if (value > peak) peak = value;
            }
            // Fall away gradually, so a bar between beats sinks instead of snapping to zero.
            Volatile.Write(ref _peak, Math.Max(peak, Volatile.Read(ref _peak) * 0.86f));
        }
    }

    /// <summary>Every active playback device, in Windows' own order.</summary>
    public static IReadOnlyList<OutputDevice> Devices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(device => new OutputDevice(device.ID, device.FriendlyName))
                .ToArray();
        }
        catch { return []; }
    }

    public static string? DefaultDeviceName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName;
        }
        catch { return null; }
    }

    public bool IsPlaying { get; private set; }

    /// <summary>Why the last routed clip could not be opened, if it could not.</summary>
    public string? LastError { get; private set; }

    public void Play(string path, string? deviceId, double volume, double startSeconds = 0)
    {
        Stop();
        LastError = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        MMDeviceEnumerator? enumerator = null;
        try
        {
            var reader = new AudioFileReader(path) { Volume = _volume = (float)Math.Clamp(volume, 0, 1) };
            if (startSeconds > 0.05)
            {
                var target = TimeSpan.FromSeconds(startSeconds);
                if (target < reader.TotalTime) reader.CurrentTime = target;
            }
            enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                device = Resolve(enumerator, deviceId);
                // A speaker the operator picked that is not connected must not quietly fall back to
                // the default output: that made a switched-off speaker look like it was playing.
                if (device is null) throw new InvalidOperationException("that speaker is not connected");
            }
            var output = device is null
                ? new WasapiOut(AudioClientShareMode.Shared, true, 100)
                : new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            var metered = new LevelReader(reader);
            output.Init(new SampleToWaveProvider(metered));
            output.Play();
            _reader = reader;
            _meter = metered;
            _output = output;
            IsPlaying = true;
        }
        catch (Exception ex)
        {
            // A codec WASAPI cannot read, or a device that has gone away, leaves this clip silent.
            LastError = $"{Path.GetFileName(path)} on {(deviceId ?? "the default speaker")}: {ex.Message}";
            Stop();
        }
        finally
        {
            try { enumerator?.Dispose(); } catch { }
        }
    }

    static MMDevice? Resolve(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        try
        {
            var match = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => string.Equals(d.ID, deviceId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(d.FriendlyName, deviceId, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            // Ask the enumerator for a fresh handle on the same endpoint; a device object borrowed
            // from a disposed collection is not reliable for opening a stream.
            try { return enumerator.GetDevice(match.ID) ?? match; } catch { return match; }
        }
        catch { return null; }
    }

    public void SetVolume(double volume)
    {
        _volume = (float)Math.Clamp(volume, 0, 1);
        if (_reader is not null) _reader.Volume = _volume;
    }

    public void Pause()
    {
        try { _output?.Pause(); } catch { }
        IsPlaying = false;
    }

    public void Resume()
    {
        try { _output?.Play(); } catch { }
        IsPlaying = _output is not null;
    }

    public void Seek(double seconds)
    {
        if (_reader is null) return;
        try
        {
            var target = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (target < _reader.TotalTime) _reader.CurrentTime = target;
        }
        catch { }
    }

    public double Position
    {
        get
        {
            try { return _reader?.CurrentTime.TotalSeconds ?? 0; } catch { return 0; }
        }
    }

    public void Stop()
    {
        IsPlaying = false;
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _output = null;
        _reader = null;
        _meter = null;
    }

    public void Dispose() => Stop();
}
