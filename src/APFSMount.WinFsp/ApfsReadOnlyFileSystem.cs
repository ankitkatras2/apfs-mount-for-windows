using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32.SafeHandles;
using APFSMount.Core.Models;
using APFSMount.Core.Services;
using Fsp;
using Fsp.Interop;
using FileInfo = Fsp.Interop.FileInfo;

namespace APFSMount.WinFsp;

public sealed class ApfsMountSession : IDisposable
{
    private readonly FileSystemHost _host;
    private bool _disposed;

    private ApfsMountSession(FileSystemHost host, string mountPoint)
    {
        _host = host;
        MountPoint = mountPoint;
    }

    public string MountPoint { get; }

    public static ApfsMountSession Mount(
        StorageSource source,
        ApfsContainer container,
        IReadOnlyList<ApfsFileEntry> entries,
        ApfsArchiveReader reader,
        bool explorerVisible = true)
    {
        if (entries.Count == 0)
            throw new IOException("The APFS volume has no readable entries to mount.");

        if (!explorerVisible)
        {
            var driveLetter = FindCandidateDriveLetters().FirstOrDefault()
                ?? throw new IOException("No free drive letter is available.");
            var host = CreateHost(source, container, entries, reader);
            var result = host.Mount(driveLetter, null, false, 0);
            if (result >= 0)
                return new ApfsMountSession(host, driveLetter);
            host.Dispose();
            throw new IOException($"WinFsp could not mount {driveLetter} (NTSTATUS 0x{result:X8}).");
        }

        var mountManagerDrivesBefore = GetMountManagerDriveLetters();
        int? lastResult = null;
        foreach (var driveLetter in FindCandidateDriveLetters())
        {
            // The extended drive form asks WinFsp to register through Windows Mount Manager.
            // A plain "Z:" creates a logon-local DOS device that an elevated process can see
            // while the user's normal File Explorer cannot.
            var mountPoint = @"\\?\" + driveLetter;
            var host = CreateHost(source, container, entries, reader);
            var result = host.Mount(mountPoint, null, false, 0);
            if (result < 0)
            {
                lastResult = result;
                host.Dispose();
                continue;
            }

            var explorerDrive = GetMountManagerDriveLetters()
                .Except(mountManagerDrivesBefore)
                .OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (explorerDrive is not null)
                return new ApfsMountSession(host, explorerDrive);

            host.Dispose();
        }

        var detail = lastResult is { } status ? $" Last NTSTATUS: 0x{status:X8}." : string.Empty;
        throw new IOException($"WinFsp could not register an Explorer-visible drive letter.{detail}");
    }

    private static FileSystemHost CreateHost(
        StorageSource source,
        ApfsContainer container,
        IReadOnlyList<ApfsFileEntry> entries,
        ApfsArchiveReader reader) => new(new ApfsReadOnlyFileSystem(source, container, entries, reader))
    {
        FileSystemName = "APFSMount",
        FileInfoTimeout = 1000,
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _host.Unmount();
        _host.Dispose();
        _disposed = true;
    }

    private static IEnumerable<string> FindCandidateDriveLetters()
    {
        var used = DriveInfo.GetDrives()
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();
        for (var letter = 'Z'; letter >= 'D'; letter--)
            if (!used.Contains(letter))
                yield return $"{letter}:";
    }

    private static HashSet<string> GetMountManagerDriveLetters()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var letter = 'D'; letter <= 'Z'; letter++)
        {
            var drive = $"{letter}:";
            var volumeName = new StringBuilder(64);
            if (GetVolumeNameForVolumeMountPoint(drive + "\\", volumeName, volumeName.Capacity))
                result.Add(drive);
        }
        return result;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        StringBuilder volumeName,
        int bufferLength);
}

