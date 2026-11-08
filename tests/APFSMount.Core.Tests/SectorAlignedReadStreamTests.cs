using APFSMount.Core.IO;
using Xunit;

namespace APFSMount.Core.Tests;

public class SectorAlignedReadStreamTests
{
    [Theory]
    [InlineData(512, 4096, 32)]
    [InlineData(512, 4091, 100)]
    [InlineData(4096, 4091, 100)]
    [InlineData(512, 16370, 100)]
    public void ArbitraryReadsUseAlignedPhysicalIo(int sector, int offset, int count)
    {
        var data = Enumerable.Range(0, 16384).Select(x => (byte)(x % 251)).ToArray();
        using var raw = new StrictDisk(data, sector);
        using var reader = new SectorAlignedReadStream(raw, sector, data.Length);
        reader.Position = offset;
        var result = new byte[count];
        var read = reader.Read(result, 0, count);
        var expected = Math.Min(count, data.Length - offset);
        Assert.Equal(expected, read);
        Assert.Equal(data.Skip(offset).Take(expected), result.Take(read));
        Assert.Equal(offset + read, reader.Position);
        Assert.Throws<NotSupportedException>(() => reader.Write(result, 0, 1));
    }

    private sealed class StrictDisk(byte[] data, int sector) : MemoryStream(data, false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            Assert.Equal(0, Position % sector);
            Assert.Equal(0, count % sector);
            return base.Read(buffer, offset, count);
        }
    }
}
