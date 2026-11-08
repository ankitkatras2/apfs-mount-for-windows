using System.Buffers.Binary;
using APFSMount.Core.Models;
using APFSMount.Core.Parsing;
using APFSMount.Core.Services;

namespace APFSMount.Core.Tests;

public sealed class StorageScannerTests
{
    [Fact]
    public async Task FindsApfsPartitionInGptImage()
    {
        const int firstLba = 2048;
        var disk = BuildGptImage(firstLba);
        var source = new StorageSource("test", "test", "test.img", StorageSourceKind.DiskImage, disk.Length, true);

        var result = await new StorageScanner().ScanAsync(source, new MemoryBlockSource(disk));

        var container = Assert.Single(result.Containers);
        Assert.Equal(firstLba * 512, container.Offset);
        Assert.True(result.HasApfs);
    }

    private static byte[] BuildGptImage(int firstLba)
    {
        const int sectorSize = 512;
        var disk = new byte[2 * 1024 * 1024];
        var header = disk.AsSpan(sectorSize, sectorSize);
        "EFI PART"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(72, 8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(80, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(84, 4), 128);

        var entry = disk.AsSpan(sectorSize * 2, 128);
        GptParser.AppleApfsPartitionType.TryWriteBytes(entry[..16]);
        Guid.Parse("11111111-2222-3333-4444-555555555555").TryWriteBytes(entry.Slice(16, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(32, 8), (ulong)firstLba);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(40, 8), (ulong)firstLba + 255);
        "Data".AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(entry.Slice(56, 8)));

        ApfsContainerProbeTests.BuildContainer(256).CopyTo(disk, firstLba * sectorSize);
        return disk;
    }
}
