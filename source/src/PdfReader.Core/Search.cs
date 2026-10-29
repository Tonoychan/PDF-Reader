using System.Globalization;

namespace PdfReader.Core;

/// <summary>One place where the search text was found.</summary>
public sealed record SearchHit(int ParagraphIndex, int CharIndex, int Length, int WordIndex, int PageNumber, string Before, string Match, string After);

/// <summary>Finds text in the book (ignoring case and accents), across word breaks within a paragraph.</summary>
public static class BookSearch
{
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth;

    public static IReadOnlyList<SearchHit> Find(BookDocument book, string query, int maxHits = 500, bool includeHeadersFooters = false)
    {
        var hits = new List<SearchHit>();
        query = TextTools.CollapseWhitespace(query ?? "").Trim();
        if (query.Length < 2) return hits;

        foreach (var p in book.Paragraphs)
        {
            if (!includeHeadersFooters && p.Kind == ParagraphKind.HeaderFooter) continue;
            int start = 0;
            while (start < p.Text.Length)
            {
                int rel = Compare.IndexOf(p.Text.AsSpan(start), query.AsSpan(), Options, out int matchLength);
                if (rel < 0) break;
                int i = start + rel;
                int len = Math.Max(1, matchLength);
                hits.Add(MakeHit(p, i, len));
                if (hits.Count >= maxHits) return hits;
                start = i + len;
            }
        }
        return hits;
    }

    private static SearchHit MakeHit(Paragraph p, int index, int length)
    {
        const int context = 48;
        int b = Math.Max(0, index - context);
        if (b > 0) { int sp = p.Text.IndexOf(' ', b); if (sp >= 0 && sp < index) b = sp + 1; }
        int e = Math.Min(p.Text.Length, index + length + context);
        if (e < p.Text.Length) { int sp = p.Text.LastIndexOf(' ', e - 1, e - (index + length)); if (sp > index + length) e = sp; }

        string before = (b > 0 ? "…" : "") + p.Text[b..index];
        string after = p.Text[(index + length)..e] + (e < p.Text.Length ? "…" : "");
        return new SearchHit(p.Index, index, length, p.WordIndexAt(index), p.PageNumber, before, p.Text.Substring(index, length), after);
    }
}