internal sealed class ApfsReadOnlyFileSystem : FileSystemBase
{
    private readonly StorageSource _source;
    private readonly ApfsContainer _container;
    private readonly ApfsArchiveReader _reader;
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] _securityDescriptor;
    private readonly string _cacheRoot;
    private readonly ulong _usedBytes;

    public ApfsReadOnlyFileSystem(
        StorageSource source,
        ApfsContainer container,
        IReadOnlyList<ApfsFileEntry> entries,
        ApfsArchiveReader reader)
    {
        _source = source;
        _container = container;
        _reader = reader;
        _cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "APFSMount",
            "MountCache",
            container.Identifier,
            Guid.NewGuid().ToString("N"));

        var descriptor = new RawSecurityDescriptor("O:BAG:BAD:P(A;;FR;;;WD)");
        _securityDescriptor = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(_securityDescriptor, 0);

        var root = new Node("\\", "", true, null);
        _nodes[root.Path] = root;
        foreach (var entry in entries)
            AddEntry(entry);
        root.Freeze();
        _usedBytes = CalculateUsedBytes(entries);
    }

    public override int Init(object hostObject)
    {
        var host = (FileSystemHost)hostObject;
        host.SectorSize = 512;
        host.SectorsPerAllocationUnit = 8;
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = (uint)_container.Identifier.GetHashCode(StringComparison.Ordinal);
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = Math.Max(_container.DeclaredBytes ?? 0, _usedBytes);
        volumeInfo.FreeSize = volumeInfo.TotalSize - Math.Min(volumeInfo.TotalSize, _usedBytes);
        volumeInfo.SetVolumeLabel("APFS Mount for Windows");
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        if (!TryGet(fileName, out var node))
        {
            fileAttributes = 0;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        fileAttributes = node.Info.FileAttributes;
        if (securityDescriptor is not null)
            securityDescriptor = _securityDescriptor;
        return STATUS_SUCCESS;
    }

    public override int Open(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out object fileNode,
        out object fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        fileDesc = null!;
        if (!TryGet(fileName, out var node))
        {
            fileNode = null!;
            fileInfo = default;
            normalizedName = null!;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        if ((createOptions & FILE_DIRECTORY_FILE) != 0 && !node.IsDirectory)
        {
            fileNode = null!;
            fileInfo = default;
            normalizedName = null!;
            return STATUS_NOT_A_DIRECTORY;
        }
        if ((createOptions & FILE_NON_DIRECTORY_FILE) != 0 && node.IsDirectory)
        {
            fileNode = null!;
            fileInfo = default;
            normalizedName = null!;
            return STATUS_FILE_IS_A_DIRECTORY;
        }
        fileNode = node;
        fileInfo = node.Info;
        normalizedName = node.Path;
        return STATUS_SUCCESS;
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = ((Node)fileNode).Info;
        return STATUS_SUCCESS;
    }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = _securityDescriptor;
        return STATUS_SUCCESS;
    }

    public override bool ReadDirectoryEntry(
        object fileNode,
        object fileDesc,
        string pattern,
        string marker,
        ref object context,
        out string fileName,
        out FileInfo fileInfo)
    {
        var directory = (Node)fileNode;
        if (context is not DirectoryCursor cursor)
        {
            cursor = new DirectoryCursor(directory.OrderedChildren, marker);
            context = cursor;
        }
        if (cursor.MoveNext(out var child))
        {
            fileName = child.Name;
            fileInfo = child.Info;
            return true;
        }
        fileName = null!;
        fileInfo = default;
        return false;
    }

    public override int GetDirInfoByName(
        object parentNode,
        object fileDesc,
        string fileName,
        out string normalizedName,
        out FileInfo fileInfo)
    {
        var parent = (Node)parentNode;
        var child = parent.FindChild(fileName);
        if (child is null)
        {
            normalizedName = null!;
            fileInfo = default;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        normalizedName = child.Name;
        fileInfo = child.Info;
        return STATUS_SUCCESS;
    }

    public override int Read(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        out uint bytesTransferred)
    {
        var node = (Node)fileNode;
        bytesTransferred = 0;
        if (node.IsDirectory)
            return STATUS_FILE_IS_A_DIRECTORY;
        if (offset >= node.Info.FileSize)
            return STATUS_END_OF_FILE;

        try
        {
            var cachePath = EnsureCached(node);
            using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            stream.Position = checked((long)offset);
            var count = (int)Math.Min(length, node.Info.FileSize - offset);
            var managed = new byte[count];
            var read = stream.Read(managed, 0, count);
            Marshal.Copy(managed, 0, buffer, read);
            bytesTransferred = (uint)read;
            return STATUS_SUCCESS;
        }
        catch (Exception exception)
        {
            TraceRead($"ERROR path={node.Path} {exception}");
            return STATUS_UNEXPECTED_IO_ERROR;
        }
    }

    private static void TraceRead(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "APFSMount");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "read-errors.log"),
                $"{DateTimeOffset.Now:O} [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never affect mounted-file reads.
        }
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor, ulong allocationSize, out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = ((Node)fileNode).Info;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo)
    {
        fileInfo = ((Node)fileNode).Info;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        fileInfo = ((Node)fileNode).Info;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName) => STATUS_MEDIA_WRITE_PROTECTED;
    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists) => STATUS_MEDIA_WRITE_PROTECTED;
    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor) => STATUS_MEDIA_WRITE_PROTECTED;
    public override int SetDelete(object fileNode, object fileDesc, string fileName, bool deleteFile) => STATUS_MEDIA_WRITE_PROTECTED;

    private string EnsureCached(Node node)
    {
        lock (node.CacheLock)
        {
            if (node.CachePath is not null && File.Exists(node.CachePath))
                return node.CachePath;
            node.CachePath = RunWithProcessIdentity(() =>
                _reader.MaterializeAsync(
                        _source,
                        _container,
                        node.SourceEntry!,
                        _cacheRoot)
                    .GetAwaiter()
                    .GetResult());
            return node.CachePath;
        }
    }

    private static T RunWithProcessIdentity<T>(Func<T> operation)
    {
        const uint tokenImpersonate = 0x0004;
        const uint tokenQuery = 0x0008;
        const int errorNoToken = 1008;

        var wasImpersonating = OpenThreadToken(
            GetCurrentThread(),
            tokenImpersonate | tokenQuery,
            true,
            out var callerToken);
        var tokenError = Marshal.GetLastWin32Error();
        if (!wasImpersonating && tokenError != errorNoToken)
            throw new Win32Exception(tokenError, "Could not inspect the WinFsp caller identity.");
        if (wasImpersonating && !RevertToSelf())
        {
            callerToken.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not use APFSMount's raw-disk identity.");
        }

        try
        {
            return operation();
        }
        finally
        {
            if (wasImpersonating)
            {
                var restored = SetThreadToken(IntPtr.Zero, callerToken);
                var restoreError = Marshal.GetLastWin32Error();
                callerToken.Dispose();
                if (!restored)
                    throw new Win32Exception(restoreError, "Could not restore the WinFsp caller identity.");
            }
        }
    }

    private void AddEntry(ApfsFileEntry entry)
    {
        var parts = entry.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parent = _nodes["\\"];
        for (var index = 0; index < parts.Length; index++)
        {
            var name = SanitizeName(parts[index]);
            var path = parent.Path == "\\" ? "\\" + name : parent.Path + "\\" + name;
            var isFinal = index == parts.Length - 1;
            if (!_nodes.TryGetValue(path, out var node))
            {
                node = new Node(path, name, !isFinal || entry.IsDirectory, isFinal ? entry : null);
                _nodes[path] = node;
                parent.Children.Add(node);
            }
            else if (isFinal && node.SourceEntry is null)
            {
                node.SourceEntry = entry;
                node.RefreshInfo();
            }
            parent = node;
        }
    }

    private static ulong CalculateUsedBytes(IEnumerable<ApfsFileEntry> entries)
    {
        ulong result = 0;
        foreach (var entry in entries.Where(entry => !entry.IsDirectory))
        {
            if (ulong.MaxValue - result < entry.Size)
                return ulong.MaxValue;
            result += entry.Size;
        }
        return result;
    }

    private bool TryGet(string path, out Node node) =>
        _nodes.TryGetValue(NormalizeWinPath(path), out node!);

    private static string NormalizeWinPath(string path)
    {
        var result = "\\" + path.Replace('/', '\\').Trim('\\');
        return result.Length == 1 ? "\\" : result;
    }

    private static string SanitizeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray())
            .TrimEnd(' ', '.');
        return string.IsNullOrEmpty(result) ? "_" : result;
    }

    private sealed class Node(string path, string name, bool isDirectory, ApfsFileEntry? sourceEntry)
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public bool IsDirectory { get; } = isDirectory;
        public ApfsFileEntry? SourceEntry { get; set; } = sourceEntry;
        public List<Node> Children { get; } = [];
        public Node[] OrderedChildren { get; private set; } = [];
        public object CacheLock { get; } = new();
        public string? CachePath { get; set; }
        public FileInfo Info { get; private set; } = MakeInfo(isDirectory, sourceEntry);

        public void RefreshInfo() => Info = MakeInfo(IsDirectory, SourceEntry);

        public void Freeze()
        {
            OrderedChildren = Children.OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var child in OrderedChildren)
                child.Freeze();
        }

        public Node? FindChild(string name)
        {
            var low = 0;
            var high = OrderedChildren.Length - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var comparison = StringComparer.OrdinalIgnoreCase.Compare(OrderedChildren[middle].Name, name);
                if (comparison == 0)
                    return OrderedChildren[middle];
                if (comparison < 0)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            return null;
        }

        private static FileInfo MakeInfo(bool directory, ApfsFileEntry? entry)
        {
            var modified = entry?.Modified ?? DateTime.UnixEpoch;
            var created = entry?.Created ?? modified;
            var size = directory ? 0UL : entry?.Size ?? 0;
            return new FileInfo
            {
                FileAttributes = (uint)(directory
                    ? System.IO.FileAttributes.Directory | System.IO.FileAttributes.ReadOnly
                    : System.IO.FileAttributes.ReadOnly),
                AllocationSize = (size + 4095) / 4096 * 4096,
                FileSize = size,
                CreationTime = (ulong)created.ToFileTimeUtc(),
                LastAccessTime = (ulong)modified.ToFileTimeUtc(),
                LastWriteTime = (ulong)modified.ToFileTimeUtc(),
                ChangeTime = (ulong)modified.ToFileTimeUtc(),
                IndexNumber = (ulong)(entry?.Path.GetHashCode(StringComparison.Ordinal) ?? 1),
            };
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenThreadToken(
        IntPtr threadHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool openAsSelf,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool RevertToSelf();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetThreadToken(IntPtr thread, SafeAccessTokenHandle token);

    private sealed class DirectoryCursor
    {
        private readonly Node[] _children;
        private int _index;

        public DirectoryCursor(Node[] children, string? marker)
        {
            _children = children;
            _index = FindFirstAfter(children, marker);
        }

        public bool MoveNext(out Node child)
        {
            if (_index < _children.Length)
            {
                child = _children[_index++];
                return true;
            }
            child = null!;
            return false;
        }

        private static int FindFirstAfter(Node[] children, string? marker)
        {
            if (string.IsNullOrEmpty(marker))
                return 0;
            var low = 0;
            var high = children.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (StringComparer.OrdinalIgnoreCase.Compare(children[middle].Name, marker) <= 0)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }
    }
}
