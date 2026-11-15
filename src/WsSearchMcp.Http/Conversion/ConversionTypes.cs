namespace WsSearchMcp.Http.Conversion;

/// <summary>Limits for turning a file into text. Output is separately capped by the server's per-call cap.</summary>
public sealed class ConversionOptions
{
    /// <summary>Largest file that will be loaded and converted (default 50 MiB).</summary>
    public long MaxInputBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>Largest amount of data a package may really decompress to while converting (default 200 MiB).</summary>
    public long MaxExpandedBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>Wall-clock limit for one conversion (default 60 s).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Total size of cached conversion results (default 64 MiB).</summary>
    public long CacheBytes { get; init; } = 64L * 1024 * 1024;
}

/// <summary>A conversion failed in a way the client can be told about (the message is client-safe: no paths).</summary>
public class ConversionException : Exception
{
    public ConversionException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>A size, expansion or time limit was exceeded.</summary>
public sealed class ConversionLimitException : ConversionException
{
    public ConversionLimitException(string message) : base(message) { }
}

/// <summary>
/// Shared allowance for one conversion: real decompressed bytes (never header-declared sizes), output
/// characters, and the cancellation token. One budget covers every entry of an archive.
/// </summary>
public sealed class ConversionBudget
{
    private long _expanded;
    private readonly long _maxExpanded;
    private readonly long _maxOutputChars;

    public ConversionBudget(long maxExpandedBytes, long maxOutputChars, CancellationToken ct)
    {
        _maxExpanded = maxExpandedBytes;
        _maxOutputChars = maxOutputChars;
        Token = ct;
    }

    public CancellationToken Token { get; }
    public long ExpandedBytes => Interlocked.Read(ref _expanded);

    public void Consume(int bytes)
    {
        if (Interlocked.Add(ref _expanded, bytes) > _maxExpanded)
            throw new ConversionLimitException("The file expands beyond the conversion limit.");
        Token.ThrowIfCancellationRequested();
    }

    public void CheckOutput(long chars)
    {
        if (chars > _maxOutputChars)
            throw new ConversionLimitException("The extracted text is larger than the per-call cap of this server.");
        Token.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// Counts the bytes that actually come out of <paramref name="inner"/> against a shared budget and aborts
/// the read the moment it is exceeded. A zip header's declared sizes are attacker-controlled and are not used.
/// </summary>
public sealed class CountingLimitStream : Stream
{
    private readonly Stream _inner;
    private readonly ConversionBudget _budget;

    public CountingLimitStream(Stream inner, ConversionBudget budget)
    {
        _inner = inner;
        _budget = budget;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        if (n > 0) _budget.Consume(n);
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        if (n > 0) _budget.Consume(n);
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Turns one kind of file into plain text. Implementations must be thread-safe and honour <see cref="ConversionBudget.Token"/>.</summary>
public interface IContentConverter
{
    bool CanConvert(FileKind kind);

    /// <summary>One-line client-facing description of what the extraction leaves out.</summary>
    string Describe(FileKind kind);

    /// <exception cref="ConversionException">The file cannot be converted (corrupt, protected, no text, over a limit).</exception>
    string Convert(Stream input, FileKind kind, ConversionBudget budget);
}
