using System.Text;
using System.Text.RegularExpressions;

namespace PdfReader.Core;

/// <summary>
/// Text as it will be spoken, plus a map back to the original text so the highlight
/// stays on the right word even when "e.g." is spoken as "for example".
/// </summary>
public sealed class SpokenText
{
    private readonly int[] _spokenStarts;
    private readonly int[] _originalStarts;

    public SpokenText(string text, int[] spokenStarts, int[] originalStarts)
    {
        Text = text;
        _spokenStarts = spokenStarts;
        _originalStarts = originalStarts;
    }

    public string Text { get; }

    /// <summary>Where in the original text the word spoken at <paramref name="spokenIndex"/> starts.</summary>
    public int ToOriginal(int spokenIndex)
    {
        if (_spokenStarts.Length == 0) return 0;
        int i = Array.BinarySearch(_spokenStarts, spokenIndex);
        if (i < 0) i = Math.Max(0, ~i - 1);
        return _originalStarts[i];
    }

    public static SpokenText Identity(string text)
    {
        var words = TextTools.SplitWords(text);
        var starts = words.Select(w => w.Start).ToArray();
        return new SpokenText(text, starts, starts);
    }
}

/// <summary>
/// Rewrites text so speech engines read it naturally: abbreviations, code names (AudioSource),
/// file types (.wav), versions, links, symbols, and the user's own word list.
/// Works word by word, so every spoken word can be traced back to the word on the page.
/// </summary>
public sealed class Pronouncer
{
    public bool Enabled { get; set; } = true;
    private Dictionary<string, string> _userWords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The user's own list: word → how to say it (case-insensitive).</summary>
    public IReadOnlyDictionary<string, string> UserWords
    {
        get => _userWords;
        set => _userWords = new Dictionary<string, string>(value ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
    }

    private static readonly (string Written, string Spoken)[] Abbreviations =
    {
        ("e.g.", "for example"), ("eg.", "for example"), ("i.e.", "that is"), ("ie.", "that is"),
        ("etc.", "et cetera"), ("vs.", "versus"), ("vs", "versus"), ("approx.", "approximately"),
        ("fig.", "figure"), ("figs.", "figures"), ("cf.", "compare"), ("w/", "with"), ("w/o", "without"),
        ("min.", "minimum"), ("max.", "maximum"), ("mr.", "mister"), ("mrs.", "missus"), ("dr.", "doctor"),
        ("ch.", "chapter"), ("sec.", "section"), ("pp.", "pages"), ("p.", "page"), ("vol.", "volume"),
        ("approx", "approximately"), ("e.g", "for example"), ("i.e", "that is"),
    };

    private static readonly Dictionary<string, string> Specials = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C#"] = "C sharp", ["F#"] = "F sharp", ["C++"] = "C plus plus", [".NET"] = "dot net", ["ASP.NET"] = "A S P dot net",
        ["&"] = "and", ["->"] = "to", ["→"] = "to", ["⟶"] = "to", ["=>"] = "to", ["<-"] = "from", ["←"] = "from",
        ["•"] = "", ["▪"] = "", ["■"] = "", ["◦"] = "", ["|"] = "", ["…"] = "", ["*"] = "", ["#"] = "",
        ["+"] = "plus", ["="] = "equals", ["~"] = "about", ["±"] = "plus or minus", ["°"] = "degrees",
    };

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wav"] = "wave", ["mp3"] = "M P 3", ["mp4"] = "M P 4", ["ogg"] = "ogg", ["png"] = "P N G", ["jpg"] = "J peg", ["jpeg"] = "J peg",
        ["gif"] = "gif", ["pdf"] = "P D F", ["fbx"] = "F B X", ["obj"] = "O B J", ["json"] = "jason", ["xml"] = "X M L", ["csv"] = "C S V",
        ["txt"] = "text", ["exe"] = "E X E", ["dll"] = "D L L", ["cs"] = "C S", ["js"] = "J S", ["ts"] = "T S", ["py"] = "P Y",
        ["html"] = "H T M L", ["css"] = "C S S", ["zip"] = "zip", ["psd"] = "P S D", ["tga"] = "T G A", ["exr"] = "E X R",
        ["hdr"] = "H D R", ["blend"] = "blend", ["unity"] = "unity", ["prefab"] = "prefab", ["asset"] = "asset", ["mat"] = "mat",
        ["shader"] = "shader", ["hlsl"] = "H L S L", ["glsl"] = "G L S L", ["md"] = "M D", ["yaml"] = "yaml", ["yml"] = "yaml",
        ["apk"] = "A P K", ["ipa"] = "I P A", ["aab"] = "A A B", ["gltf"] = "G L T F", ["glb"] = "G L B", ["ttf"] = "T T F", ["otf"] = "O T F",
    };

    private const string Opening = "([{\"'“‘«";
    private const string Closing = ".,;:!?)]}\"'”’»…";

    private static readonly Regex Url = new(@"^(https?://|www\.)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Email = new(@"^[\w.+-]+@[\w-]+(\.[\w-]+)+$", RegexOptions.Compiled);
    private static readonly Regex VersionV = new(@"^[vV](\d+(\.\d+)*)$", RegexOptions.Compiled);
    private static readonly Regex Version3 = new(@"^\d+\.\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex Dimensions = new(@"^(\d+)[xX×](\d+)$", RegexOptions.Compiled);
    private static readonly Regex DotExt = new(@"^\.([A-Za-z0-9]{1,6})$", RegexOptions.Compiled);
    private static readonly Regex FileName = new(@"^([\w-]+)\.([A-Za-z0-9]{1,6})$", RegexOptions.Compiled);
    private static readonly Regex Dotted = new(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)+(\(\))?$", RegexOptions.Compiled);
    private static readonly Regex Hash = new(@"^#(\d+)$", RegexOptions.Compiled);
    private static readonly Regex CamelBoundary = new(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=\d)", RegexOptions.Compiled);
    private static readonly Regex SlashWords = new(@"^([A-Za-z][A-Za-z0-9]+)/([A-Za-z][A-Za-z0-9]+)$", RegexOptions.Compiled);

    /// <summary>The text to speak, with a map back to <paramref name="text"/>.</summary>
    public SpokenText Transform(string text)
    {
        if (!Enabled && _userWords.Count == 0) return SpokenText.Identity(text);

        var sb = new StringBuilder(text.Length + 16);
        var spoken = new List<int>();
        var original = new List<int>();
        foreach (var w in TextTools.SplitWords(text))
        {
            string said = SayToken(text.Substring(w.Start, w.Length));
            if (said.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            spoken.Add(sb.Length);
            original.Add(w.Start);
            sb.Append(said);
        }
        if (spoken.Count == 0) return SpokenText.Identity(text);
        return new SpokenText(sb.ToString(), spoken.ToArray(), original.ToArray());
    }

    /// <summary>How one written word (with its punctuation) is said.</summary>
    public string SayToken(string token)
    {
        // Split off opening and closing punctuation, keep it around the spoken core.
        int a = 0, b = token.Length;
        while (a < b && Opening.Contains(token[a])) a++;
        string lead = token[..a];
        string rest = token[a..];

        // User words and abbreviations are matched before trailing punctuation is removed ("e.g.," → "for example,").
        if (MatchPrefixTable(rest, out string viaTable, out string afterTable)) return lead + viaTable + afterTable;

        while (b > a && Closing.Contains(token[b - 1])) b--;
        string core = token[a..b];
        string trail = token[b..];
        // "Method()" — the empty brackets aren't spoken.
        if (core.EndsWith('(') && trail.StartsWith(')')) { core = core[..^1]; trail = trail[1..]; }
        if (core.Length == 0) return Specials.TryGetValue(token, out var sym) ? sym : token;

        string? said = SayCore(core);
        return said is null ? token : lead + said + trail;
    }

    private bool MatchPrefixTable(string rest, out string said, out string after)
    {
        said = after = "";
        if (rest.Length == 0) return false;

        // User word list first (whole word, ignoring trailing punctuation).
        string bare = rest.TrimEnd(Closing.ToCharArray());
        if (bare.Length > 0 && _userWords.TryGetValue(bare, out var mine))
        {
            said = mine;
            after = rest[bare.Length..];
            return true;
        }
        if (!Enabled) return false;

        foreach (var (written, spoken) in Abbreviations)
        {
            if (!rest.StartsWith(written, StringComparison.OrdinalIgnoreCase)) continue;
            string tail = rest[written.Length..];
            if (tail.All(c => Closing.Contains(c)))
            {
                // "p." only before a page number is risky alone; skip single-letter forms unless followed by nothing.
                said = spoken;
                after = tail.TrimStart('.');
                return true;
            }
        }
        return false;
    }

    private string? SayCore(string core)
    {
        if (!Enabled) return null;
        if (Specials.TryGetValue(core, out var special)) return special;

        if (Url.IsMatch(core)) return SayUrl(core);
        if (Email.IsMatch(core)) return core.Replace("@", " at ").Replace(".", " dot ");

        var m = VersionV.Match(core);
        if (m.Success) return "version " + m.Groups[1].Value.Replace(".", " point ");
        if (Version3.IsMatch(core)) return core.Replace(".", " point ");

        m = Dimensions.Match(core);
        if (m.Success) return $"{m.Groups[1].Value} by {m.Groups[2].Value}";

        m = Hash.Match(core);
        if (m.Success) return "number " + m.Groups[1].Value;

        m = DotExt.Match(core);
        if (m.Success) return "dot " + SayExtension(m.Groups[1].Value);

        m = FileName.Match(core);
        if (m.Success && Extensions.ContainsKey(m.Groups[2].Value))
            return SplitIdentifier(m.Groups[1].Value) + " dot " + SayExtension(m.Groups[2].Value);

        if (Dotted.IsMatch(core) && !IsDecimal(core))
        {
            string c = core.EndsWith("()") ? core[..^2] : core;
            return string.Join(" dot ", c.Split('.').Select(SplitIdentifier));
        }

        m = SlashWords.Match(core);
        if (m.Success && SlashMeansOr(m.Groups[1].Value, m.Groups[2].Value))
            return SplitIdentifier(m.Groups[1].Value) + " or " + SplitIdentifier(m.Groups[2].Value);

        if (core.Contains('_') && core.Any(char.IsLetter))
            return string.Join(" ", core.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(SplitIdentifier));

        string split = SplitIdentifier(core);
        return split == core ? null : split;
    }

    /// <summary>"AudioSource" → "Audio Source", "GPUInstancing" → "GPU Instancing", "OnTriggerEnter2D" → "On Trigger Enter 2D".</summary>
    public static string SplitIdentifier(string word)
    {
        if (word.Length < 6 || !char.IsLetter(word[0])) return word;
        if (!word.All(char.IsLetterOrDigit)) return word;
        bool hasLower = word.Any(char.IsLower), hasInnerUpper = word.Skip(1).Any(char.IsUpper);
        if (!hasLower || !hasInnerUpper) return word;
        if (char.IsLower(word[0]) && char.IsUpper(word[1])) return word; // iPhone, eBay, iPad
        // Needs a lower→Upper change somewhere (not just "Hello").
        bool hump = false;
        for (int i = 1; i < word.Length; i++)
        {
            if (char.IsLower(word[i - 1]) && char.IsUpper(word[i])) { hump = true; break; }
            // "GPUInstancing": an acronym (2+ capitals) followed by a capitalised word.
            if (i >= 2 && i + 1 < word.Length && char.IsUpper(word[i - 2]) && char.IsUpper(word[i - 1]) && char.IsUpper(word[i]) && char.IsLower(word[i + 1]))
            { hump = true; break; }
        }
        if (!hump) return word;
        return CamelBoundary.Replace(word, " ");
    }

    private static bool SlashMeansOr(string left, string right)
    {
        bool leftCap = char.IsUpper(left[0]), rightCap = char.IsUpper(right[0]);
        if (leftCap && rightCap) return true;                                 // CPU/GPU, Start/Awake
        return !leftCap && !rightCap && left.Length >= 3 && right.Length >= 3 // read/write, and/or
               && left.All(char.IsLetter) && right.All(char.IsLetter);
    }

    private static bool IsDecimal(string s) => s.All(c => char.IsDigit(c) || c == '.');

    private static string SayExtension(string ext) => Extensions.TryGetValue(ext, out var s) ? s : ext.ToLowerInvariant();

    private static string SayUrl(string url)
    {
        string u = Regex.Replace(url, @"^https?://", "", RegexOptions.IgnoreCase);
        if (u.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) u = u[4..];
        int q = u.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) u = u[..q];
        u = u.TrimEnd('/');
        var parts = u.Split('/');
        var sb = new StringBuilder(parts[0].Replace(".", " dot "));
        if (parts.Length > 1) sb.Append(" slash ").Append(string.Join(" slash ", parts.Skip(1).Select(p => p.Replace("-", " ").Replace("_", " ").Replace(".", " dot "))));
        return sb.ToString();
    }
}

/// <summary>Splits text into sentences (for engines that speak a sentence at a time).</summary>
public static class SentenceSplitter
{
    private static readonly HashSet<string> NoBreakAfter = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g.", "i.e.", "etc.", "vs.", "mr.", "mrs.", "ms.", "dr.", "prof.", "st.", "fig.", "figs.", "no.", "vol.", "approx.",
        "cf.", "p.", "pp.", "ch.", "sec.", "jr.", "sr.", "inc.", "ltd.", "co.", "u.s.", "u.k.",
    };

    public const int MaxSentenceLength = 280;

    /// <summary>(start, length) of each sentence; long sentences are cut at a comma or semicolon.</summary>
    public static List<(int Start, int Length)> Split(string text)
    {
        var result = new List<(int, int)>();
        int start = 0;
        var words = TextTools.SplitWords(text);
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            string token = text.Substring(w.Start, w.Length);
            bool last = i == words.Count - 1;
            bool ends = EndsSentence(token) && !NoBreakAfter.Contains(token.TrimStart('(', '"', '“'))
                        && (last || StartsSentence(text.Substring(words[i + 1].Start, words[i + 1].Length)));
            if (ends || last)
            {
                AddCapped(text, start, w.End - start, result);
                if (!last) start = words[i + 1].Start;
            }
        }
        if (result.Count == 0 && text.Trim().Length > 0) result.Add((0, text.Length));
        return result;
    }

    private static bool EndsSentence(string token)
    {
        string t = token.TrimEnd('"', '\'', '”', '’', ')', ']');
        return t.Length > 0 && (t[^1] is '.' or '!' or '?' or '…') && !(t.Length >= 2 && t[^1] == '.' && char.IsDigit(t[^2]) && t.Count(c => c == '.') > 1);
    }

    private static bool StartsSentence(string next)
    {
        string t = next.TrimStart('"', '\'', '“', '‘', '(', '[');
        return t.Length > 0 && (char.IsUpper(t[0]) || char.IsDigit(t[0]) || !char.IsLetter(t[0]));
    }

    private static void AddCapped(string text, int start, int length, List<(int, int)> result)
    {
        while (length > MaxSentenceLength)
        {
            int limit = start + MaxSentenceLength;
            int cut = -1;
            for (int i = limit; i > start + 60; i--)
                if (text[i - 1] is ',' or ';' or ':' && i < text.Length && text[i] == ' ') { cut = i; break; }
            if (cut < 0) { cut = text.LastIndexOf(' ', limit, MaxSentenceLength - 40); if (cut <= start) cut = limit; }
            result.Add((start, cut - start));
            int next = cut;
            while (next < start + length && char.IsWhiteSpace(text[next])) next++;
            length -= next - start;
            start = next;
        }
        if (length > 0) result.Add((start, length));
    }
}

