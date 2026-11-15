using System.IO.Compression;
using WsSearchMcp.Http.Conversion;

namespace WsSearchMcp.Http;

/// <summary>What a file really is, judged from its content (magic bytes), not its extension.</summary>
public enum FileKind
{
    Unknown,
    Pdf,
    Png,
    Jpeg,
    Gif,
    Webp,
    OoxmlWord,
    OoxmlExcel,
    OoxmlPowerPoint,
    /// <summary>A zip that is not an Office document.</summary>
    Zip,
    /// <summary>OLE2 compound file: legacy Office (.doc/.xls/.ppt) or a password-protected OOXML document.</summary>
    Ole2,
}

public static class FileClassifier
{
    public static bool IsImage(FileKind k) => k is FileKind.Png or FileKind.Jpeg or FileKind.Gif or FileKind.Webp;
    public static bool IsOoxml(FileKind k) => k is FileKind.OoxmlWord or FileKind.OoxmlExcel or FileKind.OoxmlPowerPoint;
    public static bool IsConvertible(FileKind k) => k == FileKind.Pdf || IsOoxml(k);

    /// <summary>True for formats recognised by content; such files are never "text" even if their head happens to be ASCII.</summary>
    public static bool IsKnownBinary(FileKind k) => k != FileKind.Unknown;

    /// <summary>
    /// Classifies a file from its first bytes. A zip is opened (central directory only) to tell Office
    /// documents from other archives; <paramref name="stream"/> is left open and repositioned to 0.
    /// </summary>
    public static FileKind Classify(Stream stream, ReadOnlySpan<byte> head, long size, long maxInspectBytes)
    {
        if (head.StartsWith("%PDF-"u8)) return FileKind.Pdf;
        if (head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return FileKind.Png;
        if (head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return FileKind.Jpeg;
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return FileKind.Gif;
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8)) return FileKind.Webp;
        if (head.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })) return FileKind.Ole2;

        if (head.StartsWith("PK\x03\x04"u8) || head.StartsWith("PK\x05\x06"u8))
        {
            if (size > maxInspectBytes) return FileKind.Zip;
            try
            {
                stream.Position = 0;
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                var budget = new ConversionBudget(8L * 1024 * 1024, long.MaxValue, CancellationToken.None);
                var pkg = OoxmlPackage.Open(zip, budget);
                return pkg.Kind switch
                {
                    OoxmlKind.Word => FileKind.OoxmlWord,
                    OoxmlKind.Excel => FileKind.OoxmlExcel,
                    OoxmlKind.PowerPoint => FileKind.OoxmlPowerPoint,
                    _ => FileKind.Zip,
                };
            }
            catch (Exception ex) when (ex is InvalidDataException or ConversionException or IOException)
            {
                return FileKind.Zip;
            }
            finally
            {
                stream.Position = 0;
            }
        }
        return FileKind.Unknown;
    }

    /// <summary>The MIME type to report: content wins over the extension for images and PDFs.</summary>
    public static string? MimeFor(FileKind kind, string? extensionMime) => kind switch
    {
        FileKind.Pdf => "application/pdf",
        FileKind.Png => "image/png",
        FileKind.Jpeg => "image/jpeg",
        FileKind.Gif => "image/gif",
        FileKind.Webp => "image/webp",
        FileKind.OoxmlWord => OoxmlOr(extensionMime, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        FileKind.OoxmlExcel => OoxmlOr(extensionMime, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        FileKind.OoxmlPowerPoint => OoxmlOr(extensionMime, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
        FileKind.Zip => "application/zip", // the extension map says application/x-zip-compressed; use the standard name clients know
        FileKind.Ole2 => extensionMime is { } o && o.StartsWith("application/", StringComparison.OrdinalIgnoreCase) ? o : "application/x-ole-storage",
        _ => extensionMime,
    };

    private static string OoxmlOr(string? ext, string fallback) =>
        ext is not null && (ext.StartsWith("application/vnd.openxmlformats-officedocument", StringComparison.OrdinalIgnoreCase)
                            || ext.StartsWith("application/vnd.ms-", StringComparison.OrdinalIgnoreCase))
            ? ext : fallback;

    public static string Label(FileKind k) => k switch
    {
        FileKind.Pdf => "PDF document",
        FileKind.OoxmlWord => "Word document",
        FileKind.OoxmlExcel => "Excel workbook",
        FileKind.OoxmlPowerPoint => "PowerPoint presentation",
        FileKind.Ole2 => "legacy or password-protected Office file",
        FileKind.Zip => "zip archive",
        _ when IsImage(k) => "image",
        _ => "binary file",
    };
}
