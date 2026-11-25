using System.Collections.Concurrent;
using System.IO;
using NAudio.Wave;

namespace Scoreboard.Desktop;

/// <summary>
/// Captures the Windows output mix once for the local channel headend and hands out 16-bit PCM.
/// A browser or VLC opens /audio/{id}.wav and hears what the operator's machine is playing, which
/// is the programme mix (clips, cameras and the venue's own sound). If no render device can be
/// opened, the feed stays a valid silent WAV instead of failing the video stream.
/// </summary>
public sealed class SystemAudioTap : IDisposable
{
    readonly ConcurrentQueue<byte[]> _chunks = new();
    readonly object _gate = new();
    WasapiLoopbackCapture? _capture;
    WaveFormat _format = new(48000, 16, 2);
    bool _convert;
    bool _running;
    int _clients;

    public int SampleRate => _format.SampleRate;
    public int Channels => _format.Channels;
    public int Clients => Volatile.Read(ref _clients);

    public void AddClient()
    {
        if (Interlocked.Increment(ref _clients) == 1) Start();
    }

    public void RemoveClient()
    {
        if (Interlocked.Decrement(ref _clients) <= 0)
        {
            Interlocked.Exchange(ref _clients, 0);
            Stop();
        }
    }

    void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            try
            {
                _capture = new WasapiLoopbackCapture();
                _format = _capture.WaveFormat;
                _convert = _format.Encoding == WaveFormatEncoding.IeeeFloat && _format.BitsPerSample == 32;
                _capture.DataAvailable += OnData;
                _capture.StartRecording();
            }
            catch
            {
                // No output device, or it is held exclusively. The WAV endpoint remains valid and
                // silent, so a player still gets a complete audio track beside the picture.
                _capture = null;
                _format = new WaveFormat(48000, 16, 2);
                _convert = false;
            }
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        byte[] data;
        if (_convert)
        {
            var samples = e.BytesRecorded / 4;
            data = new byte[samples * 2];
            for (var index = 0; index < samples; index++)
            {
                var value = Math.Clamp(BitConverter.ToSingle(e.Buffer, index * 4), -1f, 1f);
                var sample = (short)Math.Round(value * short.MaxValue);
                data[index * 2] = (byte)(sample & 0xFF);
                data[index * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }
        else
        {
            // A 16-bit capture can be queued as-is. Other depths are rare for WASAPI loopback;
            // treat them as silence rather than sending malformed samples.
            if (_format.BitsPerSample != 16) return;
            data = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, data, e.BytesRecorded);
        }
        _chunks.Enqueue(data);
        while (_chunks.Count > 80) _chunks.TryDequeue(out _);
    }

    /// <summary>Reads the next chunk, or a correctly sized silence buffer while nothing is playing.</summary>
    public async Task<byte[]> ReadAsync(CancellationToken token)
    {
        // One 20 ms wait keeps an empty/silent feed real-time. Waiting five times before returning
        // 20 ms of silence made the WAV stream underrun whenever no capture device was present.
        if (_chunks.TryDequeue(out var chunk) && chunk.Length > 0) return chunk;
        await Task.Delay(20, token).ConfigureAwait(false);
        if (_chunks.TryDequeue(out chunk) && chunk.Length > 0) return chunk;
        return new byte[Math.Max(2, SampleRate / 50 * Math.Max(1, Channels) * 2)];
    }

    public byte[] WavHeader()
    {
        var channels = Math.Max(1, Channels);
        var rate = Math.Max(8000, SampleRate);
        var blockAlign = channels * 2;
        using var memory = new MemoryStream(44);
        using var writer = new BinaryWriter(memory);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(0x7FFFFFFF);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * blockAlign);
        writer.Write((short)blockAlign);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(0x7FFFFFFF);
        return memory.ToArray();
    }

    void Stop()
    {
        lock (_gate)
        {
            _running = false;
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnData;
                try { _capture.StopRecording(); } catch { }
                try { _capture.Dispose(); } catch { }
                _capture = null;
            }
            while (_chunks.TryDequeue(out _)) { }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _running = false; }
        Stop();
    }
}
