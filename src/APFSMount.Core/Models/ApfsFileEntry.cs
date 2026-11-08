namespace APFSMount.Core.Models;

public sealed record ApfsFileEntry(
    string Path,
    bool IsDirectory,
    ulong Size,
    DateTime? Created,
    DateTime? Modified,
    bool IsEncrypted)
{
    public string Kind => IsDirectory ? "Folder" : "File";

    public string DisplaySize => IsDirectory ? string.Empty : Size switch
    {
        >= 1UL << 40 => $"{Size / (double)(1UL << 40):0.##} TiB",
        >= 1UL << 30 => $"{Size / (double)(1UL << 30):0.##} GiB",
        >= 1UL << 20 => $"{Size / (double)(1UL << 20):0.##} MiB",
        >= 1UL << 10 => $"{Size / (double)(1UL << 10):0.##} KiB",
        _ => $"{Size} B",
    };
}
