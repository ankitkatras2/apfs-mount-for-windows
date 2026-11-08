using System.Buffers.Binary;
using System.Numerics;
using APFSMount.Core.IO;
using APFSMount.Core.Models;

namespace APFSMount.Core.Parsing;

public sealed class ApfsContainerProbe
{
    private const int HeaderBytes = 4096;
    private const int VolumeOidOffset = 184;
    private const int MaximumVolumeSlots = 100;

    public async ValueTask<ApfsContainer?> ProbeAsync(
        IBlockSource source,
        long offset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (source.Length is long length && (offset > length || length - offset < HeaderBytes))
            return null;

        var data = new byte[HeaderBytes];
        try
        {
            await source.ReadExactlyAsync(offset, data, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        if (!data.AsSpan(32, 4).SequenceEqual("NXSB"u8))
            return null;

        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(36, 4));
        if (blockSize is < 4096 or > 65536 || !BitOperations.IsPow2(blockSize))
            return null;

        var blockCount = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(40, 8));
        if (blockCount == 0)
            return null;

        if (source.Length is long sourceLength && blockCount <= ulong.MaxValue / blockSize)
        {
            var declaredBytes = blockCount * blockSize;
            var availableBytes = (ulong)(sourceLength - offset);
            if (declaredBytes > availableBytes)
                return null;
        }

        var volumes = new List<ApfsVolumeReference>();
        for (var slot = 0; slot < MaximumVolumeSlots; slot++)
        {
            var objectId = BinaryPrimitives.ReadUInt64LittleEndian(
                data.AsSpan(VolumeOidOffset + slot * sizeof(ulong), sizeof(ulong)));
            if (objectId != 0)
                volumes.Add(new ApfsVolumeReference(slot, objectId));
        }

        return new ApfsContainer(
            offset,
            blockSize,
            blockCount,
            FormatBigEndianUuid(data.AsSpan(72, 16)),
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(48, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(56, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(64, 8)),
            volumes);
    }

    private static string FormatBigEndianUuid(ReadOnlySpan<byte> bytes)
    {
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return string.Create(
            36,
            hex,
            static (destination, value) =>
            {
                value.AsSpan(0, 8).CopyTo(destination);
                destination[8] = '-';
                value.AsSpan(8, 4).CopyTo(destination[9..]);
                destination[13] = '-';
                value.AsSpan(12, 4).CopyTo(destination[14..]);
                destination[18] = '-';
                value.AsSpan(16, 4).CopyTo(destination[19..]);
                destination[23] = '-';
                value.AsSpan(20, 12).CopyTo(destination[24..]);
            });
    }
}
