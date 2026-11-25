using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

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

    /// <summary>
    /// Finds ffmpeg wherever an operator is likely to have put it: an explicit preference, the
    /// environment, beside the app, the usual package-manager folders, the App Paths registration
    /// and PATH. Stream Studio also offers a file picker, so a machine that keeps it in a custom
    /// folder never has to edit PATH.
    /// </summary>
    public static string? FindFfmpeg(string? configured = null)
    {
        foreach (var candidate in Candidates(configured))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate.Trim().Trim('"')))
                    return candidate.Trim().Trim('"');
            }
            catch { }
        }
        return null;
    }

    static IEnumerable<string> Candidates(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        var environment = Environment.GetEnvironmentVariable("SCOREPLUS_FFMPEG");
        if (!string.IsNullOrWhiteSpace(environment)) yield return environment;

        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "ffmpeg.exe");
        yield return Path.Combine(baseDir, "ffmpeg");
        yield return Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe");
        yield return Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe");
        yield return Path.Combine(baseDir, "tools", "ffmpeg.exe");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        yield return Path.Combine(profile, "scoop", "shims", "ffmpeg.exe");
        yield return Path.Combine(common, "chocolatey", "bin", "ffmpeg.exe");
        yield return Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe");
        yield return Path.Combine(programFilesX86, "ffmpeg", "bin", "ffmpeg.exe");
        yield return @"C:\ffmpeg\bin\ffmpeg.exe";

        foreach (var key in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\ffmpeg.exe",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\ffmpeg.exe"
        })
        {
            string? registered = null;
            try
            {
                using var registry = Registry.LocalMachine.OpenSubKey(key);
                registered = registry?.GetValue(null) as string;
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(registered)) yield return registered;
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            yield return Path.Combine(folder.Trim().Trim('"'), "ffmpeg.exe");
            yield return Path.Combine(folder.Trim().Trim('"'), "ffmpeg");
        }
    }

    public bool Start(string localStreamUrl, string localAudioUrl, string serverUrl, string streamKey,
        string? ffmpegPath = null)
    {
        Stop();
        Error = null;
        _lastMessage = "";
        var ffmpeg = FindFfmpeg(ffmpegPath);
        if (ffmpeg is null)
        {
            Error = "ffmpeg was not found. Install it, or use Locate ffmpeg in Stream Studio to point at ffmpeg.exe.";
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
        var args = $"-hide_banner -nostdin -loglevel warning -thread_queue_size 1024 -re -f mjpeg -i \"{localStreamUrl}\" "
            + audioInput
            + "-map 0:v:0 -map 1:a:0 -c:v libx264 -preset veryfast -tune zerolatency -pix_fmt yuv420p -r 30 -g 60 "
            + "-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" "
            + "-c:a aac -b:a 128k -ar 48000 -max_muxing_queue_size 1024 -shortest -f flv \""
            + target.Replace("\"", "") + "\"";
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
