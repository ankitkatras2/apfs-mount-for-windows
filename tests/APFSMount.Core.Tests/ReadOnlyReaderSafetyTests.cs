using APFSMount.Core.IO;
using APFSMount.Core.Services;

namespace APFSMount.Core.Tests;

public sealed class ReadOnlyReaderSafetyTests
{
    [Fact]
    public void BoundedStreamCannotReadOutsideContainerOrWrite()
    {
        using var inner = new MemoryStream(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        using var bounded = new BoundedReadOnlyStream(inner, 8, 8);
        var buffer = new byte[16];

        var read = bounded.Read(buffer, 0, buffer.Length);

        Assert.Equal(8, read);
        Assert.Equal(Enumerable.Range(8, 8).Select(value => (byte)value), buffer[..8]);
        Assert.Throws<IOException>(() => bounded.Seek(9, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => bounded.Write([1], 0, 1));
    }

    [Theory]
    [InlineData("Users/alice/report.txt", "Users\\alice\\report.txt")]
    [InlineData("folder/a:b.txt", "folder\\a_b.txt")]
    [InlineData("CON", "_CON")]
    public void CopyPathsAreMadeSafeForWindows(string archivePath, string expected)
    {
        Assert.Equal(expected, ApfsArchiveReader.MakeSafeRelativePath(archivePath));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("folder/../secret")]
    [InlineData("")]
    public void TraversalPathsAreRejected(string archivePath)
    {
        Assert.Throws<IOException>(() => ApfsArchiveReader.MakeSafeRelativePath(archivePath));
    }
}
