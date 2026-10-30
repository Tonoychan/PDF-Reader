using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfReader.App.Pdf;
using PdfReader.Core;

namespace PdfReader.App.Library;

/// <summary>
/// Small first-page pictures for the library, saved as PNG files so they appear instantly next time.
/// Covers are drawn two at a time so a big books folder doesn't slow the app down.
/// </summary>
public static class CoverCache
{
    public const int PixelWidth = 260;
    private static readonly SemaphoreSlim Gate = new(2, 2);

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDF Reader", "covers");

    private static string FileFor(string pdfPath)
    {
        string key = BookCache.KeyFor(pdfPath)
            ?? Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(pdfPath.ToLowerInvariant())))[..24].ToLowerInvariant();
        return Path.Combine(Folder, key + ".png");
    }

    /// <summary>The saved cover picture for a book, if it has been made.</summary>
    public static string? SavedFile(string pdfPath)
    {
        try
        {
            string file = FileFor(pdfPath);
            return File.Exists(file) ? file : null;
        }
        catch { return null; }
    }

    public static async Task<ImageSource?> GetAsync(string pdfPath)
    {
        if (!File.Exists(pdfPath)) return null;
        string file = FileFor(pdfPath);

        var cached = await Task.Run(() => LoadPng(file));
        if (cached is not null) return cached;

        await Gate.WaitAsync();
        try
        {
            using var renderer = await PdfPageRenderer.OpenAsync(pdfPath);
            var bitmap = await renderer.RenderAsync(1, PixelWidth, CancellationToken.None);
            if (bitmap is null) return null;
            await Task.Run(() => SavePng(bitmap, file));
            return bitmap;
        }
        catch
        {
            return null; // no cover: the card shows a plain placeholder
        }
        finally
        {
            Gate.Release();
        }
    }

    private static BitmapSource? LoadPng(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static void SavePng(BitmapSource bitmap, string file)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string tmp = file + ".tmp";
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            File.Move(tmp, file, overwrite: true);
        }
        catch { }
    }
}
