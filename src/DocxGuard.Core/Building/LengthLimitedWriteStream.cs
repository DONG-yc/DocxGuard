namespace DocxGuard.Core.Building;

internal sealed class LengthLimitedWriteStream : Stream
{
    private readonly Stream inner;
    private readonly long maxLength;

    public LengthLimitedWriteStream(Stream inner, long maxLength)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanWrite || !inner.CanSeek)
        {
            throw new ArgumentException(
                "The inner stream must be writable and seekable.",
                nameof(inner));
        }

        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        }

        this.inner = inner;
        this.maxLength = maxLength;
    }

    public override bool CanRead => false;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set
        {
            if (value < 0 || value > maxLength)
            {
                throw new DocxPackageOutputLimitException(maxLength);
            }

            inner.Position = value;
        }
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
    {
        long basis = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => Position,
            SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Position = checked(basis + offset);
        return Position;
    }

    public override void SetLength(long value)
    {
        if (value < 0 || value > maxLength)
        {
            throw new DocxPackageOutputLimitException(maxLength);
        }

        inner.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureWriteFits(count);
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWriteFits(buffer.Length);
        inner.Write(buffer);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Flush();
        }

        base.Dispose(disposing);
    }

    private void EnsureWriteFits(int count)
    {
        if (count < 0
            || Position > maxLength - count)
        {
            throw new DocxPackageOutputLimitException(maxLength);
        }
    }
}

internal sealed class DocxPackageOutputLimitException : IOException
{
    public DocxPackageOutputLimitException(string message)
        : base(message)
    {
    }

    public DocxPackageOutputLimitException(long maxLength)
        : base($"The rebuilt package exceeds the configured limit of {maxLength} bytes.")
    {
    }
}
