namespace PdfReader.Core;

/// <summary>A word inside a paragraph, as a character range of the paragraph text.</summary>
public readonly record struct WordSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>What a block of text is, which decides whether it is read aloud and how the text view shows it.</summary>
public enum ParagraphKind
{
    Body,
    Heading,
    HeaderFooter,
    Footnote,
    Caption,
    TableOfContents,
}

/// <summary>One readable paragraph of the book.</summary>
public sealed class Paragraph
{
    private static readonly WordGeometry[] NoGeometry = Array.Empty<WordGeometry>();

    public Paragraph(int index, int pageNumber, string text, ParagraphKind kind = ParagraphKind.Body,
        IReadOnlyList<WordGeometry>? geometry = null, float fontSize = 0)
    {
        Index = index;
        PageNumber = pageNumber;
        Text = text;
        Kind = kind;
        FontSize = fontSize;
        Words = TextTools.SplitWords(text);
        Geometry = geometry is not null && geometry.Count == Words.Count ? geometry : NoGeometry;

        var bounds = default(BoxF);
        foreach (var g in Geometry) bounds = BoxF.Union(BoxF.Union(bounds, g.Box), g.Box2);
        Bounds = bounds;
    }

    public int Index { get; }
    public int PageNumber { get; }
    public string Text { get; }
    public ParagraphKind Kind { get; }
    public float FontSize { get; }
    public IReadOnlyList<WordSpan> Words { get; }

    /// <summary>Position of each word on the page (empty when unknown).</summary>
    public IReadOnlyList<WordGeometry> Geometry { get; }

    /// <summary>Area the whole paragraph covers on its page (empty when unknown).</summary>
    public BoxF Bounds { get; }

    public bool HasGeometry => Geometry.Count > 0;

    /// <summary>Index of the word that contains (or most recently started before) a character position.</summary>
    public int WordIndexAt(int charPosition)
    {
        if (Words.Count == 0) return -1;
        int lo = 0, hi = Words.Count - 1, best = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Words[mid].Start <= charPosition) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }

    public string WordText(int wordIndex) => Text.Substring(Words[wordIndex].Start, Words[wordIndex].Length);
}

/// <summary>An entry of the text view: a paragraph or a picture.</summary>
public abstract record ReflowEntry;
public sealed record ReflowParagraph(Paragraph Paragraph) : ReflowEntry;
public sealed record ReflowImage(int PageNumber, BoxF Box) : ReflowEntry;

/// <summary>The whole extracted book.</summary>
public sealed class BookDocument
{
    public BookDocument(string filePath, int pageCount, IReadOnlyList<Paragraph> paragraphs, IReadOnlyList<PageInfo>? pages = null)
    {
        FilePath = filePath;
        PageCount = pageCount;
        Paragraphs = paragraphs;
        Pages = pages ?? Array.Empty<PageInfo>();
    }

    public string FilePath { get; }
    public string Title => Path.GetFileNameWithoutExtension(FilePath);
    public int PageCount { get; }
    public IReadOnlyList<Paragraph> Paragraphs { get; }
    public IReadOnlyList<PageInfo> Pages { get; }
    public bool IsEmpty => Paragraphs.Count == 0;

    public PageInfo? Page(int number) => number >= 1 && number <= Pages.Count ? Pages[number - 1] : null;

    /// <summary>
    /// Paragraphs and pictures in reading order for the text view. Each picture goes before the first
    /// paragraph of its page that starts below the picture's top edge. Headers/footers are left out.
    /// </summary>
    public IReadOnlyList<ReflowEntry> ReflowEntries()
    {
        var result = new List<ReflowEntry>(Paragraphs.Count + 16);
        var byPage = Paragraphs.GroupBy(p => p.PageNumber).ToDictionary(g => g.Key, g => g.ToList());
        int pageTotal = Math.Max(PageCount, Pages.Count);

        for (int number = 1; number <= pageTotal; number++)
        {
            var images = new Queue<BoxF>((Page(number)?.Images ?? Array.Empty<BoxF>()).OrderBy(b => b.Y));
            if (byPage.TryGetValue(number, out var paras))
            {
                foreach (var p in paras)
                {
                    if (p.Kind == ParagraphKind.HeaderFooter) continue;
                    if (!p.Bounds.IsEmpty)
                        while (images.Count > 0 && images.Peek().Y <= p.Bounds.Y + 1)
                            result.Add(new ReflowImage(number, images.Dequeue()));
                    result.Add(new ReflowParagraph(p));
                }
            }
            while (images.Count > 0) result.Add(new ReflowImage(number, images.Dequeue()));
        }
        return result;
    }
}

