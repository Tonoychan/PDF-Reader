using PdfReader.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace PdfReader.App.Pdf;

/// <summary>
/// Pulls positioned words and picture areas out of a PDF, page by page, in reading order.
/// Positions are in points from the page's top-left corner (the visible crop box), which is
/// how the page renderer draws them, so highlights line up with the rendered page.
/// </summary>
public static class PdfTextExtractor
{
    public static BookDocument Extract(string path, IProgress<(int Page, int Total)>? progress, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(path);
        var builder = new BookBuilder();
        int total = pdf.NumberOfPages;

        for (int number = 1; number <= total; number++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                builder.AddPage(ReadPage(pdf.GetPage(number), number));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // One broken page should not stop the whole book from loading.
            }
            progress?.Report((number, total));
        }

        return builder.Build(path, total);
    }

    private static PageInput ReadPage(Page page, int number)
    {
        var crop = page.CropBox.Bounds;
        float width = (float)Math.Abs(crop.Width), height = (float)Math.Abs(crop.Height);
        double left = Math.Min(crop.Left, crop.Right), top = Math.Max(crop.Top, crop.Bottom);

        BoxF ToBox(PdfRectangle r)
        {
            double l = Math.Min(r.Left, r.Right), t = Math.Max(r.Top, r.Bottom);
            return new BoxF((float)(l - left), (float)(top - t), (float)Math.Abs(r.Width), (float)Math.Abs(r.Height));
        }

        var input = new PageInput { Number = number, Width = width, Height = height };

        try
        {
            foreach (var image in page.GetImages())
                input.Images.Add(ToBox(image.Bounds));
        }
        catch
        {
            // Pictures are only needed for the text view; ignore broken image data.
        }

        var words = page.GetWords(NearestNeighbourWordExtractor.Instance)
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();
        if (words.Count == 0) return input;

        TextToken Token(Word w) => new(w.Text, ToBox(w.BoundingBox), FontSize(w), IsBold(w));

        try
        {
            // Docstrum groups words into lines and blocks (≈ paragraphs); the detector orders them (handles columns).
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
            foreach (var block in ordered)
                input.Blocks.Add(block.TextLines.Select(l => new TextLineInput(l.Words.Select(Token))).ToList());
        }
        catch (Exception)
        {
            // Layout analysis failed on an odd page: group words into lines by their baseline.
            input.Blocks.Clear();
            var lines = words
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3))
                .OrderByDescending(g => g.Key)
                .Select(g => new TextLineInput(g.OrderBy(w => w.BoundingBox.Left).Select(Token)))
                .ToList();
            input.Blocks.Add(lines);
        }

        return input;
    }

    private static float FontSize(Word w)
    {
        double sum = 0;
        int n = 0;
        foreach (var l in w.Letters)
        {
            if (l.PointSize > 0) { sum += l.PointSize; n++; }
        }
        return n == 0 ? 0 : (float)(sum / n);
    }

    private static bool IsBold(Word w)
    {
        string name = w.FontName ?? "";
        return name.Contains("Bold", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Heavy", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Semibold", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Medium", StringComparison.OrdinalIgnoreCase);
    }
}
