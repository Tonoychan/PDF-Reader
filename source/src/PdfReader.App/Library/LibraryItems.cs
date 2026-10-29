using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using PdfReader.Core;

namespace PdfReader.App.Library;

/// <summary>One book card on the library screen.</summary>
public sealed class LibraryCard : INotifyPropertyChanged
{
    private ImageSource? _cover;
    private bool _requested;

    public LibraryCard(LibraryEntry entry, bool isCurrent)
    {
        Entry = entry;
        IsCurrent = isCurrent;
        IsMissing = !File.Exists(entry.Path);
    }

    public LibraryEntry Entry { get; }
    public string Path => Entry.Path;
    public string Title => string.IsNullOrWhiteSpace(Entry.Title) ? System.IO.Path.GetFileNameWithoutExtension(Entry.Path) : Entry.Title;
    public bool IsMissing { get; }
    public bool IsCurrent { get; }

    public string Subtitle
    {
        get
        {
            if (IsMissing) return "File not found";
            if (IsCurrent) return Entry.PageCount > 0 ? $"Reading now · page {Math.Max(1, Entry.Page)} of {Entry.PageCount}" : "Reading now";
            if (!Entry.WasOpened || Entry.Page <= 0) return Entry.PageCount > 0 ? $"Not started · {Entry.PageCount} pages" : "Not started";
            return Entry.PageCount > 0 ? $"Page {Entry.Page} of {Entry.PageCount} · {Entry.Progress * 100:0}%" : $"Page {Entry.Page}";
        }
    }

    public string LastRead => Entry.LastOpenedUtc is { } t ? "Last read " + Describe(t) : "";
    public double ProgressPercent => Entry.Progress * 100;
    public bool ShowProgress => Entry.WasOpened && Entry.Progress > 0 && !IsMissing;
    public string Initial => Title.Length > 0 ? Title[..1].ToUpperInvariant() : "?";

    public ImageSource? Cover
    {
        get
        {
            if (!_requested && !IsMissing)
            {
                _requested = true;
                _ = LoadCoverAsync();
            }
            return _cover;
        }
    }

    public bool HasCover => _cover is not null;

    private async Task LoadCoverAsync()
    {
        _cover = await CoverCache.GetAsync(Entry.Path);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCover)));
    }

    private static string Describe(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var days = (DateTime.Now.Date - local.Date).Days;
        return days switch
        {
            0 => "today, " + local.ToString("h:mm tt"),
            1 => "yesterday",
            < 7 => local.ToString("dddd"),
            _ => local.ToString("d MMM yyyy"),
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A row in the Bookmarks tab.</summary>
public sealed class BookmarkItem : INotifyPropertyChanged
{
    public BookmarkItem(Bookmark bookmark) => Bookmark = bookmark;

    public Bookmark Bookmark { get; }
    public string PageLabel => $"Page {Bookmark.Page}";
    public string Snippet => Bookmark.Snippet;

    public string Note
    {
        get => Bookmark.Note;
        set
        {
            if (Bookmark.Note == value) return;
            Bookmark.Note = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Note)));
            Changed?.Invoke();
        }
    }

    /// <summary>Raised when the note text changes (so the window can save).</summary>
    public event Action? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;
}