/// <summary>Collects blocks page by page while the PDF is being read, then classifies them.</summary>
public sealed class BookBuilder
{
    private sealed class RawBlock
    {
        public required int Page { get; init; }
        public required string Text { get; init; }
        public required IReadOnlyList<WordGeometry> Geometry { get; init; }
        public required float FontSize { get; init; }
        public required float BoldFraction { get; init; }
        public required int LineCount { get; init; }
        public required bool StartsWithMarker { get; init; }
        public required IReadOnlyList<string> Lines { get; init; }
        public BoxF Bounds { get; init; }
        public bool IsNoise { get; init; }
    }

    private readonly List<RawBlock> _blocks = new();
    private readonly SortedDictionary<int, PageInfo> _pages = new();

    /// <summary>Running headers/footers are short lines.</summary>
    public const int MaxHeaderLength = 100;

    /// <summary>Adds one block of plain text lines (no positions). Used for simple sources and tests.</summary>
    public void AddBlock(int pageNumber, IEnumerable<string> lines) =>
        AddCleanBlock(pageNumber, lines.Select(TextLineInput.FromText).ToList());

    /// <summary>Adds a page with positioned words and its pictures.</summary>
    public void AddPage(PageInput page)
    {
        _pages[page.Number] = new PageInfo(page.Number, page.Width, page.Height, MergeImages(page.Images, page.Width, page.Height));
        foreach (var block in page.Blocks)
            foreach (var part in SplitByStyle(block))
                AddCleanBlock(page.Number, part);
    }

    private void AddCleanBlock(int pageNumber, IReadOnlyList<TextLineInput> lines)
    {
        var clean = TokenCleaner.Clean(lines);
        if (clean.Words.Count == 0) return;

        string text = string.Join(" ", clean.Words.Select(w => w.Text));
        var geometry = clean.Words.Select(w => new WordGeometry(w.Box, w.Box2)).ToList();
        var bounds = default(BoxF);
        foreach (var g in geometry) bounds = BoxF.Union(BoxF.Union(bounds, g.Box), g.Box2);

        int chars = clean.Words.Sum(w => w.Text.Length);
        float size = chars == 0 ? 0 : clean.Words.Sum(w => w.FontSize * w.Text.Length) / chars;
        float bold = chars == 0 ? 0 : clean.Words.Where(w => w.Bold).Sum(w => w.Text.Length) / (float)chars;

        _blocks.Add(new RawBlock
        {
            Page = pageNumber,
            Text = text,
            Geometry = geometry,
            FontSize = size,
            BoldFraction = bold,
            LineCount = clean.Lines.Count,
            StartsWithMarker = clean.StartsWithMarker,
            Lines = clean.Lines,
            Bounds = bounds,
            IsNoise = TextTools.IsNoise(text),
        });
    }

    public BookDocument Build(string filePath, int pageCount)
    {
        float body = BodyFontSize();
        var repeated = FindRepeatedHeaders(pageCount);
        var tocPages = FindTableOfContentsPages();

        var paragraphs = new List<Paragraph>(_blocks.Count);
        foreach (var b in _blocks)
        {
            var kind = Classify(b, body, repeated, tocPages);
            paragraphs.Add(new Paragraph(paragraphs.Count, b.Page, b.Text, kind, b.Geometry, b.FontSize));
        }

        var pages = new List<PageInfo>();
        for (int n = 1; n <= pageCount; n++)
            pages.Add(_pages.TryGetValue(n, out var info) ? info : new PageInfo(n, 0, 0, Array.Empty<BoxF>()));

        return new BookDocument(filePath, pageCount, paragraphs, _pages.Count > 0 ? pages : null);
    }

