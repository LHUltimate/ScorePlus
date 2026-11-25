using System.Diagnostics;
using System.IO;

namespace Scoreboard.Desktop;

/// <summary>
/// Sends the local programme stream and its system-audio mix to a third-party RTMP provider with
/// ffmpeg. The app does not bundle an encoder, so this starts only when ffmpeg.exe is beside the
/// app or on PATH. The audio input is the headend's live WAV endpoint, which is the same mix Live
/// Channel exposes; a silent stereo track is used only when that feed is unavailable.
/// </summary>
public sealed class StreamPushService : IDisposable
{
    Process? _process;
    string _lastMessage = "";

    public bool IsRunning => _process is { HasExited: false };
    public string LastMessage => _lastMessage;
    public string? Error { get; private set; }

    public static string? FindFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("SCOREPLUS_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside)) return beside;
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (File.Exists(local)) return local;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(folder.Trim(), "ffmpeg");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    public bool Start(string localStreamUrl, string localAudioUrl, string serverUrl, string streamKey)
    {
        Stop();
        Error = null;
        _lastMessage = "";
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Error = "ffmpeg was not found. Put ffmpeg.exe beside ScorePlus or on PATH, then press Push again.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(streamKey))
        {
            Error = "Enter the provider ingest URL and stream key first.";
            return false;
        }
        var target = serverUrl.Trim().TrimEnd('/') + "/" + streamKey.Trim().TrimStart('/');
        var audioInput = string.IsNullOrWhiteSpace(localAudioUrl)
            ? "-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000 "
            : $"-thread_queue_size 512 -f wav -i \"{localAudioUrl}\" ";
        // Map one video and one audio stream explicitly. The audio comes from the same programme
        // tap as Live Channel, so the provider hears the clips, cameras and venue mix, not silence.
        var args = $"-hide_banner -loglevel warning -thread_queue_size 512 -re -f mjpeg -i \"{localStreamUrl}\" "
            + audioInput
            + "-map 0:v:0 -map 1:a:0 -c:v libx264 -preset veryfast -tune zerolatency -pix_fmt yuv420p -r 30 -g 60 "
            + "-c:a aac -b:a 128k -ar 48000 -shortest -f flv \"" + target.Replace("\"", "") + "\"";
        try
        {
            var info = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                _lastMessage = e.Data.Trim();
            };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) _lastMessage = e.Data.Trim();
            };
            process.Exited += (_, _) =>
            {
                if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(Error))
                    Error = $"Provider push stopped (ffmpeg exit {process.ExitCode}). {_lastMessage}".Trim();
            };
            if (!process.Start())
            {
                Error = "ffmpeg did not start.";
                return false;
            }
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            _process = process;
            _lastMessage = $"Pushing to {ServerLabel(serverUrl)}";
            return true;
        }
        catch (Exception ex)
        {
            Error = "Provider push failed: " + ex.Message;
            return false;
        }
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
        try { process.Dispose(); } catch { }
    }

    static string ServerLabel(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }

    public void Dispose() => Stop();
}
