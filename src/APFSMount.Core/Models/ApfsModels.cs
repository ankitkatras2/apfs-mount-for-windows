namespace APFSMount.Core.Models;

public sealed record ApfsVolumeReference(int Slot, ulong ObjectIdentifier)
{
    public string DisplayName => $"Volume object {ObjectIdentifier} (slot {Slot})";
}

public sealed record ApfsContainer(
    long Offset,
    uint BlockSize,
    ulong BlockCount,
    string Identifier,
    ulong TransactionIdentifier,
    ulong CompatibleFeatures,
    ulong ReadOnlyCompatibleFeatures,
    ulong IncompatibleFeatures,
    IReadOnlyList<ApfsVolumeReference> Volumes)
{
    public ulong? DeclaredBytes => BlockCount <= ulong.MaxValue / BlockSize
        ? BlockCount * BlockSize
        : null;
}

public sealed record ScanIssue(string Code, string Message);

public sealed record StorageScanResult(
    StorageSource Source,
    IReadOnlyList<ApfsContainer> Containers,
    IReadOnlyList<ScanIssue> Issues)
{
    public bool HasApfs => Containers.Count > 0;
}