    // ---- classification --------------------------------------------------------------------------

    private ParagraphKind Classify(RawBlock b, float body, HashSet<string> repeated, HashSet<int> tocPages)
    {
        if (b.IsNoise) return ParagraphKind.HeaderFooter;
        _pages.TryGetValue(b.Page, out var page);
        bool hasPos = page is not null && page.Height > 0 && !b.Bounds.IsEmpty;

        // Short, smaller-than-body text in the top or bottom margin band.
        bool smallish = body <= 0 || b.FontSize <= 0 || b.FontSize <= body * 0.9f;
        if (hasPos && smallish && b.Text.Length <= 150 &&
            (b.Bounds.Bottom <= page!.Height * 0.075f || b.Bounds.Y >= page.Height * 0.925f))
            return ParagraphKind.HeaderFooter;

        if (b.Text.Length <= MaxHeaderLength && repeated.Contains(TextTools.HeaderKey(b.Text)))
            return ParagraphKind.HeaderFooter;

        if (tocPages.Contains(b.Page)) return ParagraphKind.TableOfContents;

        if (body > 0 && b.FontSize > 0)
        {
            bool small = b.FontSize <= body * 0.88f;
            if (small && (b.StartsWithMarker || (hasPos && b.Bounds.Y >= page!.Height * 0.72f)))
                return ParagraphKind.Footnote;

            if (b.Text.Length <= 300 && (CaptionPrefix(b.Text) || (small && hasPos && NearImage(b.Bounds, page!))))
                return ParagraphKind.Caption;

            if (b.FontSize >= body * 1.2f && b.Text.Length <= 200) return ParagraphKind.Heading;
            if (LooksLikeHeading(b, body)) return ParagraphKind.Heading;
        }
        else if (b.StartsWithMarker)
        {
            return ParagraphKind.Footnote;
        }

        return ParagraphKind.Body;
    }

    private static bool LooksLikeHeading(RawBlock b, float body)
    {
        string t = b.Text.TrimEnd();
        if (t.Length == 0 || t.Length > 90 || b.LineCount > 2) return false;
        if (b.FontSize < body * 0.95f) return false;
        if (".:;,!?—–-".Contains(t[^1])) return false;
        if (!char.IsUpper(t[0]) && !char.IsDigit(t[0])) return false;
        int words = t.Split(' ').Length;
        if (words < 2 && !(b.BoldFraction > 0.8f || t.All(c => !char.IsLetter(c) || char.IsUpper(c)))) return false;
        return b.BoldFraction > 0.6f || b.LineCount == 1;
    }