/// <summary>One sentence of a chunk, in both the spoken text and the original text.</summary>
public readonly record struct SpeechSegment(int SpokenStart, int SpokenLength, int OriginalStart, int OriginalLength);

/// <summary>Everything a speech engine needs for one chunk: the text to say, sentence boundaries and the map back.</summary>
public sealed class SpeechInput
{
    private SpeechInput(SpokenText spoken, IReadOnlyList<SpeechSegment> sentences)
    {
        Spoken = spoken;
        Sentences = sentences;
    }

    public SpokenText Spoken { get; }
    public string Text => Spoken.Text;
    public IReadOnlyList<SpeechSegment> Sentences { get; }

    public string SentenceText(int i) => Text.Substring(Sentences[i].SpokenStart, Sentences[i].SpokenLength);

    public static SpeechInput Build(string original, Pronouncer pronouncer)
    {
        var sb = new StringBuilder(original.Length + 32);
        var spokenStarts = new List<int>();
        var originalStarts = new List<int>();
        var segments = new List<SpeechSegment>();

        foreach (var (start, length) in SentenceSplitter.Split(original))
        {
            var part = pronouncer.Transform(original.Substring(start, length));
            if (part.Text.Trim().Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            int offset = sb.Length;
            sb.Append(part.Text);

            foreach (var w in TextTools.SplitWords(part.Text))
            {
                spokenStarts.Add(offset + w.Start);
                originalStarts.Add(start + part.ToOriginal(w.Start));
            }
            segments.Add(new SpeechSegment(offset, part.Text.Length, start, length));
        }

        if (segments.Count == 0)
            return new SpeechInput(SpokenText.Identity(original), new[] { new SpeechSegment(0, original.Length, 0, original.Length) });
        return new SpeechInput(new SpokenText(sb.ToString(), spokenStarts.ToArray(), originalStarts.ToArray()), segments);
    }
}

/// <summary>The word list as editable text: one "word = say it like this" per line.</summary>
public static class WordListText
{
    public static Dictionary<string, string> Parse(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string word = line[..eq].Trim();
            string say = line[(eq + 1)..].Trim();
            if (word.Length == 0 || word.Contains(' ')) continue;
            dict[word] = say;
        }
        return dict;
    }

    public static string Format(IReadOnlyDictionary<string, string> words) =>
        string.Join(Environment.NewLine, words.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key} = {kv.Value}"));
}
