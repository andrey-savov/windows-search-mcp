using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WsSearchMcp.Files;
using WsSearchMcp.Security;

namespace WsSearchMcp.Http;

/// <summary>
/// The HTTPS-only <c>get_file</c> tool. It lives in this project (not Core), so the stdio server never
/// exposes it. The Windows Search index only supplies names and metadata; the content is read from
/// disk here, as the signed-in Windows user, after the permission, index and deny-list checks.
/// </summary>
[McpServerToolType]
public sealed class FileTool
{
    private static readonly FileExtensionContentTypeProvider MimeTypes = new();
    private static readonly HashSet<string> ImageMimes = new(StringComparer.OrdinalIgnoreCase)
        { "image/png", "image/jpeg", "image/gif", "image/webp" };

    private readonly IHttpContextAccessor _http;
    private readonly McpHttpServerOptions _options;
    private readonly FileAccessPolicy _policy;
    private readonly IndexedFileResolver _resolver;
    private readonly ILogger<FileTool> _log;

    public FileTool(IHttpContextAccessor http, McpHttpServerOptions options, FileAccessPolicy policy,
        IndexedFileResolver resolver, ILogger<FileTool> log)
    {
        _http = http;
        _options = options;
        _policy = policy;
        _resolver = resolver;
        _log = log;
    }

    [McpServerTool(Name = "get_file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Retrieve the contents of a file on the server by its Windows Search path. Pass the path exactly as " +
        "find_files returned it (System.ItemPathDisplay). Returns a JSON metadata block (size, mimeType, " +
        "encoding, offset, returned, nextOffset, truncated) followed by the content: text files as text, " +
        "images as images, everything else as a base64 blob. Large files: pass a modest 'length' (for example " +
        "100000) and keep calling with offset = nextOffset until nextOffset is null; AI clients cannot take " +
        "megabytes in one reply. Only files that are in the index are available, and hidden/system files, " +
        "dot-prefixed names (.git, .ssh, ...), AppData/ProgramData/Windows and key/credential files are never " +
        "served. Requires a client that was granted file access.")]
    public async Task<IEnumerable<ContentBlock>> GetFile(
        [Description("Absolute path exactly as returned by find_files (System.ItemPathDisplay), e.g. C:\\\\Users\\\\me\\\\Documents\\\\notes.txt")]
            string path,
        [Description("Byte offset to start reading at. Default 0. For continuation use the nextOffset from the previous reply.")]
            long offset = 0,
        [Description("Maximum bytes to return. Default 0 = as much as the server allows per call.")]
            long length = 0,
        [Description("'auto' (default: text as text, images as images, else base64), 'text' (force text), or 'base64' (force a binary blob).")]
            string format = "auto",
        CancellationToken cancellationToken = default)
    {
        var client = _http.HttpContext?.Items[McpHttpServer.ClientItemKey] as ClientRecord
            ?? throw new McpException("Not authenticated.");
        if (!client.HasPermission(ClientPermissions.Files))
        {
            _log.LogWarning("get_file refused for client '{Client}': no file permission", client.Name);
            throw new McpException("This client was not granted file access. Ask the server owner to mint a client with --files.");
        }

        if (offset < 0) throw new McpException("offset must be 0 or greater.");
        if (length < 0) throw new McpException("length must be 0 or greater.");
        format = (format ?? "auto").Trim().ToLowerInvariant();
        if (format is not ("auto" or "text" or "base64"))
            throw new McpException("format must be 'auto', 'text' or 'base64'.");

        // 1. Deny list on the path text — before the index is consulted, so denied paths never reveal whether they exist.
        Deny(client, path, _policy.CheckPathText(path));

        // 2. The index is the allowlist and supplies the canonical path that is opened.
        IndexedFile indexed;
        try
        {
            indexed = _resolver.Resolve(path)
                ?? throw new McpException("File not found in the Windows Search index. Pass the path exactly as find_files returned it.");
        }
        catch (McpException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Index lookup failed for client '{Client}'", client.Name);
            throw new McpException("The Windows Search index could not be queried: " + ex.Message);
        }
        if (indexed.IsDirectory) throw new McpException("That path is a directory, not a file.");
        Deny(client, indexed.Path, _policy.CheckPathText(indexed.Path));

        // 3. Open first, then judge where the handle really points (symlinks/junctions in any folder).
        FileStream stream;
        try
        {
            stream = new FileStream(indexed.Path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0,
            });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new McpException("The file is in the index but no longer exists on disk.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new McpException("The file could not be opened: " + ex.Message);
        }

        using (stream)
        {
            Deny(client, indexed.Path, _policy.CheckAttributes(indexed.Path));
            var finalPath = NativeFilePath.GetFinalPath(stream.SafeFileHandle);
            Deny(client, finalPath, _policy.Check(finalPath, stream));

            return await ReadAsync(client, indexed, stream, offset, length, format, cancellationToken);
        }
    }

