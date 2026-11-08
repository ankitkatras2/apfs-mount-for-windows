using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using APFSMount.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace APFSMount.Core.Services;

/// <summary>
/// Read-only fallback for APFS volumes that are not exposed by 7-Zip's APFS handler.
/// libfsapfs is only used with its read access flag.
/// </summary>
internal sealed class LibfsapfsArchiveReader
{
    private static readonly object NativeLock = new();

    public bool IsAvailable => OperatingSystem.IsWindows() &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "libfsapfs.dll")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "apfsmountbridge.dll"));

    public IReadOnlyList<ApfsFileEntry> List(StorageSource source, ApfsContainer container)
    {
        lock (NativeLock)
            return ListCore(source, container);
    }

    private IReadOnlyList<ApfsFileEntry> ListCore(StorageSource source, ApfsContainer container)
    {
        using var session = Open(source, container);
        var result = new List<ApfsFileEntry>();
        foreach (var volume in session.Volumes)
        {
            var volumeName = MakeUniqueVolumeName(volume.Name, volume.Index, result);
            result.Add(new ApfsFileEntry(volumeName, true, 0, null, null, volume.IsLocked));
            if (volume.IsLocked)
                continue;

            using var root = volume.GetRoot();
            if (root is not null)
                AddChildren(root, volumeName, result);
        }
        return result.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public int Copy(
        StorageSource source,
        ApfsContainer container,
        ApfsFileEntry selection,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        lock (NativeLock)
            return CopyCore(source, container, selection, destinationDirectory, cancellationToken);
    }

    private int CopyCore(
        StorageSource source,
        ApfsContainer container,
        ApfsFileEntry selection,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var entries = ListCore(source, container);
        var selectedPath = Normalize(selection.Path);
        var prefix = selectedPath.TrimEnd('/') + "/";
        var selectedEntries = entries.Where(entry =>
            string.Equals(entry.Path, selectedPath, StringComparison.Ordinal) ||
            (selection.IsDirectory && entry.Path.StartsWith(prefix, StringComparison.Ordinal))).ToArray();
        if (selectedEntries.Length == 0)
            throw new FileNotFoundException("The selected APFS entry is no longer available.", selectedPath);

        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        using var session = Open(source, container);
        var copied = 0;
        foreach (var entry in selectedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = selection.IsDirectory
                ? entry.Path[(selectedPath.LastIndexOf('/') + 1)..]
                : Path.GetFileName(entry.Path);
            var outputPath = Path.GetFullPath(Path.Combine(
                destinationRoot,
                ApfsArchiveReader.MakeSafeRelativePath(relative)));
            EnsureWithin(destinationRoot, outputPath);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }
            if (File.Exists(outputPath))
                throw new IOException($"Copy stopped because '{outputPath}' already exists.");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            WriteVerified(session, entry, outputPath, overwrite: false, cancellationToken);
            copied++;
        }
        return copied;
    }

    public string Materialize(
        StorageSource source,
        ApfsContainer container,
        ApfsFileEntry selection,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        lock (NativeLock)
            return MaterializeCore(source, container, selection, cacheRoot, cancellationToken);
    }

    private string MaterializeCore(
        StorageSource source,
        ApfsContainer container,
        ApfsFileEntry selection,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        if (selection.IsDirectory)
            throw new IOException("Select a file to open.");
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(selection.Path)));
        var directory = Path.Combine(Path.GetFullPath(cacheRoot), key);
        var outputPath = Path.Combine(directory, ApfsArchiveReader.MakeSafeRelativePath(Path.GetFileName(selection.Path)));
        if (File.Exists(outputPath) && (ulong)new FileInfo(outputPath).Length == selection.Size)
            return outputPath;
        Directory.CreateDirectory(directory);
        using var session = Open(source, container);
        WriteVerified(session, selection, outputPath, overwrite: true, cancellationToken);
        return outputPath;
    }

    private static void WriteVerified(
        Session session,
        ApfsFileEntry entry,
        string outputPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var temporaryPath = outputPath + $".apfsmount-{Guid.NewGuid():N}.partial";
        try
        {
            using var input = session.OpenFile(entry.Path);
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1024 * 1024];
                long offset = 0;
                while ((ulong)offset < entry.Size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = (int)Math.Min((ulong)buffer.Length, entry.Size - (ulong)offset);
                    var read = input.Read(buffer, requested, offset);
                    if (read <= 0)
                        throw new EndOfStreamException($"Unexpected end of APFS file '{entry.Path}'.");
                    output.Write(buffer, 0, read);
                    offset += read;
                }
            }
            if ((ulong)new FileInfo(temporaryPath).Length != entry.Size)
                throw new IOException($"Size verification failed for '{entry.Path}'.");
            File.Move(temporaryPath, outputPath, overwrite);
            if (entry.Modified is { } modified)
                File.SetLastWriteTime(outputPath, modified);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static Session Open(StorageSource source, ApfsContainer container)
    {
        if (source.Kind == StorageSourceKind.PhysicalDisk)
        {
            var length = container.DeclaredBytes ??
                throw new IOException("The APFS container length is not supported.");
            return new Session(source.Path, container.Offset, length);
        }
        if (source.Kind == StorageSourceKind.DiskImage && container.Offset != 0)
            throw new NotSupportedException("The fallback APFS reader currently requires a partition image or a physical APFS volume.");
        return new Session(source.Path);
    }

    private static void AddChildren(NativeFileEntry directory, string parentPath, List<ApfsFileEntry> result)
    {
        var count = directory.ChildCount;
        for (var index = 0; index < count; index++)
        {
            using var child = directory.GetChild(index);
            var name = child.Name;
            if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
                continue;
            var path = Normalize(parentPath + "/" + name);
            if (!ApfsArchiveReader.ShouldShowInMountedView(path))
                continue;
            var isDirectory = child.IsDirectory;
            result.Add(new ApfsFileEntry(
                path,
                isDirectory,
                child.Size,
                FromUnixNanoseconds(child.CreationTime),
                FromUnixNanoseconds(child.ModificationTime),
                false));
            if (isDirectory)
                AddChildren(child, path, result);
        }
    }

    private static DateTime? FromUnixNanoseconds(long value)
    {
        if (value <= 0)
            return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(value / 1_000_000).LocalDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string MakeUniqueVolumeName(string name, int index, List<ApfsFileEntry> entries)
    {
        var candidate = string.IsNullOrWhiteSpace(name) ? $"APFS Volume {index + 1}" : Normalize(name);
        if (entries.All(entry => !string.Equals(entry.Path, candidate, StringComparison.OrdinalIgnoreCase)))
            return candidate;
        return $"{candidate} ({index + 1})";
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static void EnsureWithin(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("An APFS path attempted to escape the selected destination.");
    }

    private sealed class Session : IDisposable
    {
        private IntPtr _container;
        private IntPtr _rawContext;
        public List<NativeVolume> Volumes { get; } = [];

        public Session(string path)
        {
            Check(Native.ContainerInitialize(out _container, IntPtr.Zero), "initialize APFS container");
            try
            {
                Check(Native.ContainerOpenWide(_container, path, 1, IntPtr.Zero), $"open APFS volume '{path}'");
                LoadVolumes();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public Session(string physicalDiskPath, long containerOffset, ulong containerLength)
        {
            try
            {
                var openResult = Bridge.OpenRaw(
                    physicalDiskPath,
                    containerOffset,
                    containerLength,
                    out _rawContext,
                    out _container);
                if (openResult != 1)
                    throw new IOException(
                        $"libfsapfs could not open raw APFS container on '{physicalDiskPath}' " +
                        $"(bridge stage {Bridge.GetLastStage()}, Win32 {Bridge.GetLastWin32Error()}). " +
                        Marshal.PtrToStringAnsi(Bridge.GetLastError()));
                LoadVolumes();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void LoadVolumes()
        {
            Check(Native.ContainerGetNumberOfVolumes(_container, out var count, IntPtr.Zero), "read APFS volume count");
            for (var index = 0; index < count; index++)
            {
                using var error = new NativeError();
                error.Check(Native.ContainerGetVolumeByIndex(_container, index, out var volume, error.Pointer), "open APFS volume");
                Volumes.Add(new NativeVolume(volume, index));
            }
        }

        public NativeFileEntry OpenFile(string displayedPath)
        {
            var normalized = Normalize(displayedPath);
            foreach (var volume in Volumes)
            {
                var rootName = MakeUniqueVolumeName(volume.Name, volume.Index,
                    Volumes.Take(volume.Index).Select(v => new ApfsFileEntry(v.Name, true, 0, null, null, v.IsLocked)).ToList());
                if (!string.Equals(normalized, rootName, StringComparison.Ordinal) &&
                    !normalized.StartsWith(rootName + "/", StringComparison.Ordinal))
                    continue;
                var relative = normalized.Length == rootName.Length ? "/" : "/" + normalized[(rootName.Length + 1)..];
                return volume.GetByPath(relative) ?? throw new FileNotFoundException("APFS file not found.", displayedPath);
            }

            // The 7-Zip directory provider exposes paths without the libfsapfs
            // volume-name prefix. Try the same real path in each unlocked volume.
            foreach (var volume in Volumes.Where(volume => !volume.IsLocked))
                if (volume.GetByPath("/" + normalized) is { } entry)
                    return entry;

            throw new FileNotFoundException("APFS volume not found.", displayedPath);
        }

        public void Dispose()
        {
            foreach (var volume in Volumes)
                volume.Dispose();
            Volumes.Clear();
            if (_rawContext != IntPtr.Zero)
            {
                Bridge.CloseRaw(ref _rawContext);
                _container = IntPtr.Zero;
            }
            else if (_container != IntPtr.Zero)
            {
                Native.ContainerClose(_container, IntPtr.Zero);
                Native.ContainerFree(ref _container, IntPtr.Zero);
            }
        }
    }

    private sealed class NativeVolume : IDisposable
    {
        private IntPtr _handle;
        public NativeVolume(IntPtr handle, int index) { _handle = handle; Index = index; }
        public int Index { get; }
        public bool IsLocked => Native.VolumeIsLocked(_handle, IntPtr.Zero) == 1;
        public string Name => ReadUtf16(_handle, Native.VolumeGetNameSize, Native.VolumeGetName);
        public NativeFileEntry? GetRoot() => Native.VolumeGetRoot(_handle, out var entry, IntPtr.Zero) == 1
            ? new NativeFileEntry(entry)
            : null;
        public NativeFileEntry? GetByPath(string path) => Native.VolumeGetFileByPath(
            _handle, path, (nuint)path.Length, out var entry, IntPtr.Zero) == 1
                ? new NativeFileEntry(entry)
                : null;
        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
                Native.VolumeFree(ref _handle, IntPtr.Zero);
        }
    }

    private sealed class NativeFileEntry : IDisposable
    {
        private IntPtr _handle;
        public NativeFileEntry(IntPtr handle) => _handle = handle;
        public string Name => ReadUtf16(_handle, Native.FileGetNameSize, Native.FileGetName);
        public int ChildCount { get { Check(Native.FileGetChildCount(_handle, out var value, IntPtr.Zero), "read directory"); return value; } }
        public bool IsDirectory { get { Check(Native.FileGetMode(_handle, out var value, IntPtr.Zero), "read file mode"); return (value & 0xf000) == 0x4000; } }
        public ulong Size { get { Check(Native.FileGetSize(_handle, out var value, IntPtr.Zero), "read file size"); return value; } }
        public long CreationTime => GetTime(Native.FileGetCreationTime);
        public long ModificationTime => GetTime(Native.FileGetModificationTime);
        public NativeFileEntry GetChild(int index)
        {
            Check(Native.FileGetChild(_handle, index, out var child, IntPtr.Zero), "read directory entry");
            return new NativeFileEntry(child);
        }
        public int Read(byte[] buffer, int count, long offset)
        {
            var read = Native.FileReadAt(_handle, buffer, (nuint)count, offset, IntPtr.Zero);
            if (read < 0)
                throw new IOException("libfsapfs could not read file data.");
            return checked((int)read);
        }
        private long GetTime(Native.TimeGetter getter) => getter(_handle, out var value, IntPtr.Zero) == 1 ? value : 0;
        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
                Native.FileFree(ref _handle, IntPtr.Zero);
        }
    }

    private static string ReadUtf16(IntPtr handle, Native.StringSizeGetter sizeGetter, Native.StringGetter getter)
    {
        if (sizeGetter(handle, out var size, IntPtr.Zero) != 1 || size == 0 || size > 32768)
            return string.Empty;
        var buffer = new ushort[(int)size];
        Check(getter(handle, buffer, size, IntPtr.Zero), "read APFS name");
        var length = Array.IndexOf(buffer, (ushort)0);
        if (length < 0) length = buffer.Length;
        return Encoding.Unicode.GetString(MemoryMarshal.AsBytes(buffer.AsSpan(0, length)));
    }

    private static void Check(int result, string operation)
    {
        if (result != 1)
            throw new IOException($"libfsapfs could not {operation}.");
    }

    private sealed class NativeError : IDisposable
    {
        public IntPtr Pointer { get; } = Marshal.AllocHGlobal(IntPtr.Size);
        public NativeError() => Marshal.WriteIntPtr(Pointer, IntPtr.Zero);
        public void Check(int result, string operation)
        {
            if (result == 1) return;
            var value = Marshal.ReadIntPtr(Pointer);
            var buffer = new byte[16384];
            if (value != IntPtr.Zero) Native.ErrorBacktrace(value, buffer, (nuint)buffer.Length);
            throw new IOException($"libfsapfs could not {operation}: " + Encoding.UTF8.GetString(buffer).TrimEnd('\0'));
        }
        public void Dispose()
        {
            var value = Marshal.ReadIntPtr(Pointer);
            if (value != IntPtr.Zero) Native.ErrorFree(ref value);
            Marshal.FreeHGlobal(Pointer);
        }
    }

    private static class Native
    {
        private const string Dll = "libfsapfs.dll";
        [DllImport(Dll, EntryPoint = "libfsapfs_error_backtrace_sprint", CallingConvention = CallingConvention.Cdecl)] internal static extern int ErrorBacktrace(IntPtr value, [Out] byte[] buffer, nuint size);
        [DllImport(Dll, EntryPoint = "libfsapfs_error_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void ErrorFree(ref IntPtr value);
        internal delegate int StringSizeGetter(IntPtr handle, out nuint size, IntPtr error);
        internal delegate int StringGetter(IntPtr handle, [Out] ushort[] value, nuint size, IntPtr error);
        internal delegate int TimeGetter(IntPtr handle, out long value, IntPtr error);

        [DllImport(Dll, EntryPoint = "libfsapfs_container_initialize", CallingConvention = CallingConvention.Cdecl)] internal static extern int ContainerInitialize(out IntPtr value, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_container_free", CallingConvention = CallingConvention.Cdecl)] internal static extern int ContainerFree(ref IntPtr value, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_container_open_wide", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ContainerOpenWide(IntPtr value, string path, int access, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_container_close", CallingConvention = CallingConvention.Cdecl)] internal static extern int ContainerClose(IntPtr value, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_container_get_number_of_volumes", CallingConvention = CallingConvention.Cdecl)] internal static extern int ContainerGetNumberOfVolumes(IntPtr value, out int count, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_container_get_volume_by_index", CallingConvention = CallingConvention.Cdecl)] internal static extern int ContainerGetVolumeByIndex(IntPtr value, int index, out IntPtr volume, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_volume_free", CallingConvention = CallingConvention.Cdecl)] internal static extern int VolumeFree(ref IntPtr value, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_volume_is_locked", CallingConvention = CallingConvention.Cdecl)] internal static extern int VolumeIsLocked(IntPtr value, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_volume_get_utf16_name_size", CallingConvention = CallingConvention.Cdecl)] internal static extern int VolumeGetNameSize(IntPtr value, out nuint size, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_volume_get_utf16_name", CallingConvention = CallingConvention.Cdecl)] internal static extern int VolumeGetName(IntPtr value, [Out] ushort[] name, nuint size, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_volume_get_root_directory", CallingConvention = CallingConvention.Cdecl)] internal static extern int VolumeGetRoot(IntPtr value, out IntPtr entry, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_volume_get_file_entry_by_utf16_path", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int VolumeGetFileByPath(IntPtr value, string path, nuint length, out IntPtr entry, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_file_entry_free", CallingConvention = CallingConvention.Cdecl)] internal static extern int FileFree(ref IntPtr value, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_file_entry_get_utf16_name_size", CallingConvention = CallingConvention.Cdecl)] internal static extern int FileGetNameSize(IntPtr value, out nuint size, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_file_entry_get_utf16_name", CallingConvention = CallingConvention.Cdecl)] internal static extern int FileGetName(IntPtr value, [Out] ushort[] name, nuint size, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_file_entry_get_file_mode", CallingConvention = CallingConvention.Cdecl)] internal static extern int FileGetMode(IntPtr value, out ushort mode, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_file_entry_get_size", CallingConvention = CallingConvention.Cdecl)] internal static extern int FileGetSize(IntPtr value, out ulong size, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_file_entry_get_creation_time", CallingConvention = CallingConvention.Cdecl)] internal static extern int FileGetCreationTime(IntPtr value, out long time, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_file_entry_get_modification_time", CallingConvention = CallingConvention.Cdecl)] internal static extern int FileGetModificationTime(IntPtr value, out long time, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_file_entry_get_number_of_sub_file_entries", CallingConvention = CallingConvention.Cdecl)] internal static extern int FileGetChildCount(IntPtr value, out int count, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_file_entry_get_sub_file_entry_by_index", CallingConvention = CallingConvention.Cdecl)] internal static extern int FileGetChild(IntPtr value, int index, out IntPtr child, IntPtr error);
        [DllImport(Dll, EntryPoint = "libfsapfs_file_entry_read_buffer_at_offset", CallingConvention = CallingConvention.Cdecl)] internal static extern nint FileReadAt(IntPtr value, [Out] byte[] buffer, nuint size, long offset, IntPtr error);
    }

    private static class Bridge
    {
        private const string Dll = "apfsmountbridge.dll";

        [DllImport(Dll, EntryPoint = "apfsmount_open_raw", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal static extern int OpenRaw(
            string path,
            long baseOffset,
            ulong size,
            out IntPtr context,
            out IntPtr container);

        [DllImport(Dll, EntryPoint = "apfsmount_close_raw", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CloseRaw(ref IntPtr context);

        [DllImport(Dll, EntryPoint = "apfsmount_get_last_stage", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetLastStage();

        [DllImport(Dll, EntryPoint = "apfsmount_get_last_win32_error", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetLastWin32Error();

        [DllImport(Dll, EntryPoint = "apfsmount_get_last_error", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr GetLastError();
    }
}

internal static class RawVolumeResolver
{
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static string FindVolumePath(string physicalDiskPath, long partitionOffset)
    {
        var marker = "PHYSICALDRIVE";
        var markerIndex = physicalDiskPath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0 || !int.TryParse(physicalDiskPath[(markerIndex + marker.Length)..], out var diskNumber))
            throw new IOException($"Could not determine the disk number from '{physicalDiskPath}'.");

        var buffer = new StringBuilder(1024);
        var findHandle = FindFirstVolume(buffer, buffer.Capacity);
        if (findHandle == InvalidHandle)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            while (true)
            {
                var volumePath = buffer.ToString().TrimEnd('\\');
                if (Matches(volumePath, diskNumber, partitionOffset))
                    return volumePath;
                buffer.Clear();
                buffer.EnsureCapacity(1024);
                if (!FindNextVolume(findHandle, buffer, buffer.Capacity))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 18)
                        break;
                    throw new Win32Exception(error);
                }
            }
        }
        finally { FindVolumeClose(findHandle); }
        throw new IOException("Windows did not expose the APFS partition as a raw volume. Reconnect the USB drive and refresh disks.");
    }

    private static bool Matches(string volumePath, int diskNumber, long partitionOffset)
    {
        using var handle = CreateFile(volumePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return false;
        var buffer = new byte[1024];
        if (!DeviceIoControl(handle, IoctlVolumeGetVolumeDiskExtents, IntPtr.Zero, 0, buffer, buffer.Length, out var returned, IntPtr.Zero) || returned < 32)
            return false;
        var count = BitConverter.ToInt32(buffer, 0);
        for (var index = 0; index < count; index++)
        {
            var position = 8 + index * 24;
            if (position + 24 > returned)
                break;
            if (BitConverter.ToInt32(buffer, position) == diskNumber &&
                BitConverter.ToInt64(buffer, position + 8) == partitionOffset)
                return true;
        }
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindFirstVolume(StringBuilder volumeName, int bufferLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool FindNextVolume(IntPtr findHandle, StringBuilder volumeName, int bufferLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FindVolumeClose(IntPtr findHandle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
