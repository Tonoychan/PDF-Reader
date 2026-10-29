using System.Windows;
using PdfReader.Core;

namespace PdfReader.App.Reader;

/// <summary>A row of the Contents list.</summary>
public sealed class OutlineItem
{
    public OutlineItem(OutlineEntry entry)
    {
        Entry = entry;
        Indent = new Thickness(entry.Level * 16, 0, 0, 0);
    }

    public OutlineEntry Entry { get; }
    public string Title => Entry.Title;
    public string PageLabel => Entry.PageNumber.ToString();
    public Thickness Indent { get; }
    public bool IsTopLevel => Entry.Level == 0;
}

/// <summary>A row of the Search results.</summary>
public sealed class SearchItem
{
    public SearchItem(SearchHit hit) => Hit = hit;

    public SearchHit Hit { get; }
    public string PageLabel => $"Page {Hit.PageNumber}";
    public string Before => Hit.Before;
    public string Match => Hit.Match;
    public string After => Hit.After;
}
