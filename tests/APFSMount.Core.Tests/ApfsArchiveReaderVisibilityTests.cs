using APFSMount.Core.Services;

namespace APFSMount.Core.Tests;

public sealed class ApfsArchiveReaderVisibilityTests
{
    [Theory]
    [InlineData("IMG_0001.HEIC")]
    [InlineData("DCIM/100APPLE/IMG_0002.JPG")]
    [InlineData("Movies/clip.MOV")]
    public void RealMediaRemainsVisible(string path) =>
        Assert.True(ApfsArchiveReader.ShouldShowInMountedView(path));

    [Theory]
    [InlineData("IMG_0001.HEIC_com.apple.assetsd.addedDate")]
    [InlineData("IMG_0001.HEIC:com.apple.ResourceFork")]
    [InlineData("._IMG_0001.HEIC")]
    [InlineData(".Spotlight-V100/store.db")]
    [InlineData(".Trashes/501/deleted.mov")]
    [InlineData(".DS_Store")]
    public void MetadataAndMacHousekeepingAreHidden(string path) =>
        Assert.False(ApfsArchiveReader.ShouldShowInMountedView(path));
}
