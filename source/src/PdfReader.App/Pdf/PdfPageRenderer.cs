using System.IO;
using System.Windows.Media.Imaging;
using PdfReader.Core;
using global::Windows.Storage;
using global::Windows.Storage.Streams;
using WinPdf = global::Windows.Data.Pdf;

namespace PdfReader.App.Pdf;

/// <summary>
/// Draws PDF pages to bitmaps with the PDF engine built into Windows,
/// so pages look exactly like they do in a normal PDF viewer.
/// </summary>
public sealed class PdfPageRenderer : IDisposable
{
    private readonly WinPdf.PdfDocument _doc;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    private PdfPageRenderer(WinPdf.PdfDocument doc) => _doc = doc;

    public int PageCount => (int)_doc.PageCount;

    /// <summary>Recolouring for whole pages (theme). Pictures cut out for the text view keep their colours.</summary>
    public PageTint Tint { get; set; }

    public static async Task<PdfPageRenderer> OpenAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var doc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
        return new PdfPageRenderer(doc);
    }

    /// <summary>
    /// Renders page <paramref name="pageNumber"/> (1-based) at <paramref name="pixelWidth"/> pixels wide.
    /// With <paramref name="region"/> (points from the top-left, page size <paramref name="pageWidth"/> × <paramref name="pageHeight"/>)
    /// only that part of the page is drawn — used for pictures in the text view.
    /// </summary>
    public async Task<BitmapSource?> RenderAsync(int pageNumber, int pixelWidth, CancellationToken ct,
        BoxF? region = null, float pageWidth = 0, float pageHeight = 0)
    {
        if (_disposed || pageNumber < 1 || pageNumber > PageCount || pixelWidth < 8) return null;
        // Cancelled while waiting for another page to finish: nothing to draw (and nothing to release).
        try { await _lock.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        catch (ObjectDisposedException) { return null; }
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed) return null;

            using var page = _doc.GetPage((uint)(pageNumber - 1));
            var options = new WinPdf.PdfPageRenderOptions { DestinationWidth = (uint)Math.Min(pixelWidth, 6000) };

            if (region is { } r && pageWidth > 0 && pageHeight > 0)
            {
                var size = page.Size; // same page, the renderer's own units
                double sx = size.Width / pageWidth, sy = size.Height / pageHeight;
                options.SourceRect = new global::Windows.Foundation.Rect(
                    Math.Max(0, r.X * sx), Math.Max(0, r.Y * sy), Math.Max(1, r.W * sx), Math.Max(1, r.H * sy));
            }

            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options).AsTask(ct).ConfigureAwait(false);
            stream.Seek(0);

            var memory = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(memory, ct).ConfigureAwait(false);
            memory.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();

            var tint = Tint;
            if (region is null && tint != PageTint.None)
            {
                ct.ThrowIfCancellationRequested();
                return Recolour(bitmap, tint);
            }
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null; // a page that fails to draw stays blank rather than crashing the reader
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Page size in points, as Windows sees it (fallback when text extraction failed on a page).</summary>
    public (float Width, float Height) PageSizePoints(int pageNumber)
    {
        try
        {
            using var page = _doc.GetPage((uint)(pageNumber - 1));
            return ((float)(page.Size.Width * 0.75), (float)(page.Size.Height * 0.75)); // DIPs → points
        }
        catch
        {
            return (612, 792);
        }
    }

    public void Dispose() => _disposed = true;

    private static BitmapSource Recolour(BitmapSource source, PageTint tint)
    {
        var bgra = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        PageTinter.Apply(pixels, tint);
        var result = BitmapSource.Create(w, h, source.DpiX, source.DpiY, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