    private async Task<IEnumerable<ContentBlock>> ReadAsync(ClientRecord client, IndexedFile indexed,
        FileStream stream, long offset, long length, string format, CancellationToken ct)
    {
        var size = stream.Length;
        var cap = _options.MaxFileBytes;

        // Encoding is judged from the start of the file so every chunk of one file is treated alike.
        var head = new byte[FileAccessPolicy.KeySniffBytes];
        stream.Position = 0;
        var headLen = await ReadFullyAsync(stream, head, 0, (int)Math.Min(head.Length, size), ct);
        var encoding = FileChunking.Detect(head.AsSpan(0, headLen), out _);

        var wanted = length == 0 ? cap : Math.Min(length, cap);
        var count = (int)Math.Min(Math.Min(wanted, Math.Max(0, size - offset)), Array.MaxLength);

        if (encoding is FileTextEncoding.Utf16Le or FileTextEncoding.Utf16Be && offset % 2 != 0)
            throw new McpException("This file is UTF-16; offset must be an even number of bytes (use the nextOffset from a previous reply).");

        var data = new byte[count];
        stream.Position = offset;
        var read = await ReadFullyAsync(stream, data, 0, count, ct);
        var atEof = offset + read >= size;

        var name = Path.GetFileName(indexed.Path);
        MimeTypes.TryGetContentType(name, out var mime);

        // UTF-16 text legitimately contains NUL bytes; for UTF-8 a NUL in the chunk means binary data.
        var looksText = encoding switch
        {
            FileTextEncoding.Binary => false,
            FileTextEncoding.Utf8 => data.AsSpan(0, read).IndexOf((byte)0) < 0,
            _ => true,
        };
        var asText = format == "text"
            || (format == "auto" && looksText && !(mime is not null && ImageMimes.Contains(mime)));
        var asImage = format == "auto" && !asText && offset == 0 && atEof && mime is not null && ImageMimes.Contains(mime);

        var consumed = read;
        string? text = null;
        if (asText)
        {
            var textEncoding = encoding == FileTextEncoding.Binary ? FileTextEncoding.Utf8 : encoding;
            consumed = FileChunking.TrimToCharacterBoundary(data, read, textEncoding, atEof);
            if (consumed == 0 && read > 0)
                throw new McpException("length is too small to hold a single character; use at least 4 bytes.");
            text = FileChunking.Decode(data.AsSpan(0, consumed), textEncoding, atFileStart: offset == 0);
        }

        var next = offset + consumed;
        var metadata = JsonSerializer.Serialize(new
        {
            path = indexed.Path,
            size,
            modified = indexed.Modified,
            mimeType = mime ?? (asText ? "text/plain" : "application/octet-stream"),
            encoding = asText ? FileChunking.Label(encoding == FileTextEncoding.Binary ? FileTextEncoding.Utf8 : encoding)
                              : asImage ? "image" : "base64",
            offset,
            returned = consumed,
            nextOffset = next < size ? (long?)next : null,
            truncated = next < size,
            maxBytesPerCall = cap,
        });

        _log.LogInformation("get_file: client '{Client}' read {Bytes} bytes at {Offset} of {Path}", client.Name, consumed, offset, indexed.Path);

        var blocks = new List<ContentBlock> { new TextContentBlock { Text = metadata } };
        if (asText)
        {
            blocks.Add(new TextContentBlock { Text = text! });
        }
        else if (asImage)
        {
            blocks.Add(ImageContentBlock.FromBytes(data.AsMemory(0, consumed), mime!));
        }
        else
        {
            blocks.Add(new EmbeddedResourceBlock
            {
                Resource = BlobResourceContents.FromBytes(data.AsMemory(0, consumed),
                    new Uri(indexed.Path).AbsoluteUri, mime ?? "application/octet-stream"),
            });
        }
        return blocks;
    }

    private void Deny(ClientRecord client, string path, PolicyDecision decision)
    {
        if (decision.Allowed) return;
        // The category goes to the client; the path and detail stay in the server log.
        _log.LogWarning("get_file denied for client '{Client}': {Category} ({Detail}) path={Path}",
            client.Name, decision.Category, decision.Detail, path);
        throw new McpException($"Access to this file is denied by server policy ({decision.Category}).");
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, int start, int count, CancellationToken ct)
    {
        var done = 0;
        while (done < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(start + done, count - done), ct);
            if (n == 0) break;
            done += n;
        }
        return done;
    }
}
