namespace PdfReader.Core;

/// <summary>A piece of one paragraph handed to the speech engine in one go.</summary>
public readonly record struct SpeechChunk(int ParagraphIndex, int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>A place in the book: paragraph + character offset (always at a word start).</summary>
public readonly record struct ReadingPosition(int ParagraphIndex, int CharOffset)
{
    public static readonly ReadingPosition Start = new(0, 0);
}

/// <summary>
/// Decides what to speak next. Long paragraphs are cut into chunks at sentence ends
/// so speech starts quickly and the next chunk can be prepared while the current one plays.
/// </summary>
public sealed class ReadingPlan
{
    public const int DefaultTargetChunk = 600;
    public const int DefaultMaxChunk = 1200;

    private readonly BookDocument _book;
    private readonly int _target;
    private readonly int _max;
    private readonly Func<Paragraph, bool> _isSpoken;

    public ReadingPlan(BookDocument book, Func<Paragraph, bool>? isSpoken = null,
        int targetChunk = DefaultTargetChunk, int maxChunk = DefaultMaxChunk)
    {
        _book = book;
        _isSpoken = isSpoken ?? (_ => true);
        _target = targetChunk;
        _max = maxChunk;
    }

    public bool IsSpoken(int paragraphIndex) =>
        paragraphIndex >= 0 && paragraphIndex < _book.Paragraphs.Count && _isSpoken(_book.Paragraphs[paragraphIndex]);

    /// <summary>The nearest spoken paragraph from <paramref name="from"/> going in <paramref name="direction"/> (±1), including itself.</summary>
    public int? FindSpoken(int from, int direction)
    {
        for (int i = from; i >= 0 && i < _book.Paragraphs.Count; i += direction)
            if (_isSpoken(_book.Paragraphs[i])) return i;
        return null;
    }

    /// <summary>The chunk that starts at <paramref name="pos"/>, or null at the end of the book.</summary>
    public SpeechChunk? ChunkAt(ReadingPosition pos)
    {
        var p = pos.ParagraphIndex;
        var offset = pos.CharOffset;
        while (p < _book.Paragraphs.Count)
        {
            var para = _book.Paragraphs[p];
            if (!_isSpoken(para)) { p++; offset = 0; continue; }
            offset = SnapToWordStart(para, offset);
            if (offset < para.Text.Length)
                return new SpeechChunk(p, offset, FindChunkEnd(para.Text, offset));
            p++;
            offset = 0;
        }
        return null;
    }

    /// <summary>The chunk that follows <paramref name="chunk"/>, or null at the end of the book.</summary>
    public SpeechChunk? After(SpeechChunk chunk)
    {
        var para = _book.Paragraphs[chunk.ParagraphIndex];
        return chunk.End < para.Text.Length
            ? ChunkAt(new ReadingPosition(chunk.ParagraphIndex, chunk.End))
            : ChunkAt(new ReadingPosition(chunk.ParagraphIndex + 1, 0));
    }

    public string TextOf(SpeechChunk chunk) =>
        _book.Paragraphs[chunk.ParagraphIndex].Text.Substring(chunk.Start, chunk.Length);

    private static int SnapToWordStart(Paragraph para, int offset)
    {
        // Start of the word containing the offset, or of the next word if the offset is in a gap.
        foreach (var w in para.Words)
            if (w.End > offset) return w.Start;
        return para.Text.Length;
    }

    private int FindChunkEnd(string text, int start)
    {
        int remaining = text.Length - start;
        if (remaining <= _max) return text.Length;

        // Prefer the first sentence end after the target length.
        for (int i = start + _target; i < start + _max; i++)
        {
            if (IsSentenceEnd(text, i)) return i + 1;
        }
        // Otherwise the last sentence end before the target.
        for (int i = start + _target - 1; i > start + 40; i--)
        {
            if (IsSentenceEnd(text, i)) return i + 1;
        }
        // Otherwise cut at a space.
        int space = text.LastIndexOf(' ', start + _max - 1, _max - 1);
        return space > start ? space + 1 : start + _max;
    }

    private static bool IsSentenceEnd(string text, int i)
    {
        char c = text[i];
        if (c != '.' && c != '!' && c != '?' && c != ';' && c != ':') return false;
        if (i + 1 >= text.Length) return true;
        char next = text[i + 1];
        if (next == '"' || next == '\'' || next == '”' || next == '’' || next == ')')
            return i + 2 >= text.Length || text[i + 2] == ' ';
        return next == ' ';
    }
}
