using System.Security.Cryptography;
using System.Text;

namespace PhoneFiles.Core.Archive;

/// <summary>
/// Read-only recursive copier. All identity flows through opaque node IDs; the
/// relative path is built from display names only for the PC destination. No
/// device-side path is ever constructed or assumed.
/// </summary>
public sealed class ArchiveService
{
    private readonly ITransport _transport;

    public ArchiveService(ITransport transport) => _transport = transport;

    /// <summary>
    /// Copy the selected directory trees into <paramref name="destinationRoot"/>.
    /// Emits progress, honours cancellation, hashes every file with SHA-256, and
    /// writes a UTF-8 清单.csv (inventory) with relative path, size, mtime, sha256.
    /// </summary>
    public async Task<ArchiveResult> ArchiveAsync(
        IReadOnlyList<ArchiveSelection> selections,
        string destinationRoot,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        Directory.CreateDirectory(destinationRoot);
        var manifest = Path.Combine(destinationRoot, "清单.csv");
        var rows = new List<string> { "relative_path,size_bytes,mtime_utc,sha256" };
        var errors = new List<string>();
        int filesDone = 0, filesFailed = 0;
        long bytesDone = 0;

        void Report(string current, string phase) =>
            progress?.Report(new ArchiveProgress(filesDone, filesDone + filesFailed, bytesDone, current, phase));

        foreach (var sel in selections)
        {
            Report(sel.RelativePath, "copy");
            var (ok, bytes) = await CopyRecursiveAsync(sel.Node, destinationRoot, sel.RelativePath, rows, errors, progress, ct).ConfigureAwait(false);
            if (ok) { filesDone++; bytesDone += bytes; }
            else filesFailed++;
        }

        await File.WriteAllTextAsync(manifest, string.Join("\n", rows) + "\n", new UTF8Encoding(false), ct).ConfigureAwait(false);
        Report("清单.csv", "done");

        return new ArchiveResult(filesDone, filesFailed, 0, bytesDone, manifest,
            DateTime.UtcNow - started, errors);
    }

    private async Task<(bool Ok, long Bytes)> CopyRecursiveAsync(
        IFileSystemNode node,
        string destRoot,
        string relative,
        List<string> rows,
        List<string> errors,
        IProgress<ArchiveProgress>? progress,
        CancellationToken ct)
    {
        if (node.IsDir)
        {
            var dir = Path.Combine(destRoot, Sanitize(relative));
            Directory.CreateDirectory(dir);
            long total = 0;
            bool allOk = true;
            await foreach (var child in _transport.EnumerateAsync(node, ct))
            {
                var childRel = string.IsNullOrEmpty(relative) ? child.DisplayName : $"{relative}/{child.DisplayName}";
                var (ok, bytes) = await CopyRecursiveAsync(child, destRoot, childRel, rows, errors, progress, ct).ConfigureAwait(false);
                total += bytes;
                allOk &= ok;
            }
            return (allOk, total);
        }

        try
        {
            var destRel = Sanitize(relative);
            var destPath = Path.Combine(destRoot, destRel);
            var parent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            string hash;
            long written;
            await using (var src = await node.OpenReadAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                hash = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
                written = dst.Length;
            }
            var mtime = node.MTime?.UtcDateTime.ToString("o") ?? "";
            rows.Add($"{Csv(relative)},{written},{mtime},{hash}");
            progress?.Report(new ArchiveProgress(0, 0, written, relative, "file"));
            return (true, written);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add($"{relative}: {ex.GetType().Name}: {ex.Message}");
            return (false, 0);
        }
    }

    /// <summary>Remove characters illegal on Windows paths; keep '/' separators.</summary>
    private static string Sanitize(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) parts[i] = parts[i].Replace(c, '_');
            if (parts[i].Length == 0) parts[i] = "_";
        }
        return Path.Combine(parts);
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
