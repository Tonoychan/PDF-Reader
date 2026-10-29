using PdfReader.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Outline;

namespace PdfReader.App.Pdf;

/// <summary>
/// Pulls positioned words and picture areas out of a PDF, page by page, in reading order.
/// Positions are in points from the page's top-left corner (the visible crop box), which is
/// how the page renderer draws them, so highlights line up with the rendered page.
/// </summary>
public static class PdfTextExtractor
{
    /// <summary>
    /// Reads every page. Pages are shared between several workers (each with its own copy of the
    /// document, since PdfPig documents are not thread-safe), which makes big books much faster.
    /// </summary>
    public static BookDocument Extract(string path, IProgress<(int Page, int Total)>? progress, CancellationToken ct)
    {
        int total;
        IReadOnlyList<RawOutlineItem>? outline;
        using (var pdf = PdfDocument.Open(path))
        {
            total = pdf.NumberOfPages;
            outline = ReadOutline(pdf);
        }

        var pages = new PageInput?[total];
        int done = 0;
        int workers = total < 12 ? 1 : Math.Clamp(Environment.ProcessorCount - 1, 1, 6);

        Parallel.For(0, workers, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = workers }, worker =>
        {
            using var pdf = PdfDocument.Open(path);
            for (int number = worker + 1; number <= total; number += workers)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    pages[number - 1] = ReadPage(pdf.GetPage(number), number);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // One broken page should not stop the whole book from loading.
                }
                progress?.Report((Interlocked.Increment(ref done), total));
            }
        });

        var builder = new BookBuilder();
        foreach (var page in pages)
            if (page is not null) builder.AddPage(page);
        builder.SetPdfOutline(outline);
        return builder.Build(path, total);
    }

    /// <summary>The PDF's own outline (the bookmarks panel in other readers), flattened with levels.</summary>
    private static IReadOnlyList<RawOutlineItem>? ReadOutline(PdfDocument pdf)
    {
        try
        {
            if (!pdf.TryGetBookmarks(out var bookmarks) || bookmarks is null) return null;
            var list = new List<RawOutlineItem>();
            void Walk(IEnumerable<BookmarkNode> nodes, int level)
            {
                foreach (var node in nodes)
                {
                    if (node is DocumentBookmarkNode doc && !string.IsNullOrWhiteSpace(node.Title))
                        list.Add(new RawOutlineItem(TextTools.CollapseWhitespace(TextTools.Normalize(node.Title)), level, doc.PageNumber));
                    if (list.Count > 5000) return;
                    Walk(node.Children, level + 1);
                }
            }
            Walk(bookmarks.Roots, 0);
            return list;
        }
        catch
        {
            return null; // a broken outline just means Contents falls back to detected headings
        }
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

        var words = JoinUnspacedPieces(page.GetWords(NearestNeighbourWordExtractor.Instance).ToList());
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

    /// <summary>
    /// Some PDFs write real space characters between words. On those pages, two pieces on the same line with
    /// no space between them are one word that the word finder split (common in letter-spaced headings:
    /// "PROGR" + "AMMING"). Whitespace pieces are dropped afterwards.
    /// </summary>
    private static List<Word> JoinUnspacedPieces(List<Word> words)
    {
        bool explicitSpaces = words.Any(w => w.Text.Length > 0 && string.IsNullOrWhiteSpace(w.Text));
        var result = new List<Word>(words.Count);
        Word? prev = null;
        bool spaceSincePrev = false;
        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w.Text)) { spaceSincePrev = true; continue; }
            if (explicitSpaces && prev is not null && !spaceSincePrev && SameLineTouching(prev, w))
            {
                var merged = new Word(prev.Letters.Concat(w.Letters).ToList());
                result[^1] = merged;
                prev = merged;
                continue;
            }
            result.Add(w);
            prev = w;
            spaceSincePrev = false;
        }
        return result;
    }

    private static bool SameLineTouching(Word a, Word b)
    {
        double size = Math.Max(1, Math.Max(FontSize(a), FontSize(b)));
        double gap = b.BoundingBox.Left - a.BoundingBox.Right;
        bool sameLine = Math.Abs(a.BoundingBox.Bottom - b.BoundingBox.Bottom) < size * 0.25;
        return sameLine && gap > -size * 0.1 && gap < size * 0.4;
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
