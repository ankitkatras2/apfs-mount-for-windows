using System.Buffers.Binary;
using APFSMount.Core.Parsing;

namespace APFSMount.Core.Tests;

public sealed class ApfsContainerProbeTests
{
    [Fact]
    public async Task ReadsBoundedContainerIdentityAndVolumeReferences()
    {
        var bytes = BuildContainer(blockCount: 1);
        var result = await new ApfsContainerProbe().ProbeAsync(new MemoryBlockSource(bytes), 0);

        Assert.NotNull(result);
        Assert.Equal((uint)4096, result.BlockSize);
        Assert.Equal((ulong)1, result.BlockCount);
        Assert.Equal((ulong)42, result.TransactionIdentifier);
        Assert.Equal("00112233-4455-6677-8899-aabbccddeeff", result.Identifier);
        Assert.Collection(result.Volumes,
            volume =>
            {
                Assert.Equal(0, volume.Slot);
                Assert.Equal((ulong)9001, volume.ObjectIdentifier);
            });
    }

    [Fact]
    public async Task RejectsImpossibleBlockSize()
    {
        var bytes = BuildContainer(blockCount: 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36, 4), 1234);

        var result = await new ApfsContainerProbe().ProbeAsync(new MemoryBlockSource(bytes), 0);

        Assert.Null(result);
    }

    internal static byte[] BuildContainer(ulong blockCount)
    {
        var bytes = new byte[checked((int)(blockCount * 4096))];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16, 8), 42);
        "NXSB"u8.CopyTo(bytes.AsSpan(32, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36, 4), 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40, 8), blockCount);
        Convert.FromHexString("00112233445566778899AABBCCDDEEFF").CopyTo(bytes, 72);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(184, 8), 9001);
        return bytes;
    }
}
