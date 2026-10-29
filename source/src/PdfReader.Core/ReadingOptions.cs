namespace PdfReader.Core;

/// <summary>What to skip while reading aloud. Everything extra is skipped by default.</summary>
public sealed class ReadingOptions
{
    public bool SkipHeadersFooters { get; set; } = true;
    public bool SkipFootnotes { get; set; } = true;
    public bool SkipTableOfContents { get; set; } = true;
    public bool SkipCaptions { get; set; } = true;
    public bool PauseAroundHeadings { get; set; } = true;

    public bool IsSpoken(Paragraph p) => p.Kind switch
    {
        ParagraphKind.HeaderFooter => !SkipHeadersFooters,
        ParagraphKind.Footnote => !SkipFootnotes,
        ParagraphKind.TableOfContents => !SkipTableOfContents,
        ParagraphKind.Caption => !SkipCaptions,
        _ => true,
    };

    public ReadingOptions Clone() => (ReadingOptions)MemberwiseClone();
}
