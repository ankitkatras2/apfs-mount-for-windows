using Microsoft.Win32;

namespace APFSMount.WinFsp;

public sealed record WinFspRuntimeStatus(bool Available, string Message, string? InstallDirectory);

public static class WinFspRuntimeProbe
{
    public static WinFspRuntimeStatus Inspect()
    {
        _ = typeof(Fsp.FileSystemHost);

        var registryPaths = ReadInstallDirectories();
        var candidates = registryPaths.Concat(new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinFsp"),
        }).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var bins = new[] { Path.Combine(candidate!, "bin"), candidate! };
            if (bins.Any(bin => File.Exists(Path.Combine(bin, "winfsp-x64.dll")) ||
                                File.Exists(Path.Combine(bin, "winfsp-a64.dll"))))
                return new WinFspRuntimeStatus(true, "WinFsp runtime detected.", candidate);
        }

        return new WinFspRuntimeStatus(
            false,
            "WinFsp is not installed. Mounting remains disabled; scanning never falls back to writes.",
            null);
    }

    private static string[] ReadInstallDirectories()
    {
        var result = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"SOFTWARE\WinFsp");
            foreach (var valueName in new[] { "SxsDir", "InstallDir" })
                if (key?.GetValue(valueName) is string installDirectory && !string.IsNullOrWhiteSpace(installDirectory))
                    result.Add(installDirectory);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
