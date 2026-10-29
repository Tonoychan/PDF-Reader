using System.Text.Json;

namespace PdfReader.Core;

/// <summary>A book the library knows about: opened before, found in the books folder, or both.</summary>
public sealed class LibraryEntry
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public int PageCount { get; set; }
    /// <summary>Page you were on last time (0 = never opened).</summary>
    public int Page { get; set; }
    /// <summary>How far through the book, 0..1.</summary>
    public double Progress { get; set; }
    public DateTime? LastOpenedUtc { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Found in the books folder on the last scan.</summary>
    public bool InFolder { get; set; }

    public bool WasOpened => LastOpenedUtc is not null;
}

/// <summary>The library: recently opened books and the PDFs in a books folder. Stored as JSON.</summary>
public sealed class LibraryStore
{
    public List<LibraryEntry> Entries { get; set; } = new();
    /// <summary>Folder books the user removed from the library; a rescan won't bring them back.</summary>
    public List<string> Hidden { get; set; } = new();

    public const int MaxRecent = 40;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDF Reader", "library.json");

    public static LibraryStore Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new LibraryStore();
            var s = JsonSerializer.Deserialize<LibraryStore>(File.ReadAllText(path), Json) ?? new LibraryStore();
            s.Entries ??= new();
            s.Hidden ??= new();
            s.Entries = s.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Path))
                .GroupBy(e => e.Path, PathComparer).Select(g => g.First()).ToList();
            return s;
        }
        catch
        {
            return new LibraryStore();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    public LibraryEntry? Find(string path) => Entries.FirstOrDefault(e => PathComparer.Equals(e.Path, path));

    /// <summary>Recently opened books, newest first.</summary>
    public IReadOnlyList<LibraryEntry> Recent =>
        Entries.Where(e => e.WasOpened).OrderByDescending(e => e.LastOpenedUtc).Take(MaxRecent).ToList();

    /// <summary>Books found in the books folder, by title.</summary>
    public IReadOnlyList<LibraryEntry> FolderBooks =>
        Entries.Where(e => e.InFolder && !Hidden.Contains(e.Path, PathComparer))
            .OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>A book was opened: it goes to the top of "Continue reading".</summary>
    public LibraryEntry Touch(string path, string title, int pageCount, DateTime? nowUtc = null)
    {
        var e = Find(path);
        if (e is null) { e = new LibraryEntry { Path = path }; Entries.Add(e); }
        e.Title = title;
        e.PageCount = pageCount;
        e.LastOpenedUtc = nowUtc ?? DateTime.UtcNow;
        Hidden.RemoveAll(h => PathComparer.Equals(h, path));
        TrimRecent();
        return e;
    }

    public void UpdateProgress(string path, int page, double progress)
    {
        var e = Find(path);
        if (e is null) return;
        e.Page = Math.Max(0, page);
        e.Progress = Math.Clamp(progress, 0, 1);
    }

    /// <summary>Removes a book from "Continue reading" (and from the folder list if <paramref name="alsoFromFolder"/>).</summary>
    public void Remove(string path, bool alsoFromFolder)
    {
        var e = Find(path);
        if (e is null) return;
        if (alsoFromFolder && e.InFolder && !Hidden.Contains(path, PathComparer)) Hidden.Add(path);
        if (e.InFolder && !alsoFromFolder) e.LastOpenedUtc = null;
        else Entries.Remove(e);
    }

    /// <summary>Applies a folder scan: new PDFs are added, ones no longer there leave the folder list.</summary>
    public void ApplyFolderScan(IEnumerable<string> pdfPaths)
    {
        var found = new HashSet<string>(pdfPaths, PathComparer);
        foreach (var e in Entries) e.InFolder = found.Contains(e.Path);
        foreach (var p in found)
            if (Find(p) is null)
                Entries.Add(new LibraryEntry { Path = p, Title = System.IO.Path.GetFileNameWithoutExtension(p), InFolder = true });
        Entries.RemoveAll(e => !e.InFolder && !e.WasOpened);
    }

    /// <summary>Forgets the folder (folder-only books disappear; opened books stay in "Continue reading").</summary>
    public void ClearFolder() => ApplyFolderScan(Array.Empty<string>());

    private void TrimRecent()
    {
        var opened = Entries.Where(e => e.WasOpened).OrderByDescending(e => e.LastOpenedUtc).ToList();
        foreach (var old in opened.Skip(MaxRecent))
        {
            if (old.InFolder) old.LastOpenedUtc = null;
            else Entries.Remove(old);
        }
    }

    /// <summary>All PDFs under a folder (subfolders included), up to <paramref name="max"/>.</summary>
    public static List<string> ScanFolder(string folder, int max = 5000)
    {
        var list = new List<string>();
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
            foreach (var f in Directory.EnumerateFiles(folder, "*.pdf", options))
            {
                list.Add(f);
                if (list.Count >= max) break;
            }
        }
        catch { }
        return list;
    }
}
