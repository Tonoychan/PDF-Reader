using System.Text;

namespace PdfReader.Core;

/// <summary>A chapter/section entry of the book's contents, linked to the paragraph it starts at.</summary>
public sealed record OutlineEntry(string Title, int Level, int PageNumber, int ParagraphIndex);

/// <summary>An entry of the PDF's own outline (bookmarks) as read from the file.</summary>
public sealed record RawOutlineItem(string Title, int Level, int PageNumber);

/// <summary>Builds the Contents list: from the PDF's outline when it has one, otherwise from detected headings.</summary>
public static class OutlineBuilder
{
    public static IReadOnlyList<OutlineEntry> Build(IReadOnlyList<Paragraph> paragraphs, IReadOnlyList<RawOutlineItem>? pdfOutline)
    {
        if (paragraphs.Count == 0) return Array.Empty<OutlineEntry>();
        if (pdfOutline is { Count: > 0 })
        {
            var fromPdf = FromPdfOutline(paragraphs, pdfOutline);
            if (fromPdf.Count > 0) return fromPdf;
        }
        return FromHeadings(paragraphs);
    }

    private static List<OutlineEntry> FromPdfOutline(IReadOnlyList<Paragraph> paragraphs, IReadOnlyList<RawOutlineItem> items)
    {
        var byPage = paragraphs.Where(p => p.Kind != ParagraphKind.HeaderFooter)
            .GroupBy(p => p.PageNumber).ToDictionary(g => g.Key, g => g.ToList());
        int maxPage = paragraphs.Max(p => p.PageNumber);

        var result = new List<OutlineEntry>(items.Count);
        foreach (var item in items)
        {
            string title = item.Title.Trim();
            if (title.Length == 0 || item.PageNumber < 1) continue;
            int? para = FindParagraph(byPage, item.PageNumber, title, maxPage);
            if (para is null) continue;
            result.Add(new OutlineEntry(title, Math.Max(0, item.Level), item.PageNumber, para.Value));
        }
        return result;
    }

    /// <summary>The paragraph on (or soon after) the page whose text starts like the title; else the page's first paragraph.</summary>
    private static int? FindParagraph(Dictionary<int, List<Paragraph>> byPage, int page, string title, int maxPage)
    {
        string key = Normalize(title);
        for (int p = page; p <= Math.Min(page + 1, maxPage); p++)
        {
            if (!byPage.TryGetValue(p, out var list)) continue;
            Paragraph? best = null;
            foreach (var para in list)
            {
                string text = Normalize(para.Text);
                bool match = key.Length > 0 && (text.StartsWith(key) || (text.Length >= 4 && key.StartsWith(text)) || (key.Length >= 6 && text.Contains(key)));
                if (!match) continue;
                if (best is null || (para.Kind == ParagraphKind.Heading && best.Kind != ParagraphKind.Heading)) best = para;
                if (best.Kind == ParagraphKind.Heading) break;
            }
            if (best is not null) return best.Index;
        }

        // No text match: the first readable paragraph from that page on.
        for (int p = page; p <= maxPage; p++)
            if (byPage.TryGetValue(p, out var list))
            {
                var first = list.FirstOrDefault(x => x.Kind is ParagraphKind.Heading or ParagraphKind.Body) ?? list[0];
                return first.Index;
            }
        return null;
    }

    private sealed class HeadingGroup
    {
        public required Paragraph First { get; init; }
        public required string Text { get; set; }
        public required int LastIndex { get; set; }
    }

    /// <summary>
    /// Contents from detected headings: bigger text = higher level (at most 3 levels). Headings that run on
    /// (a banner title split over lines, e.g. "ADAPTIVE" + "PERFORMANCE") are joined into one entry.
    /// </summary>
    private static List<OutlineEntry> FromHeadings(IReadOnlyList<Paragraph> paragraphs)
    {
        var headings = paragraphs.Where(p => p.Kind == ParagraphKind.Heading && p.Text.Length is >= 2 and <= 120).ToList();
        if (headings.Count == 0) return new List<OutlineEntry>();

        // Join run-on pieces: consecutive paragraphs, same page, same size.
        var groups = new List<HeadingGroup>();
        foreach (var h in headings)
        {
            if (groups.Count > 0)
            {
                var g = groups[^1];
                if (h.Index == g.LastIndex + 1 && h.PageNumber == g.First.PageNumber &&
                    MathF.Abs(h.FontSize - g.First.FontSize) < 0.6f && g.Text.Length + h.Text.Length < 120 &&
                    IsAllCaps(g.Text) && IsAllCaps(h.Text))
                {
                    g.Text += " " + h.Text;
                    g.LastIndex = h.Index;
                    continue;
                }
            }
            groups.Add(new HeadingGroup { First = h, Text = h.Text, LastIndex = h.Index });
        }

        // Levels come from the heading sizes used more than once (a one-off size, like a cover title,
        // takes the level of the nearest size at or below it).
        var counts = groups.GroupBy(g => MathF.Round(g.First.FontSize)).ToDictionary(x => x.Key, x => x.Count());
        var sizes = counts.Where(kv => kv.Value >= 2).Select(kv => kv.Key).OrderByDescending(x => x).ToList();
        if (sizes.Count == 0) sizes = counts.Keys.OrderByDescending(x => x).ToList();
        int RankOf(float size)
        {
            float r = MathF.Round(size);
            int i = sizes.FindIndex(x => x <= r);
            return Math.Min(2, i < 0 ? sizes.Count : i);
        }
        var raw = groups.Select(g => RankOf(g.First.FontSize)).ToList();
        // Use consecutive levels only (sizes 44 + 9 → levels 0 and 1, not 0 and 2).
        var used = raw.Distinct().OrderBy(x => x).ToList();

        var result = new List<OutlineEntry>();
        string? last = null;
        for (int i = 0; i < groups.Count; i++)
        {
            string title = Tidy(groups[i].Text);
            if (title == last) continue; // the same title repeated right after (e.g. banner + heading)
            last = title;
            result.Add(new OutlineEntry(title, used.IndexOf(raw[i]), groups[i].First.PageNumber, groups[i].First.Index));
        }
        return result;
    }

    private static bool IsAllCaps(string t) => t.Any(char.IsLetter) && t.Where(char.IsLetter).All(char.IsUpper);

    /// <summary>"PROFILING" → "Profiling" so all-caps banners read nicely in the list.</summary>
    private static string Tidy(string text)
    {
        string t = text.Trim();
        if (t.Length > 3 && t.Any(char.IsLetter) && t.Where(char.IsLetter).All(char.IsUpper))
            t = char.ToUpper(t[0]) + t[1..].ToLowerInvariant();
        return t;
    }

    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in TextTools.Normalize(s))
        {
            if (char.IsLetterOrDigit(c)) { if (space && sb.Length > 0) sb.Append(' '); sb.Append(char.ToLowerInvariant(c)); space = false; }
            else space = true;
        }
        return sb.ToString();
    }

    /// <summary>The entry the given paragraph belongs to (the last one starting at or before it), or -1.</summary>
    public static int EntryFor(IReadOnlyList<OutlineEntry> outline, int paragraphIndex)
    {
        int best = -1;
        for (int i = 0; i < outline.Count; i++)
        {
            if (outline[i].ParagraphIndex <= paragraphIndex && (best < 0 || outline[i].ParagraphIndex >= outline[best].ParagraphIndex)) best = i;
        }
        return best;
    }
}
