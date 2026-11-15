using System.Text;

namespace WsSearchMcp.Http.Conversion;

/// <summary>Result of rendering pages of a PDF: its total page count and the pages that were produced.</summary>
public sealed record RenderResult(int PageCount, IReadOnlyList<RenderedPage> Pages);

/// <summary>Identifies one version of one file for the conversion cache.</summary>
public readonly record struct ConversionKey(string Path, long Size, long LastWriteTicks)
{
    public bool Equals(ConversionKey other) =>
        Size == other.Size && LastWriteTicks == other.LastWriteTicks
        && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Path), Size, LastWriteTicks);
}

/// <summary>
/// Converts files to UTF-8 text and caches the result so paging through a converted document does not
/// reconvert it on every call. The cache is bounded by total bytes (LRU), shares one conversion between
/// concurrent requests for the same file version, and never caches a failure.
/// </summary>
public sealed class ConversionService
{
    private readonly IReadOnlyList<IContentConverter> _converters;
    private readonly IPdfRenderer? _renderer;
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly long _maxOutputChars;

    private readonly object _gate = new();
    private readonly Dictionary<ConversionKey, Entry> _map = new();
    private readonly LinkedList<ConversionKey> _lru = new(); // most recently used first
    private long _cachedBytes;

    private sealed class Entry
    {
        public required Task<byte[]> Task { get; set; }
        public long Bytes { get; set; }
        public bool Counted { get; set; }
        public required LinkedListNode<ConversionKey> Node { get; init; }
    }

    public ConversionService(IEnumerable<IContentConverter> converters, ConversionOptions options, long maxOutputBytes,
        IPdfRenderer? renderer = null)
    {
        _renderer = renderer;
        _converters = converters.ToList();
        Options = options;
        _maxOutputChars = maxOutputBytes;
    }

    public static ConversionService CreateDefault(ConversionOptions options, long maxOutputBytes) =>
        new(new IContentConverter[] { new PdfTextConverter(), new OoxmlTextConverter() }, options, maxOutputBytes, new PdfiumRenderer());

    /// <summary>True if PDF pages can be delivered as images.</summary>
    public bool CanRender => _renderer is not null;

    public ConversionOptions Options { get; }

    /// <summary>Number of conversions actually run (not served from cache); for tests and diagnostics.</summary>
    public int Conversions => Volatile.Read(ref _conversions);
    private int _conversions;

    public long CachedBytes { get { lock (_gate) return _cachedBytes; } }

    public IContentConverter? Find(FileKind kind) => _converters.FirstOrDefault(c => c.CanConvert(kind));

    /// <summary>
    /// UTF-8 text of <paramref name="source"/>, converted once per file version. The input is copied to memory
    /// first (bounded by <see cref="ConversionOptions.MaxInputBytes"/>), so the caller's file handle may be
    /// closed while a shared conversion is still running.
    /// </summary>
    public async Task<byte[]> GetTextAsync(FileKind kind, ConversionKey key, Stream source, CancellationToken ct)
    {
        var converter = Find(kind) ?? throw new ConversionException("This file type cannot be converted to text.");

        Task<byte[]>? cached = TryGet(key);
        if (cached is null)
        {
            if (key.Size > Options.MaxInputBytes)
                throw new ConversionLimitException(
                    $"The file is {key.Size / (1024 * 1024)} MiB; files over {Options.MaxInputBytes / (1024 * 1024)} MiB are not converted to text.");

            var input = await LoadInputAsync(key, source, ct);
            cached = GetOrStart(key, converter, kind, input);
        }
        return await cached.WaitAsync(ct);
    }

    /// <summary>Copies the (already policy-checked) file into memory, bounded by <see cref="ConversionOptions.MaxInputBytes"/>.</summary>
    private async Task<byte[]> LoadInputAsync(ConversionKey key, Stream source, CancellationToken ct)
    {
        var input = new byte[key.Size];
        source.Position = 0;
        var done = 0;
        while (done < input.Length)
        {
            var n = await source.ReadAsync(input.AsMemory(done), ct);
            if (n == 0) break;
            done += n;
        }
        if (done != input.Length) Array.Resize(ref input, done);
        return input;
    }

