using PDFtoImage;
using PdfiumApi = PDFtoImage.Conversion;

namespace WsSearchMcp.Http.Conversion;

/// <summary>One rendered PDF page.</summary>
public sealed record RenderedPage(int Page, byte[] Data, string MimeType, int Width, int Height);

/// <summary>Draws PDF pages as images, so a client with a vision model can see layout, figures and scanned pages.</summary>
public interface IPdfRenderer
{
    /// <exception cref="ConversionException">The PDF cannot be opened (corrupt or password-protected).</exception>
    int GetPageCount(byte[] pdf);

    /// <param name="pageIndex">Zero-based page.</param>
    /// <param name="mimeType">image/png, image/jpeg or image/webp.</param>
    /// <param name="maxDimension">Longest edge in pixels.</param>
    /// <param name="maxDpi">Upper bound on the rendering resolution (small pages are not blown up beyond this).</param>
    RenderedPage Render(byte[] pdf, int pageIndex, string mimeType, int maxDimension, int maxDpi);
}

/// <summary>PDFium (through PDFtoImage). PDFium is a native, non-thread-safe library, so every call is serialised.</summary>
public sealed class PdfiumRenderer : IPdfRenderer
{
    private static readonly object Gate = new();

    public int GetPageCount(byte[] pdf)
    {
        lock (Gate)
        {
            try { return PdfiumApi.GetPageCount(pdf); }
            catch (Exception ex) { throw Wrap(ex); }
        }
    }

    public RenderedPage Render(byte[] pdf, int pageIndex, string mimeType, int maxDimension, int maxDpi)
    {
        lock (Gate)
        {
            try
            {
                var size = PdfiumApi.GetPageSize(pdf, pageIndex);
                if (size.Width <= 0 || size.Height <= 0)
                    throw new ConversionException("The PDF page has no size.");

                // Page sizes are in points (1/72 in). Render at up to maxDpi, never beyond maxDimension pixels.
                var scale = Math.Min(maxDpi / 72.0, maxDimension / (double)Math.Max(size.Width, size.Height));
                var width = Math.Max(1, (int)Math.Round(size.Width * scale));
                var height = Math.Max(1, (int)Math.Round(size.Height * scale));
                var options = new RenderOptions(Dpi: 72, Width: width, Height: height);

                using var ms = new MemoryStream();
                switch (mimeType)
                {
                    case "image/png": PdfiumApi.SavePng(ms, pdf, pageIndex, null, options); break;
                    case "image/jpeg": PdfiumApi.SaveJpeg(ms, pdf, pageIndex, null, options); break;
                    case "image/webp": PdfiumApi.SaveWebp(ms, pdf, pageIndex, null, options); break;
                    default: throw new ConversionException("Unsupported image type for rendering.");
                }
                return new RenderedPage(pageIndex + 1, ms.ToArray(), mimeType, width, height);
            }
            catch (ConversionException) { throw; }
            catch (Exception ex) { throw Wrap(ex); }
        }
    }

    private static ConversionException Wrap(Exception ex)
    {
        var protectedPdf = ex.GetType().Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                           || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase);
        return new ConversionException(protectedPdf
            ? "The PDF is password-protected, so its pages cannot be rendered."
            : "The PDF could not be rendered (it may be corrupt).", ex);
    }
}
