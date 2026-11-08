namespace APFSMount.Core.IO;

/// <summary>Projects a bounded, seekable, read-only view over another stream.</summary>
public sealed class BoundedReadOnlyStream(Stream inner, long start, long length, bool leaveOpen = false) : Stream
{
    private long _position;
    private bool _disposed;

    public override bool CanRead => !_disposed && inner.CanRead;
    public override bool CanSeek => !_disposed && inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException();
        if (_position >= length)
            return 0;

        var available = (int)Math.Min(count, length - _position);
        inner.Position = checked(start + _position);
        var read = inner.Read(buffer, offset, available);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (next < 0 || next > length)
            throw new IOException("Seek would leave the APFS container bounds.");
        return _position = next;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException("The source is read-only.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The source is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing && !leaveOpen)
            inner.Dispose();
        _disposed = true;
        base.Dispose(disposing);
    }
}
