using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PdfReader.Core;

/// <summary>
/// Saves the extracted text of a book so it opens instantly next time.
/// The cache entry is tied to the file's path, size and modified time, so an edited PDF is re-read.
/// </summary>
public static class BookCache
{
    /// <summary>Bump when extraction or classification changes, so old cache files are ignored.</summary>
    public const int FormatVersion = 3;
    private const uint Magic = 0x4B4F4F42; // "BOOK"
    private const int MaxEntries = 60;

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDF Reader", "cache");

    public static string KeyFor(string filePath, long size, DateTime lastWriteUtc)
    {
        string id = $"{Path.GetFullPath(filePath).ToLowerInvariant()}|{size}|{lastWriteUtc.Ticks}|{FormatVersion}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(id)))[..24].ToLowerInvariant();
    }

    public static string? KeyFor(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return info.Exists ? KeyFor(filePath, info.Length, info.LastWriteTimeUtc) : null;
        }
        catch { return null; }
    }

    public static BookDocument? TryLoad(string folder, string filePath)
    {
        try
        {
            string? key = KeyFor(filePath);
            if (key is null) return null;
            string path = Path.Combine(folder, key + ".book");
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var book = Read(fs, filePath);
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return book;
        }
        catch
        {
            return null; // a damaged cache file just means the book is read again
        }
    }

    public static void TrySave(string folder, BookDocument book)
    {
        try
        {
            string? key = KeyFor(book.FilePath);
            if (key is null) return;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, key + ".book");
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp)) Write(fs, book);
            File.Move(tmp, path, overwrite: true);
            Prune(folder);
        }
        catch
        {
            // caching is best effort
        }
    }

    private static void Prune(string folder)
    {
        var files = new DirectoryInfo(folder).GetFiles("*.book").OrderByDescending(f => f.LastAccessTimeUtc).ToList();
        foreach (var old in files.Skip(MaxEntries)) { try { old.Delete(); } catch { } }
    }

    // ---- binary format ----------------------------------------------------------------------------

    public static void Write(Stream output, BookDocument book)
    {
        using var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
        using var w = new BinaryWriter(gz, Encoding.UTF8);
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(book.PageCount);

        w.Write(book.Pages.Count);
        foreach (var p in book.Pages)
        {
            w.Write(p.Number); w.Write(p.Width); w.Write(p.Height);
            w.Write(p.Images.Count);
            foreach (var b in p.Images) WriteBox(w, b);
        }

        w.Write(book.Paragraphs.Count);
        foreach (var p in book.Paragraphs)
        {
            w.Write(p.PageNumber);
            w.Write((byte)p.Kind);
            w.Write(p.FontSize);
            w.Write(p.Text);
            w.Write(p.Geometry.Count);
            foreach (var g in p.Geometry)
            {
                WriteBox(w, g.Box);
                bool two = !g.Box2.IsEmpty;
                w.Write(two);
                if (two) WriteBox(w, g.Box2);
            }
        }

        w.Write(book.Outline.Count);
        foreach (var o in book.Outline)
        {
            w.Write(o.Title); w.Write(o.Level); w.Write(o.PageNumber); w.Write(o.ParagraphIndex);
        }
    }

    public static BookDocument? Read(Stream input, string filePath)
    {
        using var gz = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var r = new BinaryReader(gz, Encoding.UTF8);
        if (r.ReadUInt32() != Magic || r.ReadInt32() != FormatVersion) return null;
        int pageCount = r.ReadInt32();

        int nPages = r.ReadInt32();
        var pages = new List<PageInfo>(nPages);
        for (int i = 0; i < nPages; i++)
        {
            int number = r.ReadInt32(); float width = r.ReadSingle(), height = r.ReadSingle();
            int nImages = r.ReadInt32();
            var images = new List<BoxF>(nImages);
            for (int k = 0; k < nImages; k++) images.Add(ReadBox(r));
            pages.Add(new PageInfo(number, width, height, images));
        }

        int nParas = r.ReadInt32();
        var paragraphs = new List<Paragraph>(nParas);
        for (int i = 0; i < nParas; i++)
        {
            int page = r.ReadInt32();
            var kind = (ParagraphKind)r.ReadByte();
            float size = r.ReadSingle();
            string text = r.ReadString();
            int nGeo = r.ReadInt32();
            var geo = new WordGeometry[nGeo];
            for (int k = 0; k < nGeo; k++)
            {
                var box = ReadBox(r);
                var box2 = r.ReadBoolean() ? ReadBox(r) : default;
                geo[k] = new WordGeometry(box, box2);
            }
            paragraphs.Add(new Paragraph(i, page, text, kind, geo, size));
        }

        int nOutline = r.ReadInt32();
        var outline = new List<OutlineEntry>(nOutline);
        for (int i = 0; i < nOutline; i++)
            outline.Add(new OutlineEntry(r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));

        return new BookDocument(filePath, pageCount, paragraphs, pages.Count > 0 ? pages : null, outline);
    }

    private static void WriteBox(BinaryWriter w, BoxF b) { w.Write(b.X); w.Write(b.Y); w.Write(b.W); w.Write(b.H); }
    private static BoxF ReadBox(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}
