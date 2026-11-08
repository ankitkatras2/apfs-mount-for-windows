using APFSMount.Core.Models;
using APFSMount.Core.Services;
using APFSMount.WinFsp;

var source = new StorageSource(
    "smoke", "smoke", Environment.ProcessPath!, StorageSourceKind.DiskImage,
    new FileInfo(Environment.ProcessPath!).Length, true);
var container = new ApfsContainer(0, 4096, 256, Guid.NewGuid().ToString(), 1, 0, 0, 0, []);
const int directoryCount = 35_000;
var entries = Enumerable.Range(0, directoryCount)
    .Select(index => new ApfsFileEntry(
        $"Visible Folder {index:D5}", true, 0, DateTime.UtcNow, DateTime.UtcNow, false))
    .Append(new ApfsFileEntry("sample.HEIC", false, 1_024, DateTime.UtcNow, DateTime.UtcNow, false))
    .ToArray();

using var mount = ApfsMountSession.Mount(
    source, container, entries, new ApfsArchiveReader(), explorerVisible: false);
var root = mount.MountPoint + "\\";
var names = Directory.GetFileSystemEntries(root).Select(Path.GetFileName).ToArray();
if (names.Length != entries.Length || !names.Contains("Visible Folder 34999") || !names.Contains("sample.HEIC"))
    throw new InvalidOperationException($"Expected {entries.Length} mounted entries, observed {names.Length}.");

var drive = new DriveInfo(mount.MountPoint);
const long expectedTotal = 1_048_576;
const long expectedFree = expectedTotal - 1_024;
if (drive.TotalSize != expectedTotal ||
    drive.TotalFreeSpace > expectedFree ||
    drive.TotalFreeSpace < expectedFree - 4_095)
    throw new InvalidOperationException($"Unexpected capacity: total={drive.TotalSize}, free={drive.TotalFreeSpace}.");

var writeRejected = false;
try
{
    File.WriteAllText(Path.Combine(root, "must-not-write.txt"), "blocked");
}
catch (UnauthorizedAccessException) { writeRejected = true; }
catch (IOException) { writeRejected = true; }

if (!writeRejected)
    throw new InvalidOperationException("The read-only mount accepted a write.");

Console.WriteLine($"PASS: mounted {mount.MountPoint}, enumerated {entries.Length:N0} entries, reported capacity, rejected write, and will unmount.");
