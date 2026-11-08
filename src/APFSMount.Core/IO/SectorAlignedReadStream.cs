using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace APFSMount.Core.IO;

/// <summary>Translates arbitrary parser reads into whole-sector physical I/O.</summary>
public sealed class SectorAlignedReadStream(Stream inner, int sectorSize, long length) : Stream
{
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

    public static int GetSectorSize(SafeFileHandle handle)
    {
        var geometry = new byte[24];
        if (!DeviceIoControl(handle, 0x00070000, IntPtr.Zero, 0, geometry, geometry.Length, out var returned, IntPtr.Zero) || returned < 24)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var size = BitConverter.ToInt32(geometry, 20);
        if (size < 512 || size > 65536 || (size & (size - 1)) != 0)
            throw new IOException("Invalid physical disk sector size.");
        return size;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
        if (count == 0 || _position >= length) return 0;
        if (sectorSize <= 0 || (sectorSize & (sectorSize - 1)) != 0 || length % sectorSize != 0)
            throw new IOException("Invalid sector geometry.");
        var take = (int)Math.Min(Math.Min(count, 1024 * 1024), length - _position);
        var prefix = (int)(_position % sectorSize);
        var alignedPosition = _position - prefix;
        var alignedCount = checked((prefix + take + sectorSize - 1) / sectorSize * sectorSize);
        var bytes = new byte[alignedCount];
        inner.Position = alignedPosition;
        var read = inner.Read(bytes, 0, alignedCount);
        var available = Math.Min(take, Math.Max(0, read - prefix));
        Array.Copy(bytes, prefix, buffer, offset, available);
        _position += available;
        return available;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var next = origin switch {
            SeekOrigin.Begin => offset, SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        if (next < 0) throw new IOException("Negative disk offset.");
        return _position = next;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize,
        [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
