using Microsoft.Win32.SafeHandles;

namespace APFSMount.Core.IO;

public sealed class ReadOnlyBlockSource : IBlockSource
{
    private readonly SafeFileHandle _handle;

    private ReadOnlyBlockSource(SafeFileHandle handle, long? length)
    {
        _handle = handle;
        Length = length;
    }

    public long? Length { get; }

    public static ReadOnlyBlockSource OpenImage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var handle = File.OpenHandle(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        return new ReadOnlyBlockSource(handle, new FileInfo(fullPath).Length);
    }

    public static ReadOnlyBlockSource OpenPhysicalDisk(string devicePath, long? length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        if (!devicePath.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only explicit Windows PhysicalDrive paths are accepted.", nameof(devicePath));

        var handle = File.OpenHandle(
            devicePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        return new ReadOnlyBlockSource(handle, length);
    }

    public async ValueTask ReadExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        var consumed = 0;
        while (consumed < destination.Length)
        {
            var read = await RandomAccess.ReadAsync(
                _handle,
                destination[consumed..],
                offset + consumed,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException($"Unexpected end of source at byte {offset + consumed}.");
            consumed += read;
        }
    }

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
