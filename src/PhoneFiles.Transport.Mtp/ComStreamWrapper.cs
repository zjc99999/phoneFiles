using System;
using System.IO;
using System.Runtime.InteropServices;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace PhoneFiles.Transport.Mtp;

/// <summary>Read-only adapter from a COM IStream (WPD resource) to a managed Stream.</summary>
internal sealed class ComStreamWrapper : Stream
{
    private readonly ComIStream _stream;

    public ComStreamWrapper(ComIStream stream) => _stream = stream;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (offset != 0) throw new NotSupportedException("Non-zero offset not supported by COM stream.");
        var tmp = new byte[count];
        var pcbRead = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(pcbRead, 0);
            _stream.Read(tmp, count, pcbRead);
            int read = Marshal.ReadInt32(pcbRead);
            Array.Copy(tmp, 0, buffer, 0, read);
            return read;
        }
        finally { Marshal.FreeHGlobal(pcbRead); }
    }

    public override int Read(Span<byte> buffer)
    {
        var tmp = new byte[buffer.Length];
        int read = Read(tmp, 0, tmp.Length);
        tmp.AsSpan(0, read).CopyTo(buffer);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken ct)
        => Task.FromResult(Read(buffer, offset, count));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { Marshal.ReleaseComObject(_stream); } catch { } }
        base.Dispose(disposing);
    }
}
