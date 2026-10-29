using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using PdfReader.Core;

namespace PdfReader.App.Reader;

/// <summary>"Page 12" divider shown between pages in the reader.</summary>
public sealed class PageHeaderItem
{
    public PageHeaderItem(int pageNumber, int pageCount)
    {
        PageNumber = pageNumber;
        Label = $"Page {pageNumber} of {pageCount}";
    }

    public int PageNumber { get; }
    public string Label { get; }
}

/// <summary>One paragraph in the reader, plus which of its words are highlighted.</summary>
public sealed class ParagraphItem : INotifyPropertyChanged
{
    private int _readingWord = -1;
    private int _cursorWord = -1;
    private bool _isActive;
    private bool _isSkipped;

    public ParagraphItem(Paragraph model) => Model = model;

    public Paragraph Model { get; }
    public ParagraphKind Kind => Model.Kind;

    /// <summary>True when the reading options skip this paragraph (shown dimmed).</summary>
    public bool IsSkipped { get => _isSkipped; set => Set(ref _isSkipped, value); }

    /// <summary>Word being read (or where reading will resume), -1 for none.</summary>
    public int ReadingWord { get => _readingWord; set => Set(ref _readingWord, value); }

    /// <summary>Word the user clicked to read from next, -1 for none.</summary>
    public int CursorWord { get => _cursorWord; set => Set(ref _cursorWord, value); }

    /// <summary>True for the paragraph currently being read.</summary>
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A picture from the PDF shown in the text view (drawn from the page on demand).</summary>
public sealed class ImageItem : INotifyPropertyChanged
{
    private ImageSource? _bitmap;
    private bool _requested;

    public ImageItem(int pageNumber, BoxF box)
    {
        PageNumber = pageNumber;
        Box = box;
        double w = box.W * 96.0 / 72.0;
        double scale = w > MaxWidth ? MaxWidth / w : 1;
        DisplayWidth = w * scale;
        DisplayHeight = box.H * 96.0 / 72.0 * scale;
    }

    public const double MaxWidth = 780;

    public int PageNumber { get; }
    public BoxF Box { get; }
    public double DisplayWidth { get; }
    public double DisplayHeight { get; }

    /// <summary>Set by the window: renders this picture.</summary>
    public Func<ImageItem, Task<ImageSource?>>? Loader { get; set; }

    public ImageSource? Bitmap
    {
        get
        {
            if (!_requested && Loader is { } load)
            {
                _requested = true;
                _ = LoadAsync(load);
            }
            return _bitmap;
        }
    }

    private async Task LoadAsync(Func<ImageItem, Task<ImageSource?>> load)
    {
        var bmp = await load(this);
        if (bmp is null) { _requested = false; return; }
        _bitmap = bmp;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bitmap)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Multiplies a number binding by the converter parameter (used for heading/footnote sizes).</summary>
public sealed class MultiplyConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        double factor = double.TryParse(parameter?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 1;
        return value is double d ? d * factor : value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
