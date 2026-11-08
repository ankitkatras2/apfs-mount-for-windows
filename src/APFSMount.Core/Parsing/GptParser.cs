using System.Buffers.Binary;
using APFSMount.Core.IO;

namespace APFSMount.Core.Parsing;

public sealed record GptPartition(Guid TypeIdentifier, Guid UniqueIdentifier, ulong FirstLba, ulong LastLba, string Name);

public sealed class GptParser
{
    public static readonly Guid AppleApfsPartitionType = new("7c3457ef-0000-11aa-aa11-00306543ecac");
    private const int MaximumPartitionTableBytes = 16 * 1024 * 1024;

    public async ValueTask<IReadOnlyList<GptPartition>> ReadAsync(
        IBlockSource source,
        int sectorSize,
        CancellationToken cancellationToken = default)
    {
        if (sectorSize is not (512 or 4096))
            throw new ArgumentOutOfRangeException(nameof(sectorSize));

        var header = new byte[sectorSize];
        try
        {
            await source.ReadExactlyAsync(sectorSize, header, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return [];
        }

        if (!header.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
            return [];

        var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72, 8));
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80, 4));
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84, 4));
        if (entryCount == 0 || entryCount > 4096 || entrySize < 128 || entrySize > 4096)
            return [];

        var tableBytes64 = (ulong)entryCount * entrySize;
        if (tableBytes64 > MaximumPartitionTableBytes || entriesLba > long.MaxValue / (ulong)sectorSize)
            return [];

        var tableOffset = checked((long)(entriesLba * (ulong)sectorSize));
        var table = new byte[(int)tableBytes64];
        try
        {
            await source.ReadExactlyAsync(tableOffset, table, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return [];
        }

        return ParseEntries(table, entryCount, entrySize);
    }

    private static IReadOnlyList<GptPartition> ParseEntries(
        byte[] table,
        uint entryCount,
        uint entrySize)
    {
        var partitions = new List<GptPartition>();
        for (var index = 0; index < entryCount; index++)
        {
            var entry = table.AsSpan(checked((int)(index * entrySize)), checked((int)entrySize));
            if (entry[..16].IndexOfAnyExcept((byte)0) < 0)
                continue;

            var firstLba = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(32, 8));
            var lastLba = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(40, 8));
            if (lastLba < firstLba)
                continue;

            var nameBytes = entry.Slice(56, Math.Min(72, entry.Length - 56));
            var name = System.Text.Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');
            partitions.Add(new GptPartition(
                new Guid(entry[..16]),
                new Guid(entry.Slice(16, 16)),
                firstLba,
                lastLba,
                name));
        }

        return partitions;
    }
}
