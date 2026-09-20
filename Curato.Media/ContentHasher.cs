using System.IO.Hashing;

namespace Curato.Media;

/// <summary>
/// PLAN.md §7 — the expensive, exact-confirmation tier of duplicate
/// detection: a full-file hash, only ever computed for files that already
/// collide on the cheap (FileSizeBytes, QuickHash) tier, to give a yes/no
/// confirmation before anything is ever offered as "delete this duplicate."
/// </summary>
public static class ContentHasher
{
    private const int BufferSize = 1024 * 1024;

    public static string ComputeContentHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hasher = new XxHash128();

        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.Append(buffer.AsSpan(0, read));
        }

        return Convert.ToHexStringLower(hasher.GetCurrentHash());
    }
}
