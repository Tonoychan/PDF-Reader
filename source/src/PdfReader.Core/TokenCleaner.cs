using System.Text.RegularExpressions;

namespace PdfReader.Core;

/// <summary>A cleaned word with its position(s) on the page.</summary>
public sealed record CleanWord(string Text, BoxF Box, BoxF Box2, float FontSize, bool Bold);

/// <summary>A cleaned block: its words, and facts used to classify it.</summary>
public sealed record CleanBlock(IReadOnlyList<CleanWord> Words, bool StartsWithMarker, IReadOnlyList<string> Lines);

/// <summary>
/// Turns raw PDF words into words fit for reading aloud while keeping where each word sits on the page:
/// removes footnote markers, glues stray punctuation ("team ." → "team."), joins letter-spaced headings
/// ("I N T R O" → "INTRO") and words hyphenated across lines ("exam-" + "ple" → "example").
/// </summary>
public static class TokenCleaner
{
    private const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹";
    private const string Closing = ".,;:!?%)]}’”»…";
    private const string Opening = "([{‘“«";
    private static readonly Regex ApostropheSuffix = new(@"^['’](s|t|d|ll|re|ve|m)\W*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static CleanBlock Clean(IReadOnlyList<TextLineInput> lines)
    {
        var words = new List<CleanWord>();
        var lineTexts = new List<string>();
        bool startsWithMarker = false;

        for (int li = 0; li < lines.Count; li++)
        {
            var toks = CleanLine(lines[li].Tokens, out bool leadingMarker);
            if (words.Count == 0 && leadingMarker) startsWithMarker = true;
            if (toks.Count == 0) continue;
            lineTexts.Add(string.Join(" ", toks.Select(t => t.Text)));

            int start = 0;
            if (words.Count > 0)
            {
                var last = words[^1];
                var first = toks[0];
                if (IsHyphenBreak(last.Text, first.Text))
                {
                    words[^1] = last with { Text = last.Text[..^1] + first.Text, Box2 = BoxF.Union(last.Box2, first.Box) };
                    start = 1;
                }
                else if (IsAllClosing(first.Text))
                {
                    words[^1] = last with { Text = last.Text + first.Text, Box2 = BoxF.Union(last.Box2, first.Box) };
                    start = 1;
                }
            }

            for (int i = start; i < toks.Count; i++)
                words.Add(new CleanWord(toks[i].Text, toks[i].Box, default, toks[i].FontSize, toks[i].Bold));
        }

        return new CleanBlock(words, startsWithMarker, lineTexts);
    }

    /// <summary>Cleans the words of one line.</summary>
    public static List<TextToken> CleanLine(IReadOnlyList<TextToken> tokens, out bool leadingMarker)
    {
        leadingMarker = false;
        float median = Median(tokens.Where(t => t.FontSize > 0).Select(t => t.FontSize));

        // 1. Normalize text, drop footnote markers (superscript digits or tiny digits).
        var list = new List<TextToken>(tokens.Count);
        foreach (var t in tokens)
        {
            string raw = t.Text ?? "";
            if (raw.Length > 0 && raw.All(c => Superscripts.Contains(c) || char.IsWhiteSpace(c)))
            {
                if (list.Count == 0) leadingMarker = true;
                continue;
            }
            if (list.Count == 0 && raw.Length > 0 && Superscripts.Contains(raw[0])) leadingMarker = true;
            raw = raw.Trim(Superscripts.ToCharArray());

            string s = RemoveWhitespace(TextTools.Normalize(raw));
            if (s.Length == 0) continue;

            bool tiny = median > 0 && t.FontSize > 0 && t.FontSize <= median * 0.72f;
            if (tiny && s.Length <= 3 && s.All(c => char.IsDigit(c) || c == '*' || c == '†'))
            {
                if (list.Count == 0) leadingMarker = true;
                continue;
            }
            list.Add(t with { Text = s });
        }

        // 2. Letter-spaced words: "I N T R O" → "INTRO".
        list = MergeLetterSpacing(list);

        // 3. Glue punctuation and visually touching pieces.
        var output = new List<TextToken>(list.Count);
        foreach (var t in list)
        {
            if (output.Count > 0)
            {
                var prev = output[^1];
                bool touching = Touching(prev, t);
                bool closing = IsAllClosing(t.Text) || ApostropheSuffix.IsMatch(t.Text);
                bool prevOpening = IsAllOpening(prev.Text);
                if (touching || closing || prevOpening)
                {
                    output[^1] = Merge(prev, t);
                    continue;
                }
            }
            output.Add(t);
        }
        return output;
    }

    private static List<TextToken> MergeLetterSpacing(List<TextToken> list)
    {
        var result = new List<TextToken>(list.Count);
        int i = 0;
        while (i < list.Count)
        {
            int j = i;
            while (j < list.Count && list[j].Text.Length == 1 && char.IsLetter(list[j].Text[0])) j++;
            int run = j - i;
            if (run >= 3)
            {
                // Split the run where the gap is clearly wider than the letter spacing (a real word break).
                var gaps = new List<float>();
                for (int k = i + 1; k < j; k++)
                    if (!list[k].Box.IsEmpty && !list[k - 1].Box.IsEmpty) gaps.Add(list[k].Box.X - list[k - 1].Box.Right);
                float typical = gaps.Count > 0 ? Median(gaps) : 0;

                var current = list[i];
                for (int k = i + 1; k < j; k++)
                {
                    float gap = !list[k].Box.IsEmpty && !list[k - 1].Box.IsEmpty ? list[k].Box.X - list[k - 1].Box.Right : 0;
                    bool wordBreak = gaps.Count > 0 && gap > Math.Max(typical * 1.8f, typical + list[k].FontSize * 0.25f);
                    if (wordBreak) { result.Add(current); current = list[k]; }
                    else current = Merge(current, list[k]);
                }
                result.Add(current);
                i = j;
            }
            else
            {
                result.Add(list[i]);
                i++;
            }
        }
        return result;
    }

    private static bool Touching(TextToken a, TextToken b)
    {
        if (a.Box.IsEmpty || b.Box.IsEmpty) return false;
        float size = Math.Max(1, Math.Max(a.FontSize, b.FontSize));
        float gap = b.Box.X - a.Box.Right;
        bool sameLine = Math.Abs((a.Box.Y + a.Box.H / 2) - (b.Box.Y + b.Box.H / 2)) < size * 0.6f;
        return sameLine && gap < size * 0.1f && gap > -size; // overlapping a lot means something else
    }

    private static TextToken Merge(TextToken a, TextToken b) =>
        new(a.Text + b.Text, BoxF.Union(a.Box, b.Box), Math.Max(a.FontSize, b.FontSize), a.Bold && b.Bold);

    private static bool IsHyphenBreak(string last, string first) =>
        last.Length >= 2 && last[^1] == '-' && char.IsLetter(last[^2]) && first.Length > 0 && char.IsLower(first[0]);

    public static bool IsAllClosing(string s) => s.Length > 0 && s.All(c => Closing.Contains(c));
    private static bool IsAllOpening(string s) => s.Length > 0 && s.All(c => Opening.Contains(c));

    private static string RemoveWhitespace(string s) =>
        s.Any(char.IsWhiteSpace) ? new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()) : s;

    internal static float Median(IEnumerable<float> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return 0;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }
}