    /// <summary>
    /// Renders up to <paramref name="maxPages"/> pages of a PDF starting at zero-based <paramref name="firstPage"/>.
    /// Stops early when the pages so far would exceed <paramref name="maxBytes"/> (always returns at least one page
    /// if any is left). Rendering is serialised and runs under the conversion timeout.
    /// </summary>
    public async Task<RenderResult> RenderPagesAsync(ConversionKey key, Stream source, string mimeType, int firstPage, int maxPages,
        long maxBytes, int maxDimension, int maxDpi, CancellationToken ct)
    {
        var renderer = _renderer ?? throw new ConversionException("This server cannot render PDF pages.");
        if (key.Size > Options.MaxInputBytes)
            throw new ConversionLimitException(
                $"The file is {key.Size / (1024 * 1024)} MiB; files over {Options.MaxInputBytes / (1024 * 1024)} MiB are not rendered.");
        var input = await LoadInputAsync(key, source, ct);

        // PDFium cannot be cancelled and is not re-entrant: wait for the gate, and fail fast instead of queueing behind a hung render.
        if (!await _renderGate.WaitAsync(Options.Timeout, ct))
            throw new ConversionLimitException("The PDF renderer is busy; try again shortly.");
        var work = Task.Run(() =>
        {
            try
            {
                var count = renderer.GetPageCount(input);
                var pages = new List<RenderedPage>();
                long bytes = 0;
                for (var p = firstPage; p < count && pages.Count < maxPages; p++)
                {
                    var page = renderer.Render(input, p, mimeType, maxDimension, maxDpi);
                    if (pages.Count > 0 && bytes + page.Data.Length > maxBytes) break;
                    if (page.Data.Length > maxBytes)
                        throw new ConversionLimitException("A rendered page is larger than the per-call cap of this server.");
                    pages.Add(page);
                    bytes += page.Data.Length;
                }
                return new RenderResult(count, pages);
            }
            finally { _renderGate.Release(); }
        });
        try
        {
            return await work.WaitAsync(Options.Timeout, ct);
        }
        catch (TimeoutException)
        {
            _ = work.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
            throw new ConversionLimitException($"Rendering timed out after {(int)Options.Timeout.TotalSeconds} s.");
        }
    }

    private Task<byte[]>? TryGet(ConversionKey key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var e)) return null;
            _lru.Remove(e.Node);
            _lru.AddFirst(e.Node);
            return e.Task;
        }
    }

    private Task<byte[]> GetOrStart(ConversionKey key, IContentConverter converter, FileKind kind, byte[] input)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing.Node);
                _lru.AddFirst(existing.Node);
                return existing.Task;
            }
            var node = _lru.AddFirst(key);
            var entry = new Entry { Task = null!, Node = node };
            _map[key] = entry;
            // Task.Run: the bookkeeping continuation needs this lock, which is held until the entry is registered.
            entry.Task = Task.Run(() => RunAsync(key, converter, kind, input));
            return entry.Task;
        }
    }

    private async Task<byte[]> RunAsync(ConversionKey key, IContentConverter converter, FileKind kind, byte[] input)
    {
        try
        {
            Interlocked.Increment(ref _conversions);
            var bytes = await ConvertWithTimeoutAsync(converter, kind, input);
            Complete(key, bytes.Length);
            return bytes;
        }
        catch
        {
            Remove(key);
            throw;
        }
    }

    private async Task<byte[]> ConvertWithTimeoutAsync(IContentConverter converter, FileKind kind, byte[] input)
    {
        using var cts = new CancellationTokenSource();
        var budget = new ConversionBudget(Options.MaxExpandedBytes, _maxOutputChars, cts.Token);
        var work = Task.Run(() =>
        {
            using var ms = new MemoryStream(input, writable: false);
            return Encoding.UTF8.GetBytes(converter.Convert(ms, kind, budget));
        });
        try
        {
            return await work.WaitAsync(Options.Timeout);
        }
        catch (TimeoutException)
        {
            cts.Cancel(); // converters check the token between pages/entries; a truly stuck parser cannot be killed in-process
            _ = work.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default); // observe the eventual failure
            throw new ConversionLimitException($"Conversion timed out after {(int)Options.Timeout.TotalSeconds} s.");
        }
        catch (OperationCanceledException)
        {
            throw new ConversionLimitException("Conversion was cancelled.");
        }
    }

    private void Complete(ConversionKey key, long bytes)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var e)) return;
            if (bytes > Options.CacheBytes) { RemoveLocked(key, e); return; } // served, but too big to keep
            e.Bytes = bytes;
            e.Counted = true;
            _cachedBytes += bytes;
            // Evict least recently used finished entries (never the one just added) until within budget.
            for (var node = _lru.Last; node is not null && _cachedBytes > Options.CacheBytes;)
            {
                var prev = node.Previous;
                if (!node.Value.Equals(key) && _map.TryGetValue(node.Value, out var victim) && victim.Counted)
                    RemoveLocked(node.Value, victim);
                node = prev;
            }
        }
    }

    private void Remove(ConversionKey key)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var e)) RemoveLocked(key, e);
        }
    }

    private void RemoveLocked(ConversionKey key, Entry e)
    {
        _map.Remove(key);
        _lru.Remove(e.Node);
        if (e.Counted) _cachedBytes -= e.Bytes;
    }
}
