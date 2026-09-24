using System.Diagnostics;

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
/// </summary>
public static class VideoThumbnailGenerator
{
    public static byte[] GenerateJpeg(string filePath, int maxDimension)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
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
}
