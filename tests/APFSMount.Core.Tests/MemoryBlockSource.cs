using APFSMount.Core.IO;

namespace APFSMount.Core.Tests;

internal sealed class MemoryBlockSource(byte[] data) : IBlockSource
{
    public long? Length => data.LongLength;

    public ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0 || offset > data.LongLength - destination.Length)
            throw new EndOfStreamException();
        data.AsMemory((int)offset, destination.Length).CopyTo(destination);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
