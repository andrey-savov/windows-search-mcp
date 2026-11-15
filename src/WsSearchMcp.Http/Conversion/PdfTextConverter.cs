using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace WsSearchMcp.Http.Conversion;

/// <summary>Text extraction from PDF with PdfPig (managed, no native code). No OCR: scanned pages yield nothing.</summary>
public sealed class PdfTextConverter : IContentConverter
{
    public bool CanConvert(FileKind kind) => kind == FileKind.Pdf;

    public string Describe(FileKind kind) =>
        "Text extracted from the PDF page by page; layout, images and scanned (image-only) pages are not included.";

    public string Convert(Stream input, FileKind kind, ConversionBudget budget)
    {
        var sb = new StringBuilder();
        var pagesWithText = 0;
        try
        {
            using var doc = PdfDocument.Open(input, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            for (var n = 1; n <= doc.NumberOfPages; n++)
            {
                budget.Token.ThrowIfCancellationRequested(); // PdfPig has no token; check between pages
                var text = ContentOrderTextExtractor.GetText(doc.GetPage(n)).Trim();
                sb.Append("--- page ").Append(n).Append(" ---\n");
                if (text.Length > 0) { sb.Append(text).Append('\n'); pagesWithText++; }
                budget.CheckOutput(sb.Length);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (ConversionException) { throw; }
        catch (Exception ex)
        {
            var encrypted = ex.GetType().Name.Contains("Encrypt", StringComparison.OrdinalIgnoreCase)
                            || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase);
            throw new ConversionException(encrypted
                ? "The PDF is encrypted or password-protected, so its text cannot be extracted."
                : "The PDF could not be parsed (it may be corrupt).", ex);
        }

        if (pagesWithText == 0)
            throw new ConversionException("The PDF has no extractable text; it is probably a scan (image-only) and would need OCR.");
        return sb.ToString().TrimEnd();
    }
}
