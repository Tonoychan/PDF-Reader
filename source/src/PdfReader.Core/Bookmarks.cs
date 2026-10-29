using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PdfReader.Core;

/// <summary>A saved place in a book, optionally with a note.</summary>
public sealed class Bookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Paragraph { get; set; }
    public int Char { get; set; }
    public int Page { get; set; }
    /// <summary>The text at the bookmark, so the list shows what's there.</summary>
    public string Snippet { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public ReadingPosition Position => new(Paragraph, Char);
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
}

/// <summary>The bookmarks and notes of one book, saved as JSON next to the other app data.</summary>
public sealed class BookNotes
{
    public string BookPath { get; set; } = "";
    public List<Bookmark> Items { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDF Reader", "notes");

    public static string FileFor(string folder, string bookPath)
    {
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(bookPath).ToLowerInvariant())))[..20].ToLowerInvariant();
        return Path.Combine(folder, key + ".json");
    }

    public static BookNotes Load(string folder, string bookPath)
    {
        try
        {
            string file = FileFor(folder, bookPath);
            if (File.Exists(file))
            {
                var n = JsonSerializer.Deserialize<BookNotes>(File.ReadAllText(file), Json);
                if (n is not null)
                {
                    n.BookPath = bookPath;
                    n.Items ??= new();
                    n.Sort();
                    return n;
                }
            }
        }
        catch { }
        return new BookNotes { BookPath = bookPath };
    }

    public void Save(string folder)
    {
        try
        {
            string file = FileFor(folder, BookPath);
            if (Items.Count == 0)
            {
                if (File.Exists(file)) File.Delete(file);
                return;
            }
            Directory.CreateDirectory(folder);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, file, overwrite: true);
        }
        catch { }
    }

    /// <summary>Adds a bookmark at a word (or returns the one already there).</summary>
    public Bookmark Add(BookDocument book, ReadingPosition pos, string note = "")
    {
        var para = book.Paragraphs[Math.Clamp(pos.ParagraphIndex, 0, book.Paragraphs.Count - 1)];
        int word = Math.Max(0, para.WordIndexAt(pos.CharOffset));
        int ch = para.Words.Count > 0 ? para.Words[word].Start : 0;

        var existing = Items.FirstOrDefault(b => b.Paragraph == para.Index && b.Char == ch);
        if (existing is not null)
        {
            if (note.Length > 0) existing.Note = string.IsNullOrWhiteSpace(existing.Note) ? note : existing.Note + "\n" + note;
            return existing;
        }

        var b = new Bookmark
        {
            Paragraph = para.Index,
            Char = ch,
            Page = para.PageNumber,
            Snippet = SnippetAt(para, ch),
            Note = note,
        };
        Items.Add(b);
        Sort();
        return b;
    }

    public bool Remove(Guid id) => Items.RemoveAll(b => b.Id == id) > 0;

    /// <summary>Bookmarks in reading order.</summary>
    public void Sort() => Items.Sort((a, b) => a.Paragraph != b.Paragraph ? a.Paragraph.CompareTo(b.Paragraph) : a.Char.CompareTo(b.Char));

    /// <summary>Bookmarks on a page (for the markers in the page view).</summary>
    public IEnumerable<Bookmark> OnPage(int page) => Items.Where(b => b.Page == page);

    /// <summary>~90 characters starting at the word, ending on a word boundary.</summary>
    public static string SnippetAt(Paragraph p, int start)
    {
        start = Math.Clamp(start, 0, p.Text.Length);
        string rest = p.Text[start..];
        if (rest.Length <= 90) return rest;
        int cut = rest.LastIndexOf(' ', 90);
        return rest[..(cut > 40 ? cut : 90)] + "…";
    }

    /// <summary>All bookmarks and notes as Markdown, for saving or sharing.</summary>
    public string ToMarkdown(string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Notes — {title}");
        sb.AppendLine();
        foreach (var b in Items)
        {
            sb.AppendLine($"## Page {b.Page}");
            sb.AppendLine($"> {b.Snippet.Replace("\n", " ")}");
            if (b.HasNote)
            {
                sb.AppendLine();
                sb.AppendLine(b.Note.Trim());
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
