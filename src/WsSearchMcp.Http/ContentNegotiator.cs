using ModelContextProtocol;

namespace WsSearchMcp.Http;

/// <summary>How get_file will deliver a file to this client.</summary>
public enum Delivery
{
    /// <summary>The file's own bytes decoded as text.</summary>
    Text,
    /// <summary>A complete image as an image content block.</summary>
    Image,
    /// <summary>The raw bytes as a binary resource labelled with the file's real MIME type.</summary>
    Blob,
    /// <summary>Text extracted from the file (PDF, docx, xlsx, pptx).</summary>
    ConvertedText,
}

/// <summary>
/// Chooses the best form of a file the client said it can read. Preference order for
/// <c>format=auto</c>: the file as text → the file natively (image block / binary resource) when its MIME
/// type is accepted → extracted text when it can be converted → a clear error.
/// </summary>
public static class ContentNegotiator
{
    public static Delivery Decide(FileKind kind, bool textLike, string? mime, string format, AcceptList accept, bool canConvert)
    {
        // Explicit formats are an escape hatch that bypasses negotiation.
        if (format == "text") return Delivery.Text;
        if (format == "base64") return Delivery.Blob;

        if (kind == FileKind.Unknown && textLike) return Delivery.Text; // text is always acceptable

        mime ??= "application/octet-stream";
        if (FileClassifier.IsImage(kind))
        {
            if (accept.Accepts(mime)) return Delivery.Image;
            throw NotDeliverable(kind, mime, accept);
        }
        if (FileClassifier.IsConvertible(kind))
        {
            if (accept.Accepts(mime)) return Delivery.Blob;
            if (canConvert) return Delivery.ConvertedText;
            throw NotDeliverable(kind, mime, accept);
        }
        return DecideBinary(kind, mime, accept);
    }

    /// <summary>Delivery for a file that turned out to be binary data (nothing to convert it to).</summary>
    public static Delivery DecideBinary(FileKind kind, string? mime, AcceptList accept)
    {
        mime ??= "application/octet-stream";
        if (accept.Accepts(mime)) return Delivery.Blob;
        throw NotDeliverable(kind, mime, accept);
    }

    public static McpException NotDeliverable(FileKind kind, string mime, AcceptList accept)
    {
        var what = $"a {FileClassifier.Label(kind)} ({mime})";
        var head = $"This file is {what} and cannot be delivered with accept=\"{accept.Normalized}\". ";
        var tail = FileClassifier.IsImage(kind)
            ? $"Images cannot be converted to text. Pass accept including \"{mime}\" (or \"image/*\") if your client can display images."
            : kind == FileKind.Ole2
                ? $"Legacy and password-protected Office files cannot be converted to text. Pass accept including \"{mime}\" if your client can read it natively."
                : $"Pass accept including \"{mime}\" if your client can read it natively, or format=base64 for clients that accept binary attachments.";
        return new McpException(head + tail);
    }
}
