using System.IO.Hashing;

namespace Curato.Media;

/// <summary>
/// PLAN.md §7 — the cheap tier of the two-tier duplicate-detection hash.
/// Hashes the file size plus sampled byte ranges (start/middle/end, or the
/// whole file when it's smaller than the sample size) instead of reading
/// every byte, so it costs O(1) disk seeks regardless of file size — the
/// whole point being that a multi-gigabyte video is no more expensive to
/// hash than a small photo.
/// </summary>
public static class QuickHasher
{
    private const int SampleSize = 64 * 1024;

    public static string ComputeQuickHash(string filePath, long fileSizeBytes)
    {
        using var stream = File.OpenRead(filePath);
        var hasher = new XxHash128();

        Span<byte> sizeBytes = stackalloc byte[sizeof(long)];
        BitConverter.TryWriteBytes(sizeBytes, fileSizeBytes);
        hasher.Append(sizeBytes);

        if (fileSizeBytes <= SampleSize * 3L)
        {
            HashRange(hasher, stream, 0, fileSizeBytes);
        }
        else
        {
            HashRange(hasher, stream, 0, SampleSize);
            HashRange(hasher, stream, (fileSizeBytes - SampleSize) / 2, SampleSize);
            HashRange(hasher, stream, fileSizeBytes - SampleSize, SampleSize);
        }

        return Convert.ToHexStringLower(hasher.GetCurrentHash());
    }

    private static void HashRange(XxHash128 hasher, FileStream stream, long offset, long length)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(length, int.MaxValue)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        hasher.Append(buffer.AsSpan(0, read));
    }
}
