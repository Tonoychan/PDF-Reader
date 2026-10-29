using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfReader.Core;

/// <summary>Cleans up raw PDF text so it reads naturally.</summary>
public static class TextTools
{
    private const char SoftHyphen = '­';

    /// <summary>
    /// Joins the lines of one block of plain text into a paragraph string (same clean-up as real PDF words:
    /// ligatures, hyphenation across lines, stray punctuation, footnote markers).
    /// </summary>
    public static string JoinLines(IEnumerable<string> lines)
    {
        var block = TokenCleaner.Clean(lines.Select(TextLineInput.FromText).ToList());
        return string.Join(" ", block.Words.Select(w => w.Text));
    }

    /// <summary>Identity of a possible running header/footer: lower-case, numbers ignored.</summary>
    public static string HeaderKey(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"\d+", "#").Trim();

    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        // NFKC turns ligatures and full-width forms into plain letters.
        s = s.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == SoftHyphen || c == '﻿' || c == '​') continue;
            var cat = char.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.Control && !char.IsWhiteSpace(c)) continue;
            if (cat == UnicodeCategory.PrivateUse) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool pendingSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Words are runs of non-whitespace characters.</summary>
    public static IReadOnlyList<WordSpan> SplitWords(string text)
    {
        var words = new List<WordSpan>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (i > start) words.Add(new WordSpan(start, i - start));
        }
        return words;
    }

    /// <summary>Blocks that are not worth reading aloud: empty, bare page numbers, lone symbols.</summary>
    public static bool IsNoise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!text.Any(char.IsLetterOrDigit)) return true;

        string t = text.Trim();
        // "12", "- 12 -", "Page 12", "12 / 300", "xiv"
        string stripped = t.Trim('-', '–', '—', ' ', '|', '.');
        if (stripped.All(char.IsDigit)) return true;
        if (stripped.StartsWith("page ", StringComparison.OrdinalIgnoreCase) &&
            stripped[5..].Replace(" ", "").Replace("/", "").Replace("of", "").All(char.IsDigit)) return true;
        if (stripped.Replace(" ", "").Split('/').All(p => p.Length > 0 && p.All(char.IsDigit))) return true;
        if (stripped.Length <= 6 && stripped.All(c => "ivxlcdm".Contains(char.ToLowerInvariant(c)))) return true;
        return false;
    }
}