    private static bool CaptionPrefix(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"^(Figure|Fig\.|Table|Image|Illustration|Source:)\s*\d*", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        && text.Length <= 300;

    private static bool NearImage(BoxF block, PageInfo page)
    {
        foreach (var img in page.Images)
        {
            bool overlapX = block.X < img.Right && img.X < block.Right;
            float gapBelow = block.Y - img.Bottom;
            float gapAbove = img.Y - block.Bottom;
            if (overlapX && ((gapBelow >= -2 && gapBelow <= 28) || (gapAbove >= -2 && gapAbove <= 20))) return true;
        }
        return false;
    }

    /// <summary>The most common text size, weighted by characters (the size of ordinary body text).</summary>
    private float BodyFontSize()
    {
        var weights = new Dictionary<float, int>();
        foreach (var b in _blocks)
        {
            if (b.IsNoise || b.FontSize <= 0) continue;
            float key = MathF.Round(b.FontSize * 2) / 2;
            weights[key] = weights.GetValueOrDefault(key) + b.Text.Length;
        }
        return weights.Count == 0 ? 0 : weights.MaxBy(kv => kv.Value).Key;
    }

    /// <summary>
    /// Short blocks whose text (ignoring numbers) shows up on many different pages,
    /// like "6 of 80 | example.com" or "© 2023 Company".
    /// </summary>
    private HashSet<string> FindRepeatedHeaders(int pageCount)
    {
        int threshold = Math.Max(3, (int)Math.Ceiling(pageCount * 0.3));
        return _blocks
            .Where(b => b.Text.Length <= MaxHeaderLength)
            .GroupBy(b => TextTools.HeaderKey(b.Text))
            .Where(g => g.Select(b => b.Page).Distinct().Count() >= threshold)
            .Select(g => g.Key)
            .ToHashSet();
    }

    private static readonly System.Text.RegularExpressions.Regex DotLeader =
        new(@"(\.\s*){4,}\s*\d{1,4}\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Pages where many lines end in dot leaders and a page number ("Audio . . . . 65").</summary>
    private HashSet<int> FindTableOfContentsPages()
    {
        var pages = new HashSet<int>();
        foreach (var g in _blocks.GroupBy(b => b.Page))
        {
            var lines = g.SelectMany(b => b.Lines).ToList();
            int leaders = lines.Count(l => DotLeader.IsMatch(l));
            if (leaders >= 3 && leaders >= lines.Count * 0.3) pages.Add(g.Key);
        }
        return pages;
    }

    // ---- page structure helpers ------------------------------------------------------------------

    /// <summary>Splits a layout block where the text style changes (e.g. a heading glued to its paragraph).</summary>
    private static IEnumerable<List<TextLineInput>> SplitByStyle(List<TextLineInput> block)
    {
        var current = new List<TextLineInput>();
        (float Size, bool Bold)? prevStyle = null;
        foreach (var line in block)
        {
            var style = LineStyle(line);
            if (prevStyle is { } p && current.Count > 0 && style.Size > 0 && p.Size > 0)
            {
                float ratio = style.Size / p.Size;
                if (ratio > 1.15f || ratio < 0.87f || style.Bold != p.Bold)
                {
                    yield return current;
                    current = new List<TextLineInput>();
                }
            }
            current.Add(line);
            prevStyle = style;
        }
        if (current.Count > 0) yield return current;
    }

    private static (float Size, bool Bold) LineStyle(TextLineInput line)
    {
        int chars = 0, boldChars = 0;
        float sum = 0;
        foreach (var t in line.Tokens)
        {
            if (t.FontSize <= 0) continue;
            int n = Math.Max(1, t.Text.Length);
            chars += n;
            sum += t.FontSize * n;
            if (t.Bold) boldChars += n;
        }
        return chars == 0 ? (0, false) : (sum / chars, boldChars * 2 > chars);
    }

    /// <summary>Drops tiny and full-page pictures and merges pictures that overlap (tiled images).</summary>
    public static List<BoxF> MergeImages(IEnumerable<BoxF> images, float pageWidth, float pageHeight)
    {
        float pageArea = pageWidth * pageHeight;
        var list = images
            .Select(b => Clip(b, pageWidth, pageHeight))
            .Where(b => b.W >= 36 && b.H >= 36)
            .Where(b => pageArea <= 0 || b.Area < pageArea * 0.85f)
            .ToList();

        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < list.Count && !merged; i++)
                for (int j = i + 1; j < list.Count && !merged; j++)
                    if (list[i].Inflate(2).Intersects(list[j]))
                    {
                        list[i] = BoxF.Union(list[i], list[j]);
                        list.RemoveAt(j);
                        merged = true;
                    }
        }
        return list.OrderBy(b => b.Y).ThenBy(b => b.X).ToList();
    }

    private static BoxF Clip(BoxF b, float w, float h)
    {
        if (w <= 0 || h <= 0) return b;
        float x = Math.Max(0, b.X), y = Math.Max(0, b.Y);
        float r = Math.Min(w, b.Right), bottom = Math.Min(h, b.Bottom);
        return new BoxF(x, y, r - x, bottom - y);
    }
}
