using System.Runtime.InteropServices;

namespace APFSMount.App;

internal sealed class DiskNotifications : NativeWindow, IDisposable
{
    private readonly Action<bool> _changed;
    private readonly IntPtr _registration;
    public DiskNotifications(Action<bool> changed)
    {
        _changed = changed;
        CreateHandle(new CreateParams { Caption = "APFSMount USB notifications", Parent = new IntPtr(-3) });
        var filter = new DeviceInterface { Size = Marshal.SizeOf<DeviceInterface>(), Type = 5,
            ClassGuid = new Guid("53f56307-b6bf-11d0-94f2-00a0c91efb8b") };
        _registration = RegisterDeviceNotification(Handle, ref filter, 0);
        if (_registration == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0219 && (message.WParam.ToInt64() == 0x8000 || message.WParam.ToInt64() == 0x8004))
            _changed(message.WParam.ToInt64() == 0x8004);
        base.WndProc(ref message);
    }
    public void Dispose() { UnregisterDeviceNotification(_registration); DestroyHandle(); }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterface { public int Size; public int Type; public int Reserved; public Guid ClassGuid; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DeviceInterface filter, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);
}
