using System.Diagnostics;
using APFSMount.Core.IO;
using APFSMount.Core.Services;
using APFSMount.WinFsp;

namespace APFSMount.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\APFSMountWindows.v1", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        if (args.Contains("--diagnose"))
        {
            var runtime = WinFspRuntimeProbe.Inspect();
            Log.Write(runtime.Message);
            foreach (var disk in UsbDiscovery.Read()) Log.Write($"USB: {disk.DisplayName}, {disk.Path}");
            Log.Write("Diagnosis complete; no drives mounted.");
            return;
        }
        Application.Run(new TrayContext());
        GC.KeepAlive(mutex);
    }
}

internal static class Log
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APFSMountWindows");
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, "activity.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                File.Move(path, path + ".previous", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly System.Drawing.Icon _appIcon;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private readonly Dictionary<string, List<ApfsMountSession>> _mounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _retry = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private readonly ApfsArchiveReader _reader = new();
    private readonly DiskNotifications _notifications;
    private bool _diskRemoved;
    private bool _busy;
    private bool _stopping;
    private bool _paused;

    public TrayContext()
    {
        _appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
            ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _notifications = new DiskNotifications(removed =>
        {
            _diskRemoved |= removed;
            Log.Write(removed ? "Disk removal notification received." : "Disk arrival notification received.");
        });
        var menu = new ContextMenuStrip();
        menu.Items.Add("APFSMount — read-only APFS").Enabled = false;
        menu.Items.Add("Open mounted drives", null, (_, _) =>
        {
            foreach (var mount in _mounts.Values.SelectMany(x => x))
                Process.Start(new ProcessStartInfo("explorer.exe", mount.MountPoint + "\\") { UseShellExecute = true });
        });
        var pause = new ToolStripMenuItem("Pause automatic mounting") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => _paused = pause.Checked;
        menu.Items.Add(pause);
        menu.Items.Add("Retry detected drives", null, (_, _) => { _ignored.Clear(); _retry.Clear(); });
        menu.Items.Add("About APFS Mount for Windows", null, (_, _) =>
        {
            using var about = new Form { Text = "APFS Mount for Windows", Icon = _appIcon, Width = 560, Height = 260, StartPosition = FormStartPosition.CenterScreen };
            about.Controls.Add(new Label { Left = 20, Top = 20, Width = 510, Height = 140,
                Text = "APFS Mount for Windows 1.0.2\nRead-only APFS USB mounting.\n\nWinFsp - Windows File System Proxy,\nCopyright (C) Bill Zissimopoulos\n\nApplication source: MIT license. Dependency licenses are included." });
            var link = new LinkLabel { Left = 20, Top = 170, Width = 510, Text = "https://github.com/winfsp/winfsp" };
            link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(link.Text) { UseShellExecute = true });
            about.Controls.Add(link);
            about.ShowDialog();
        });
        menu.Items.Add("Open activity log", null, (_, _) =>
        {
            Log.Write("Activity log opened.");
            Process.Start(new ProcessStartInfo("notepad.exe", Path.Combine(Log.Folder, "activity.log")) { UseShellExecute = true });
        });
        menu.Items.Add("Unmount all and exit", null, async (_, _) => await StopAsync());
        _icon = new NotifyIcon { Icon = _appIcon, Text = "APFS Mount for Windows: watching USB drives", ContextMenuStrip = menu, Visible = true };
        _timer.Tick += async (_, _) => await ReconcileAsync();
        var runtime = WinFspRuntimeProbe.Inspect();
        Log.Write(runtime.Message);
        if (!runtime.Available)
        {
            _paused = true;
            pause.Checked = true;
            Notice("WinFsp is required", runtime.Message);
        }
        _timer.Start();
        Log.Write("USB watcher started. All source access is read-only.");
    }

    private async Task ReconcileAsync()
    {
        if (_busy || _stopping) return;
        _busy = true;
        try
        {
            if (_diskRemoved)
            {
                _diskRemoved = false;
                foreach (var id in _mounts.Keys.ToArray()) Release(id);
                _ignored.Clear();
                _retry.Clear();
            }
            var disks = await Task.Run(UsbDiscovery.Read);
            var present = disks.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in _mounts.Keys.Where(id => !present.Contains(id)).ToArray()) Release(id);
            _ignored.RemoveWhere(id => !present.Contains(id));
            foreach (var id in _retry.Keys.Where(id => !present.Contains(id)).ToArray()) _retry.Remove(id);
            if (_paused || _stopping) return;
            foreach (var disk in disks)
            {
                if (_stopping || _paused) break;
                if (_mounts.ContainsKey(disk.Id) || _ignored.Contains(disk.Id) ||
                    (_retry.TryGetValue(disk.Id, out var next) && next > DateTime.UtcNow)) continue;
                var sessions = new List<ApfsMountSession>();
                try
                {
                    await using var source = ReadOnlyBlockSource.OpenPhysicalDisk(disk.Path, disk.Length);
                    var scan = await new StorageScanner().ScanAsync(disk, source);
                    if (!scan.HasApfs) { _ignored.Add(disk.Id); continue; }
                    foreach (var container in scan.Containers)
                    {
                        var entries = await _reader.ListAsync(disk, container);
                        if (_stopping) break;
                        // Recheck identity after lengthy decoding, before publishing a mount.
                        var stillPresent = await Task.Run(UsbDiscovery.Read);
                        if (!stillPresent.Any(d => d.Id == disk.Id)) throw new IOException("USB disk disconnected during scan.");
                        sessions.Add(ApfsMountSession.Mount(disk, container, entries, _reader));
                    }
                    if (_stopping) { foreach (var s in sessions) s.Dispose(); break; }
                    _mounts.Add(disk.Id, sessions);
                    var points = string.Join(", ", sessions.Select(s => s.MountPoint));
                    Notice("APFS drive ready", $"{disk.DisplayName}: {points} (read-only)");
                    Log.Write($"Mounted {disk.Path} at {points}");
                }
                catch (Exception ex)
                {
                    foreach (var session in sessions) session.Dispose();
                    // Back off unsupported or not-yet-ready devices instead of looping rapidly.
                    if (!_retry.ContainsKey(disk.Id))
                        Notice("APFS drive could not be mounted", "Open the activity log from the tray menu for details.");
                    _retry[disk.Id] = DateTime.UtcNow.AddMinutes(1);
                    Log.Write($"Mount failed for {disk.Path}: {ex.Message}");
                }
            }
            _icon.Text = $"APFS Mount for Windows: {_mounts.Values.Sum(x => x.Count)} read-only mount(s)";
        }
        catch (Exception ex) { Log.Write($"USB discovery: {ex.Message}"); }
        finally { _busy = false; }
    }

    private void Release(string id)
    {
        foreach (var mount in _mounts[id]) mount.Dispose();
        _mounts.Remove(id);
        Log.Write("Released mounts for " + id);
    }

    private void Notice(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(5000);
    }

    private async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _timer.Stop();
        while (_busy) await Task.Delay(100);
        foreach (var id in _mounts.Keys.ToArray()) Release(id);
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
        _timer.Dispose();
        _notifications.Dispose();
        ExitThread();
    }
}
