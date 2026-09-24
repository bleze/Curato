using System.Diagnostics;
using Xabe.FFmpeg.Downloader;

namespace Curato.Media;

/// <summary>
/// PLAN.md §11 — a video's grid/viewer "thumbnail" was never anything but a
/// static "Video" placeholder (confirmed live: the frontend didn't even
/// attempt to load one, since nothing here could generate one). Shells out
/// to `ffmpeg` (already present in the Docker image for exactly this, per
/// its own install comment) rather than pulling in a native decoding
/// library — same reasoning as icloudpd/pyicloud_ipd (Curato.iCloud): a
/// mature external tool already does this well, no reason to reimplement
/// video demuxing/decoding in-process.
///
/// Seeks ~0.5s into the clip before grabbing a single frame — avoids the
/// common all-black/fade-in first frame at t=0 — using *input* seeking
/// (`-ss` before `-i`), which jumps to the nearest keyframe instead of
/// decoding frame-by-frame from the start; approximate positioning is fine
/// for a thumbnail. A clip shorter than that just yields no output, which
/// the caller (MediaController) already treats as "no preview" — the same
/// fallback an undecodable photo format gets.
///
/// Confirmed live this is a real gap for local dev, not just a hypothetical:
/// nothing installs `ffmpeg` on a Windows dev machine's PATH the way the
/// Docker image's `apt-get install ffmpeg` does, so every video thumbnail
/// request there fails outright. <see cref="EnsureFfmpegAvailable"/> only
/// ever engages that fallback — if `ffmpeg` already resolves (true in every
/// Docker deployment), it's used directly, no download, no extra runtime
/// internet dependency for a deployment that already works fine offline.
/// </summary>
public static class VideoThumbnailGenerator
{
    private static readonly SemaphoreSlim EnsureAvailableLock = new(1, 1);
    private static string? _resolvedExecutablePath;

    public static byte[] GenerateJpeg(string filePath, int maxDimension)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = EnsureFfmpegAvailable(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
                 {
                     "-y", "-loglevel", "error",
                     "-ss", "00:00:00.5",
                     "-i", filePath,
                     "-frames:v", "1",
                     "-vf", $"scale='min({maxDimension},iw)':'min({maxDimension},ih)':force_original_aspect_ratio=decrease",
                     "-f", "image2pipe",
                     "-vcodec", "mjpeg",
                     "pipe:1",
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");

        // Both the frame bytes (stdout) and any diagnostic text (stderr) are
        // drained concurrently — reading either stream synchronously to
        // completion one at a time risks a classic pipe deadlock if the
        // other stream's OS buffer fills up while nothing is reading it.
        var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream);
        var stderrTask = process.StandardError.ReadToEndAsync();

        var jpegBytes = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();

        if (process.ExitCode != 0 || jpegBytes.Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg failed to extract a thumbnail frame from '{filePath}': {stderr}");
        }

        return jpegBytes;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream source)
    {
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// Returns "ffmpeg" as-is when it's already resolvable on PATH (every
    /// Docker deployment); otherwise downloads a portable build once into a
    /// shared cache folder and returns that path instead. Cached in-memory
    /// after the first call so a long-running server checks PATH/downloads
    /// at most once per process, not per thumbnail request; the lock only
    /// matters for the (harmless either way) race of two concurrent first
    /// requests both triggering a download simultaneously.
    /// </summary>
    private static string EnsureFfmpegAvailable()
    {
        if (_resolvedExecutablePath is not null)
        {
            return _resolvedExecutablePath;
        }

        EnsureAvailableLock.Wait();
        try
        {
            if (_resolvedExecutablePath is not null)
            {
                return _resolvedExecutablePath;
            }

            if (IsExecutableOnPath("ffmpeg"))
            {
                _resolvedExecutablePath = "ffmpeg";
                return _resolvedExecutablePath;
            }

            var cacheDir = Path.Combine(Path.GetTempPath(), "curato-ffmpeg-cache");
            var cachedExecutable = Path.Combine(cacheDir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (!File.Exists(cachedExecutable))
            {
                Directory.CreateDirectory(cacheDir);
                FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, cacheDir).GetAwaiter().GetResult();
            }

            _resolvedExecutablePath = cachedExecutable;
            return _resolvedExecutablePath;
        }
        finally
        {
            EnsureAvailableLock.Release();
        }
    }

    private static bool IsExecutableOnPath(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            return process is not null && process.WaitForExit(3000) && process.ExitCode == 0;
        }
        catch (Exception) when (!Debugger.IsAttached)
        {
            return false;
        }
    }
}
