using APFSMount.Core.IO;
using APFSMount.Core.Models;
using APFSMount.Core.Parsing;

namespace APFSMount.Core.Services;

public sealed class StorageScanner
{
    private readonly ApfsContainerProbe _apfs = new();
    private readonly GptParser _gpt = new();

    public async ValueTask<StorageScanResult> ScanAsync(
        StorageSource source,
        IBlockSource blockSource,
        CancellationToken cancellationToken = default)
    {
        var containers = new List<ApfsContainer>();
        var issues = new List<ScanIssue>();
        var seenOffsets = new HashSet<long>();

        await ProbeAtAsync(0).ConfigureAwait(false);

        var foundGpt = false;
        foreach (var sectorSize in new[] { 512, 4096 })
        {
            var partitions = await _gpt.ReadAsync(blockSource, sectorSize, cancellationToken).ConfigureAwait(false);
            if (partitions.Count == 0)
                continue;

            foundGpt = true;
            foreach (var partition in partitions.Where(p => p.TypeIdentifier == GptParser.AppleApfsPartitionType))
            {
                if (partition.FirstLba > long.MaxValue / (ulong)sectorSize)
                {
                    issues.Add(new ScanIssue("partition-offset-overflow", "An APFS GPT partition offset exceeds supported addressing."));
                    continue;
                }
                await ProbeAtAsync(checked((long)(partition.FirstLba * (ulong)sectorSize))).ConfigureAwait(false);
            }
            break;
        }

        if (containers.Count == 0)
            issues.Add(new ScanIssue(
                foundGpt ? "apfs-partition-not-readable" : "apfs-not-detected",
                foundGpt
                    ? "A GPT was found, but no supported APFS container superblock was readable."
                    : "No APFS container was detected at the source root or in a supported GPT partition."));

        return new StorageScanResult(source, containers, issues);

        async ValueTask ProbeAtAsync(long offset)
        {
            if (!seenOffsets.Add(offset))
                return;
            var container = await _apfs.ProbeAsync(blockSource, offset, cancellationToken).ConfigureAwait(false);
            if (container is not null)
                containers.Add(container);
        }
    }
}
