using APFSMount.Core.IO;
using APFSMount.Core.Models;
using System.Security.Cryptography;
using System.Text;
using SevenZipExtractor;

namespace APFSMount.Core.Services;

/// <summary>
/// Read-only APFS directory and file access backed by 7-Zip's APFS handler.
/// The source stream is always opened with FileAccess.Read.
/// </summary>
public sealed class ApfsArchiveReader
{
    private readonly LibfsapfsArchiveReader _fallback = new();

    public Task<IReadOnlyList<ApfsFileEntry>> ListAsync(
        StorageSource source,
        ApfsContainer container,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = OpenArchiveOrFallback(source, container);
        if (archive is null)
            return _fallback.List(source, container);
        var entries = archive.Entries
            .Select(entry => new ApfsFileEntry(
                NormalizeArchivePath(entry.FileName),
                entry.IsFolder,
                entry.Size,
                NormalizeDate(entry.CreationTime),
                NormalizeDate(entry.LastWriteTime),
                entry.IsEncrypted))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .Where(entry => ShouldShowInMountedView(entry.Path))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (IReadOnlyList<ApfsFileEntry>)(entries.Length > 0 || !_fallback.IsAvailable
            ? entries
            : _fallback.List(source, container));
    }, cancellationToken);

    public Task<int> CopyAsync(
        StorageSource source,
        ApfsContainer container,
        ApfsFileEntry selection,
        string destinationDirectory,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        using var archive = OpenArchiveOrFallback(source, container);
        if (archive is null)
            return _fallback.Copy(source, container, selection, destinationDirectory, cancellationToken);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        var selectedPath = NormalizeArchivePath(selection.Path);
        var prefix = selectedPath.TrimEnd('/') + "/";
        var entries = archive.Entries.Where(entry =>
        {
            var candidate = NormalizeArchivePath(entry.FileName);
            return ShouldShowInMountedView(candidate) &&
                   (string.Equals(candidate, selectedPath, StringComparison.Ordinal) ||
                    (selection.IsDirectory && candidate.StartsWith(prefix, StringComparison.Ordinal)));
        }).ToArray();

        if (entries.Length == 0 && _fallback.IsAvailable)
            return _fallback.Copy(source, container, selection, destinationDirectory, cancellationToken);
        if (entries.Length == 0)
            throw new FileNotFoundException("The selected APFS entry is no longer available.", selectedPath);

        var copied = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archivePath = NormalizeArchivePath(entry.FileName);
            var relative = selection.IsDirectory
                ? archivePath[(selectedPath.LastIndexOf('/') + 1)..]
                : archivePath[(archivePath.LastIndexOf('/') + 1)..];
            var safeRelative = MakeSafeRelativePath(relative);
            var outputPath = Path.GetFullPath(Path.Combine(destinationRoot, safeRelative));
            if (!IsWithin(destinationRoot, outputPath))
                throw new IOException("An APFS path attempted to escape the selected destination.");

            if (entry.IsFolder)
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }
            if (File.Exists(outputPath))
                throw new IOException($"Copy stopped because '{outputPath}' already exists.");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var temporaryPath = outputPath + $".apfsmount-{Guid.NewGuid():N}.partial";
            try
            {
                {
                    using var output = new FileStream(
                        temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    entry.Extract(output);
                }
                if ((ulong)new FileInfo(temporaryPath).Length != entry.Size)
                    throw new IOException($"Size verification failed for '{archivePath}'.");
                File.Move(temporaryPath, outputPath);
                if (NormalizeDate(entry.LastWriteTime) is { } modified)
                    File.SetLastWriteTime(outputPath, modified);
                copied++;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        return copied;
    }, cancellationToken);

    public Task<string> MaterializeAsync(
        StorageSource source,
        ApfsContainer container,
        ApfsFileEntry selection,
        string cacheRoot,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (selection.IsDirectory)
            throw new IOException("Select a file to open.");

        cancellationToken.ThrowIfCancellationRequested();
        using var archive = OpenArchiveOrFallback(source, container);
        if (archive is null)
            return _fallback.Materialize(source, container, selection, cacheRoot, cancellationToken);
        var selectedPath = NormalizeArchivePath(selection.Path);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selectedPath)));
        var directory = Path.Combine(Path.GetFullPath(cacheRoot), key);
        var outputPath = Path.Combine(directory, MakeSafeRelativePath(Path.GetFileName(selectedPath)));
        if (File.Exists(outputPath) && (ulong)new FileInfo(outputPath).Length == selection.Size)
            return outputPath;

        Directory.CreateDirectory(directory);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(NormalizeArchivePath(candidate.FileName), selectedPath, StringComparison.Ordinal));
        if (entry is null && _fallback.IsAvailable)
            return _fallback.Materialize(source, container, selection, cacheRoot, cancellationToken);
        if (entry is null)
            throw new FileNotFoundException("The selected APFS file is no longer available.", selectedPath);

        var temporaryPath = outputPath + $".apfsmount-{Guid.NewGuid():N}.partial";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                entry.Extract(output);
            if ((ulong)new FileInfo(temporaryPath).Length != entry.Size)
                throw new IOException($"Size verification failed for '{selectedPath}'.");
            File.Move(temporaryPath, outputPath, true);
            return outputPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }, cancellationToken);

    public static string MakeSafeRelativePath(string archivePath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = archivePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
            throw new IOException("The APFS entry has an unsafe path.");

        return Path.Combine(parts.Select(part =>
        {
            var cleaned = new string(part.Select(ch => char.IsControl(ch) || invalid.Contains(ch) ? '_' : ch).ToArray())
                .TrimEnd(' ', '.');
            if (string.IsNullOrEmpty(cleaned))
                cleaned = "_";
            var stem = cleaned.Split('.')[0];
            if (ReservedWindowsNames.Contains(stem))
                cleaned = "_" + cleaned;
            return cleaned;
        }).ToArray());
    }

    private static readonly HashSet<string> ReservedWindowsNames = new(
        new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" },
        StringComparer.OrdinalIgnoreCase);

    private ArchiveFile? OpenArchiveOrFallback(StorageSource source, ApfsContainer container)
    {
        ArchiveFile? archive = null;
        try
        {
            archive = OpenArchive(source, container);
            // SevenZipExtractor opens lazily when Entries is first requested.
            _ = archive.Entries;
            return archive;
        }
        catch (SevenZipException) when (_fallback.IsAvailable && source.Kind == StorageSourceKind.PhysicalDisk)
        {
            archive?.Dispose();
            return null;
        }
        catch
        {
            archive?.Dispose();
            throw;
        }
    }

    private static ArchiveFile OpenArchive(StorageSource source, ApfsContainer container)
    {
        if (container.DeclaredBytes is not { } bytes || bytes > long.MaxValue)
            throw new IOException("The APFS container length is not supported.");
        var length = checked((long)bytes);
        if (container.Offset < 0 || source.Length is { } sourceLength && container.Offset + length > sourceLength)
            throw new IOException("The APFS container lies outside the source bounds.");

        var stream = new FileStream(
            source.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            source.Kind == StorageSourceKind.PhysicalDisk ? 1 : 64 * 1024,
            FileOptions.RandomAccess);
        try
        {
            Stream disk = source.Kind == StorageSourceKind.PhysicalDisk
                ? new SectorAlignedReadStream(stream, SectorAlignedReadStream.GetSectorSize(stream.SafeFileHandle),
                    source.Length ?? throw new IOException("Physical disk length is unavailable."))
                : stream;
            var bounded = new BoundedReadOnlyStream(disk, container.Offset, length);
            var library = ResolveSevenZipLibrary();
            return new ArchiveFile(bounded, SevenZipFormat.APFS, library);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string ResolveSevenZipLibrary()
    {
        var architecture = Environment.Is64BitProcess ? "x64" : "x86";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, architecture, "7z.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.dll"),
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("7-Zip's 7z.dll is required for APFS file browsing.");
    }

    private static string NormalizeArchivePath(string? path) =>
        (path ?? string.Empty).Replace('\\', '/').TrimStart('/');

    public static bool ShouldShowInMountedView(string archivePath)
    {
        var normalized = NormalizeArchivePath(archivePath);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        if (HiddenMacRootEntries.Contains(parts[0]))
            return false;

        var name = parts[^1];
        return !name.StartsWith("._", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("_com.apple.", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains(":com.apple.", StringComparison.OrdinalIgnoreCase) &&
               !name.EndsWith("_resourcefork", StringComparison.OrdinalIgnoreCase) &&
               !name.EndsWith("_rsrc", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> HiddenMacRootEntries = new(
        new[]
        {
            ".apdisk", ".DocumentRevisions-V100", ".DS_Store", ".fseventsd",
            ".Spotlight-V100", ".TemporaryItems", ".Trashes", ".vol",
        },
        StringComparer.OrdinalIgnoreCase);

    private static DateTime? NormalizeDate(DateTime value) => value.Year <= 1601 ? null : value;

    private static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
