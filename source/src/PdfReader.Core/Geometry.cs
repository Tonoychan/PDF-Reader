namespace PdfReader.Core;

/// <summary>A rectangle on a PDF page, in points, measured from the page's top-left corner.</summary>
public readonly record struct BoxF(float X, float Y, float W, float H)
{
    public bool IsEmpty => W <= 0 || H <= 0;
    public float Right => X + W;
    public float Bottom => Y + H;
    public float Area => IsEmpty ? 0 : W * H;

    public static BoxF Union(BoxF a, BoxF b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        float x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new BoxF(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    public BoxF Inflate(float d) => IsEmpty ? this : new BoxF(X - d, Y - d, W + 2 * d, H + 2 * d);

    public bool Intersects(BoxF o) =>
        !IsEmpty && !o.IsEmpty && X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    public bool Contains(float px, float py) => !IsEmpty && px >= X && px <= Right && py >= Y && py <= Bottom;

    /// <summary>Distance from a point to this box (0 when inside).</summary>
    public float DistanceTo(float px, float py)
    {
        float dx = Math.Max(Math.Max(X - px, 0), px - Right);
        float dy = Math.Max(Math.Max(Y - py, 0), py - Bottom);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>Where a word is drawn on its page. A word hyphenated across two lines has a second box.</summary>
public readonly record struct WordGeometry(BoxF Box, BoxF Box2)
{
    public bool HasBox => !Box.IsEmpty;
}

/// <summary>One word as it comes out of the PDF, before clean-up.</summary>
public sealed record TextToken(string Text, BoxF Box = default, float FontSize = 0, bool Bold = false);

/// <summary>One text line of a layout block.</summary>
public sealed class TextLineInput
{
    public TextLineInput(IEnumerable<TextToken> tokens) => Tokens = tokens.ToList();
    public IReadOnlyList<TextToken> Tokens { get; }

    public static TextLineInput FromText(string line) =>
        new(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => new TextToken(w)));
}

/// <summary>Everything the extractor found on one page.</summary>
public sealed class PageInput
{
    public required int Number { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
    public List<List<TextLineInput>> Blocks { get; init; } = new();
    public List<BoxF> Images { get; init; } = new();
}

/// <summary>Page size and the pictures on it.</summary>
public sealed class PageInfo
{
    public PageInfo(int number, float width, float height, IReadOnlyList<BoxF> images)
    {
        Number = number;
        Width = width;
        Height = height;
        Images = images;
    }

    public int Number { get; }
    public float Width { get; }
    public float Height { get; }
    public IReadOnlyList<BoxF> Images { get; }
}
