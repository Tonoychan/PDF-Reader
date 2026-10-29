using System.Text.Json;

namespace PdfReader.Core;

/// <summary>Where you left off in one book.</summary>
public sealed class SavedPosition
{
    public int Paragraph { get; set; }
    public int Char { get; set; }
    public DateTime SavedAt { get; set; }
}

/// <summary>User settings, stored as JSON in %AppData%\PDF Reader\settings.json.</summary>
public sealed class AppSettings
{
    public string? VoiceKey { get; set; }
    public double Rate { get; set; } = 1.0;
    public bool FollowReading { get; set; } = true;
    public double FontSize { get; set; } = 19;
    public string? LastFolder { get; set; }
    /// <summary>"page" (the real PDF pages) or "text" (cleaned-up text with headings and pictures).</summary>
    public string ViewMode { get; set; } = "page";
    /// <summary>Page view zoom in screen units per PDF point; null = fit to width.</summary>
    public double? PageZoom { get; set; }
    public ReadingOptions Reading { get; set; } = new();
    public Dictionary<string, SavedPosition> Positions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxRememberedBooks = 200;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDF Reader", "settings.json");

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
            s.Positions = new Dictionary<string, SavedPosition>(s.Positions ?? new(), StringComparer.OrdinalIgnoreCase);
            if (s.Rate is < 0.5 or > 3.0 or double.NaN) s.Rate = 1.0;
            if (s.FontSize is < 10 or > 40 or double.NaN) s.FontSize = 19;
            if (s.PageZoom is < 0.2 or > 8 or double.NaN) s.PageZoom = null;
            if (s.ViewMode is not ("page" or "text")) s.ViewMode = "page";
            s.Reading ??= new ReadingOptions();
            return s;
        }
        catch
        {
            return new AppSettings(); // a broken settings file must never stop the app from starting
        }
    }

    public void Save(string path)
    {
        try
        {
            if (Positions.Count > MaxRememberedBooks)
            {
                foreach (var old in Positions.OrderBy(kv => kv.Value.SavedAt).Take(Positions.Count - MaxRememberedBooks).ToList())
                    Positions.Remove(old.Key);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // best effort
        }
    }

    public void Remember(string bookPath, ReadingPosition pos) =>
        Positions[bookPath] = new SavedPosition { Paragraph = pos.ParagraphIndex, Char = pos.CharOffset, SavedAt = DateTime.UtcNow };

    public ReadingPosition? Recall(string bookPath, BookDocument book)
    {
        if (!Positions.TryGetValue(bookPath, out var p)) return null;
        if (p.Paragraph < 0 || p.Paragraph >= book.Paragraphs.Count) return null;
        int c = Math.Clamp(p.Char, 0, book.Paragraphs[p.Paragraph].Text.Length);
        if (p.Paragraph == 0 && c == 0) return null;
        return new ReadingPosition(p.Paragraph, c);
    }
}
