namespace APFSMount.Core.Models;

public enum StorageSourceKind
{
    DiskImage,
    PhysicalDisk,
}

public sealed record StorageSource(
    string Id,
    string DisplayName,
    string Path,
    StorageSourceKind Kind,
    long? Length,
    bool CanOpenReadOnly,
    string? Detail = null);
