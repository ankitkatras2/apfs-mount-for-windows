namespace APFSMount.Core.IO;

public interface IBlockSource : IAsyncDisposable
{
    long? Length { get; }

    ValueTask ReadExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default);
}
