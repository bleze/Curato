using Curato.Data;
using Curato.Media;
using Curato.Organizer;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// PLAN.md §4a — the browser-upload MVP for getting new files into a library
/// that only the server has filesystem access to. Received files are written
/// as loose files at the library root (collision-safe names), then run
/// through the exact same <see cref="ScanService"/> pipeline a folder-scan
/// uses — EXIF read, cluster, persist - so an upload behaves exactly like
/// the server finding the same files on disk itself.
///
/// Path-traversal note (§15): only the bare file name is ever taken from an
/// uploaded file's own (client-supplied) name - any directory component is
/// stripped before it touches the filesystem.
///
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api/upload")]
public sealed class UploadController : ControllerBase
{
    // Generous enough for RAW photos and short 4K clips without opening the
    // door to arbitrary-size uploads exhausting server disk space unchecked.
    private const long MaxFileSizeBytes = 2L * 1024 * 1024 * 1024;

    public sealed record UploadResponse(int FilesSaved, int FilesSkipped, ScanResult Scan);

    [HttpPost]
    [RequestSizeLimit(20L * 1024 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 20L * 1024 * 1024 * 1024)]
    public async Task<ActionResult<UploadResponse>> Upload(
        [FromForm] string path, [FromForm] List<IFormFile> files)
    {
        var libraryRoot = Path.GetFullPath(path);
        if (!Directory.Exists(libraryRoot))
        {
            return NotFound($"Folder not found: {path}");
        }

        if (files.Count == 0)
        {
            return BadRequest("No files received.");
        }

        var filesSaved = 0;
        var filesSkipped = 0;
        var claimedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file.FileName);
            var extension = Path.GetExtension(fileName);

            if (string.IsNullOrEmpty(fileName) ||
                !MediaFileScanner.IsSupportedExtension(extension) ||
                file.Length == 0 ||
                file.Length > MaxFileSizeBytes)
            {
                filesSkipped++;
                continue;
            }

            var destFileName = ResolveCollisionFreeName(libraryRoot, fileName, claimedNames);
            claimedNames.Add(destFileName);

            var destFullPath = Path.Combine(libraryRoot, destFileName);
            await using (var stream = System.IO.File.Create(destFullPath))
            {
                await file.CopyToAsync(stream);
            }

            filesSaved++;
        }

        var database = new LibraryDatabase(libraryRoot);
        database.EnsureSchema();
        var scanService = new ScanService(new LibraryRepository(database), new LibraryQueryService(database));
        var scanResult = scanService.Scan(libraryRoot);

        return Ok(new UploadResponse(filesSaved, filesSkipped, scanResult));
    }

    private static string ResolveCollisionFreeName(string libraryRoot, string fileName, HashSet<string> claimedNames)
    {
        if (!claimedNames.Contains(fileName) && !System.IO.File.Exists(Path.Combine(libraryRoot, fileName)))
        {
            return fileName;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;
        string candidate;
        do
        {
            candidate = $"{nameWithoutExtension} ({counter}){extension}";
            counter++;
        } while (claimedNames.Contains(candidate) || System.IO.File.Exists(Path.Combine(libraryRoot, candidate)));

        return candidate;
    }
}
