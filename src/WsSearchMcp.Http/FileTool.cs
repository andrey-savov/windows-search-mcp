using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WsSearchMcp.Files;
using WsSearchMcp.Http.Conversion;
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
    private readonly IHttpContextAccessor _http;
    private readonly McpHttpServerOptions _options;
    private readonly FileAccessPolicy _policy;
    private readonly IndexedFileResolver _resolver;
    private readonly ConversionService _conversion;
    private readonly ILogger<FileTool> _log;

    public FileTool(IHttpContextAccessor http, McpHttpServerOptions options, FileAccessPolicy policy,
        IndexedFileResolver resolver, ConversionService conversion, ILogger<FileTool> log)
    {
        _http = http;
        _options = options;
        _policy = policy;
        _resolver = resolver;
        _conversion = conversion;
        _log = log;
    }

    [McpServerTool(Name = "get_file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Retrieve the contents of a file on the server by its Windows Search path. Pass the path exactly as " +
        "find_files returned it (System.ItemPathDisplay). Returns a JSON metadata block (size, mimeType, " +
        "deliveredAs, encoding, offset, returned, nextOffset, truncated, note) followed by the content. " +
        "WHAT YOU RECEIVE DEPENDS ON 'accept': text files always come back as text. Other files come back in " +
        "their own format only if you list that MIME type in 'accept' (images as images, PDFs and the rest as a " +
        "binary attachment); otherwise PDF, Word (docx), Excel (xlsx) and PowerPoint (pptx) files are converted to " +
        "extracted plain text, and other types are refused with an explanation. Only list types your model can " +
        "really read: passing application/pdf returns the raw PDF as a binary attachment (a scanned PDF has no " +
        "text layer). FALLBACK: if your client reports that it cannot read the returned content (for example " +
        "'this model does not support pdf input' or an unsupported attachment/image error), call get_file again " +
        "with the same path and accept=\"text/*\"; the server then returns extracted text. " +
        "Large files: pass a modest 'length' (for example 100000) and keep calling with offset = nextOffset until " +
        "nextOffset is null; for converted files offset/length count bytes of the extracted text. Images are " +
        "always delivered whole. Only files that are in the index are available, and hidden/system files, " +
        "dot-prefixed names (.git, .ssh, ...), AppData/ProgramData/Windows and key/credential files are never " +
        "served. Requires a client that was granted file access.")]
    public async Task<IEnumerable<ContentBlock>> GetFile(
        [Description("Absolute path exactly as returned by find_files (System.ItemPathDisplay), e.g. C:\\Users\\me\\Documents\\notes.txt")]
            string path,
        [Description("Byte offset to start reading at. Default 0. For continuation use the nextOffset from the previous reply.")]
            long offset = 0,
        [Description("Maximum bytes to return. Default 0 = as much as the server allows per call. Not allowed for images (always whole).")]
            long length = 0,
        [Description("'auto' (default: negotiate using 'accept'), 'text' (force the raw bytes decoded as text), or 'base64' (force a binary attachment).")]
            string format = "auto",
        [Description("Comma-separated MIME types your client/model can read natively, with wildcards, e.g. \"text/*,image/*,application/pdf\" " +
                     "or \"*/*\". Text is always accepted. Default \"text/*\": non-text files are converted to text where possible " +
                     "(PDF, docx, xlsx, pptx). If the client says it cannot read what you got, retry with accept=\"text/*\".")]
            string accept = AcceptList.Default,
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
        AcceptList acceptList;
        try { acceptList = AcceptList.Parse(accept); }
        catch (FormatException ex) { throw new McpException(ex.Message); }

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

            return await ReadAsync(client, indexed, stream, offset, length, format, acceptList, cancellationToken);
        }
    }

    private async Task<IEnumerable<ContentBlock>> ReadAsync(ClientRecord client, IndexedFile indexed,
        FileStream stream, long offset, long length, string format, AcceptList accept, CancellationToken ct)
    {
        var size = stream.Length;
        var cap = _options.MaxFileBytes;
        var name = Path.GetFileName(indexed.Path);
        MimeTypes.TryGetContentType(name, out var extensionMime);

        // What the file is and how it is encoded are judged from its start, so every chunk of one file is treated alike.
        var head = new byte[FileAccessPolicy.KeySniffBytes];
        stream.Position = 0;
        var headLen = await ReadFullyAsync(stream, head, 0, (int)Math.Min(head.Length, size), ct);
        var encoding = FileChunking.Detect(head.AsSpan(0, headLen), out _);
        var kind = FileClassifier.Classify(stream, head.AsSpan(0, headLen), size, _conversion.Options.MaxInputBytes);
        var mime = FileClassifier.MimeFor(kind, extensionMime);
        var textLike = kind == FileKind.Unknown && encoding != FileTextEncoding.Binary;

        // Negotiate: the file as text, natively (if the client accepts its type), as extracted text, or refuse.
        var delivery = ContentNegotiator.Decide(kind, textLike, mime, format, accept, _conversion.Find(kind) is not null);

        if (delivery == Delivery.Image && (offset > 0 || (length > 0 && length < size) || size > cap))
            throw new McpException("Images are delivered whole, so offset and length cannot be used" +
                (size > cap ? $" (this image is {size} bytes, over the server cap of {cap} bytes)" : "") +
                ". Use format=base64 to page the raw bytes.");

        byte[]? converted = null;
        if (delivery == Delivery.ConvertedText)
        {
            try
            {
                var key = new ConversionKey(indexed.Path, size, File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks);
                converted = await _conversion.GetTextAsync(kind, key, stream, ct);
            }
            catch (ConversionException ex)
            {
                _log.LogWarning("get_file: conversion of a {Kind} failed for client '{Client}': {Reason} path={Path}",
                    kind, client.Name, ex.Message, indexed.Path);
                throw new McpException("The file could not be converted to text: " + ex.Message);
            }
        }
        var total = converted?.Length ?? size;

        if (delivery == Delivery.Text && converted is null
            && encoding is FileTextEncoding.Utf16Le or FileTextEncoding.Utf16Be && offset % 2 != 0)
            throw new McpException("This file is UTF-16; offset must be an even number of bytes (use the nextOffset from a previous reply).");

        var wanted = length == 0 ? cap : Math.Min(length, cap);
        var count = (int)Math.Min(Math.Min(wanted, Math.Max(0, total - offset)), Array.MaxLength);
        var data = new byte[count];
        int read;
        if (converted is not null)
        {
            if (count > 0) Array.Copy(converted, offset, data, 0, count);
            read = count;
        }
        else
        {
            stream.Position = offset;
            read = await ReadFullyAsync(stream, data, 0, count, ct);
        }
        var atEof = offset + read >= total;

        // UTF-16 text legitimately contains NUL bytes; for UTF-8 a NUL in the chunk means binary data.
        if (delivery == Delivery.Text && format == "auto" && converted is null
            && encoding == FileTextEncoding.Utf8 && data.AsSpan(0, read).IndexOf((byte)0) >= 0)
            delivery = ContentNegotiator.DecideBinary(kind, mime, accept);

        var asText = delivery is Delivery.Text or Delivery.ConvertedText;
        var textEncoding = converted is not null || encoding == FileTextEncoding.Binary ? FileTextEncoding.Utf8 : encoding;

        var consumed = read;
        string? text = null;
        if (asText)
        {
            consumed = FileChunking.TrimToCharacterBoundary(data, read, textEncoding, atEof);
            if (consumed == 0 && read > 0)
                throw new McpException("length is too small to hold a single character; use at least 4 bytes.");
            text = FileChunking.Decode(data.AsSpan(0, consumed), textEncoding, atFileStart: offset == 0);
        }

        var next = offset + consumed;
        var meta = new Dictionary<string, object?>
        {
            ["path"] = indexed.Path,
            ["size"] = total,
        };
        if (converted is not null) meta["sourceSize"] = size;
        meta["modified"] = indexed.Modified;
        meta["mimeType"] = delivery switch
        {
            Delivery.ConvertedText => "text/plain",
            Delivery.Text => TextMime(mime),
            _ => mime ?? "application/octet-stream",
        };
        meta["encoding"] = asText ? FileChunking.Label(textEncoding) : delivery == Delivery.Image ? "image" : "base64";
        meta["offset"] = offset;
        meta["returned"] = consumed;
        meta["nextOffset"] = next < total ? (long?)next : null;
        meta["truncated"] = next < total;
        meta["maxBytesPerCall"] = cap;
        meta["accept"] = accept.Normalized;
        meta["deliveredAs"] = delivery switch
        {
            Delivery.Text => "text",
            Delivery.Image => "image",
            Delivery.Blob => "blob",
            _ => "converted-text",
        };
        if (converted is not null)
        {
            meta["convertedFrom"] = mime;
            meta["note"] = _conversion.Find(kind)!.Describe(kind);
        }
        else if (delivery == Delivery.Blob && FileClassifier.IsConvertible(kind))
        {
            meta["note"] = $"If your client cannot read {mime} attachments, call get_file again with accept=\"text/*\" to receive extracted text" +
                           (kind == FileKind.Pdf ? " (a scanned PDF has no text layer and cannot be converted)." : ".");
        }

        _log.LogInformation("get_file: client '{Client}' read {Bytes} bytes at {Offset} of {Path} as {Delivery}",
            client.Name, consumed, offset, indexed.Path, delivery);

        var blocks = new List<ContentBlock> { new TextContentBlock { Text = JsonSerializer.Serialize(meta) } };
        switch (delivery)
        {
            case Delivery.Text or Delivery.ConvertedText:
                blocks.Add(new TextContentBlock { Text = text! });
                break;
            case Delivery.Image:
                blocks.Add(ImageContentBlock.FromBytes(data.AsMemory(0, consumed), mime!));
                break;
            default:
                blocks.Add(new EmbeddedResourceBlock
                {
                    Resource = BlobResourceContents.FromBytes(data.AsMemory(0, consumed),
                        new Uri(indexed.Path).AbsoluteUri, mime ?? "application/octet-stream"),
                });
                break;
        }
        return blocks;
    }

    /// <summary>The MIME type to report for content delivered as text: the extension's own if it is textual, else text/plain.</summary>
    private static string TextMime(string? mime)
    {
        if (mime is null) return "text/plain";
        if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return mime;
        string[] textual = { "json", "xml", "javascript", "yaml", "toml", "csv", "x-sh", "x-powershell" };
        return textual.Any(t => mime.Contains(t, StringComparison.OrdinalIgnoreCase)) ? mime : "text/plain";
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
