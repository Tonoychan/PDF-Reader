using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using PdfReader.App.Pdf;
using PdfReader.Core;

namespace PdfReader.App.Reader;

/// <summary>One rendered PDF page with a transparent layer on top for highlights.</summary>
internal sealed class PageSlot : Grid
{
    private readonly Image _image;
    private CancellationTokenSource? _cts;
    private int _renderedPx;
    private int _pendingPx;

    public PageSlot(int pageNumber, float width, float height)
    {
        PageNumber = pageNumber;
        PageWidth = Math.Max(1, width);
        PageHeight = Math.Max(1, height);
        Background = Brushes.White;
        Cursor = Cursors.Hand;
        Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 1, Opacity = 0.16, Color = Colors.Black };
        HorizontalAlignment = HorizontalAlignment.Center;

        _image = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
        Children.Add(_image);
        Children.Add(Overlay);
    }

    public int PageNumber { get; }
    public float PageWidth { get; }
    public float PageHeight { get; }
    public Canvas Overlay { get; }

    public void SetScale(double scale)
    {
        Width = PageWidth * scale;
        Height = PageHeight * scale;
    }

    public async void EnsureRendered(PdfPageRenderer renderer, double dpiScale)
    {
        int px = (int)Math.Round(Width * dpiScale);
        if (px <= 0 || px == _renderedPx || px == _pendingPx) return;

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _pendingPx = px;

        var bitmap = await renderer.RenderAsync(PageNumber, px, cts.Token);
        if (!ReferenceEquals(_cts, cts) || cts.IsCancellationRequested) return;
        _pendingPx = 0;
        if (bitmap is not null)
        {
            _image.Source = bitmap;
            _renderedPx = px;
        }
    }

    public void Release()
    {
        if (_image.Source is null && _pendingPx == 0) return;
        _cts?.Cancel();
        _cts = null;
        _pendingPx = 0;
        _renderedPx = 0;
        _image.Source = null;
    }
}

/// <summary>
/// The real PDF pages in one vertical scroll. The word being read is highlighted on the page,
/// clicking a word picks it, and Ctrl + mouse wheel zooms around the mouse.
/// </summary>
public sealed class PagesView : Grid
{
    private const double Gap = 18;
    private const double Pad = 22;

    private readonly ScrollViewer _scroller;
    private readonly StackPanel _panel;
    private readonly List<PageSlot> _slots = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly Rectangle _paragraphMark;
    private readonly Rectangle _wordMark;
    private readonly Rectangle _wordMark2;
    private readonly Rectangle _cursorMark;
    private double[] _tops = Array.Empty<double>();
    private Dictionary<int, List<(int Para, int Word, BoxF Box)>> _wordsByPage = new();
    private BookDocument? _book;
    private PdfPageRenderer? _renderer;
    private double _zoom = 1.3;
    private bool _fitPending;
    private int _readPara = -1, _readWord = -1, _cursorPara = -1, _cursorWord = -1;

    public PagesView()
    {
        Background = Brushes.Transparent;
        _panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, Pad, 24, Pad) };
        _scroller = new ScrollViewer
        {
            Content = _panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
            CanContentScroll = false,
        };
        Children.Add(_scroller);

        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); RenderVisible(); };

        _scroller.ScrollChanged += (_, _) => ScheduleRender();
        _scroller.SizeChanged += (_, _) =>
        {
            if (_fitPending) FitWidth();
            ScheduleRender();
        };
        _scroller.PreviewMouseWheel += OnMouseWheel;

        _paragraphMark = MakeMark(Color.FromArgb(0x1C, 0x2B, 0x5B, 0xD7), null, 0, 6);
        _wordMark = MakeMark(Color.FromArgb(0x70, 0xFF, 0xC4, 0x1A), Color.FromArgb(0xD0, 0xE8, 0xA4, 0x00), 1, 2);
        _wordMark2 = MakeMark(Color.FromArgb(0x70, 0xFF, 0xC4, 0x1A), Color.FromArgb(0xD0, 0xE8, 0xA4, 0x00), 1, 2);
        _cursorMark = MakeMark(Color.FromArgb(0x30, 0x2B, 0x5B, 0xD7), Color.FromArgb(0xFF, 0x2B, 0x5B, 0xD7), 2, 2);
        Panel.SetZIndex(_paragraphMark, 0);
        Panel.SetZIndex(_wordMark, 1);
        Panel.SetZIndex(_wordMark2, 1);
        Panel.SetZIndex(_cursorMark, 2);
    }

    /// <summary>(paragraph index, word index, click count)</summary>
    public event Action<int, int, int>? WordClicked;
    /// <summary>The user scrolled by hand (so auto-follow should pause).</summary>
    public event Action? UserScrolled;
    public event Action<double>? ZoomChanged;

    public ScrollViewer Scroller => _scroller;
    public double Zoom => _zoom;

    /// <summary>Which paragraphs are read aloud; clicks prefer words in those.</summary>
    public Func<int, bool>? IsSpokenParagraph { get; set; }

    // ---- loading ---------------------------------------------------------------------------------

    public void Load(BookDocument book, PdfPageRenderer renderer, double? zoom)
    {
        Clear();
        _book = book;
        _renderer = renderer;

        for (int n = 1; n <= book.PageCount; n++)
        {
            var info = book.Page(n);
            var (w, h) = info is { Width: > 0, Height: > 0 } ? (info.Width, info.Height) : renderer.PageSizePoints(n);
            var slot = new PageSlot(n, w, h) { Margin = new Thickness(0, 0, 0, Gap) };
            slot.MouseLeftButtonDown += OnSlotMouseDown;
            _slots.Add(slot);
            _panel.Children.Add(slot);
        }

        _wordsByPage = new Dictionary<int, List<(int, int, BoxF)>>();
        foreach (var p in book.Paragraphs)
        {
            if (!p.HasGeometry) continue;
            if (!_wordsByPage.TryGetValue(p.PageNumber, out var list)) _wordsByPage[p.PageNumber] = list = new();
            for (int i = 0; i < p.Geometry.Count; i++)
            {
                list.Add((p.Index, i, p.Geometry[i].Box));
                if (!p.Geometry[i].Box2.IsEmpty) list.Add((p.Index, i, p.Geometry[i].Box2));
            }
        }

        if (zoom is { } z) { _zoom = z; _fitPending = false; }
        else _fitPending = true;
        ApplyZoom();
        _scroller.ScrollToTop();
        Dispatcher.InvokeAsync(() => { if (_fitPending) FitWidth(); ScheduleRender(); }, DispatcherPriority.Loaded);
    }

    public void Clear()
    {
        foreach (var s in _slots) s.Release();
        foreach (var mark in new[] { _paragraphMark, _wordMark, _wordMark2, _cursorMark })
            (mark.Parent as Canvas)?.Children.Remove(mark);
        _slots.Clear();
        _panel.Children.Clear();
        _tops = Array.Empty<double>();
        _wordsByPage.Clear();
        _book = null;
        _renderer = null;
        _readPara = _readWord = _cursorPara = _cursorWord = -1;
    }

    // ---- zoom ------------------------------------------------------------------------------------

    public void FitWidth()
    {
        if (_slots.Count == 0) return;
        double available = _scroller.ViewportWidth > 0 ? _scroller.ViewportWidth : ActualWidth;
        if (available <= 0) return;
        _fitPending = false;
        double widest = _slots.Max(s => s.PageWidth);
        _zoom = Math.Clamp((available - 72) / widest, 0.3, 1.9);
        ApplyZoom();
    }

    public void ZoomBy(double factor) =>
        ZoomAt(factor, new Point(_scroller.ViewportWidth / 2, _scroller.ViewportHeight / 2));

    private void ZoomAt(double factor, Point anchor)
    {
        if (_slots.Count == 0) return;
        double old = _zoom;
        double zoom = Math.Clamp(old * factor, 0.3, 5.0);
        if (Math.Abs(zoom - old) < 0.0001) return;

        double docY = _scroller.VerticalOffset + anchor.Y;
        double docX = _scroller.HorizontalOffset + anchor.X;
        int page = SlotAt(docY);
        double withinPoints = (docY - _tops[page]) / old;

        _fitPending = false;
        _zoom = zoom;
        ApplyZoom();
        _scroller.UpdateLayout();
        _scroller.ScrollToVerticalOffset(Math.Max(0, _tops[page] + withinPoints * zoom - anchor.Y));
        _scroller.ScrollToHorizontalOffset(Math.Max(0, docX * zoom / old - anchor.X));
        ZoomChanged?.Invoke(zoom);
    }

    private void ApplyZoom()
    {
        _tops = new double[_slots.Count];
        double y = Pad;
        for (int i = 0; i < _slots.Count; i++)
        {
            _slots[i].SetScale(_zoom);
            _tops[i] = y;
            y += _slots[i].Height + Gap;
        }
        UpdateMarks();
        ScheduleRender();
    }

    private int SlotAt(double docY)
    {
        for (int i = _tops.Length - 1; i >= 0; i--)
            if (docY >= _tops[i]) return i;
        return 0;
    }

    // ---- rendering -------------------------------------------------------------------------------

    private void ScheduleRender()
    {
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private void RenderVisible()
    {
        if (_renderer is null || _slots.Count == 0) return;
        double top = _scroller.VerticalOffset;
        double view = Math.Max(1, _scroller.ViewportHeight);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;

        for (int i = 0; i < _slots.Count; i++)
        {
            double y0 = _tops[i], y1 = y0 + _slots[i].Height;
            if (y1 >= top - view && y0 <= top + view * 2) _slots[i].EnsureRendered(_renderer, dpi);
            else if (y1 < top - view * 4 || y0 > top + view * 5) _slots[i].Release();
        }
    }

    // ---- highlights ------------------------------------------------------------------------------

    public void SetReading(int paragraph, int word)
    {
        _readPara = paragraph;
        _readWord = word;
        UpdateMarks();
    }

    public void SetCursor(int paragraph, int word)
    {
        _cursorPara = paragraph;
        _cursorWord = word;
        UpdateMarks();
    }

    private void UpdateMarks()
    {
        if (TryGetWord(_readPara, _readWord, out var p, out var g))
        {
            Place(_paragraphMark, p.PageNumber, p.Bounds, 5);
            Place(_wordMark, p.PageNumber, g.Box, 1.2f);
            if (!g.Box2.IsEmpty) Place(_wordMark2, p.PageNumber, g.Box2, 1.2f);
            else Hide(_wordMark2);
        }
        else
        {
            Hide(_paragraphMark);
            Hide(_wordMark);
            Hide(_wordMark2);
        }

        if (TryGetWord(_cursorPara, _cursorWord, out var cp, out var cg)) Place(_cursorMark, cp.PageNumber, cg.Box, 1.5f);
        else Hide(_cursorMark);
    }

    private bool TryGetWord(int para, int word, out Paragraph paragraph, out WordGeometry geometry)
    {
        paragraph = null!;
        geometry = default;
        if (_book is null || para < 0 || para >= _book.Paragraphs.Count) return false;
        paragraph = _book.Paragraphs[para];
        if (!paragraph.HasGeometry || word < 0 || word >= paragraph.Geometry.Count) return false;
        geometry = paragraph.Geometry[word];
        return geometry.HasBox && paragraph.PageNumber >= 1 && paragraph.PageNumber <= _slots.Count;
    }

    private void Place(Rectangle mark, int pageNumber, BoxF box, float inflate)
    {
        if (box.IsEmpty) { Hide(mark); return; }
        var overlay = _slots[pageNumber - 1].Overlay;
        if (mark.Parent is Canvas current && !ReferenceEquals(current, overlay)) current.Children.Remove(mark);
        if (mark.Parent is null) overlay.Children.Add(mark);

        var b = box.Inflate(inflate);
        Canvas.SetLeft(mark, b.X * _zoom);
        Canvas.SetTop(mark, b.Y * _zoom);
        mark.Width = Math.Max(1, b.W * _zoom);
        mark.Height = Math.Max(1, b.H * _zoom);
        mark.Visibility = Visibility.Visible;
    }

    private static void Hide(Rectangle mark) => mark.Visibility = Visibility.Collapsed;

    private static Rectangle MakeMark(Color fill, Color? stroke, double strokeThickness, double radius) => new()
    {
        Fill = new SolidColorBrush(fill),
        Stroke = stroke is { } s ? new SolidColorBrush(s) : null,
        StrokeThickness = strokeThickness,
        RadiusX = radius,
        RadiusY = radius,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>Scrolls so the word being read sits in the upper third of the view.</summary>
    public void FollowReading(bool force)
    {
        if (!TryGetWord(_readPara, _readWord, out var p, out var g)) return;
        int i = p.PageNumber - 1;
        if (i >= _tops.Length) return;

        double view = _scroller.ViewportHeight;
        if (view <= 0) return;
        double y = _tops[i] + g.Box.Y * _zoom;
        double h = g.Box.H * _zoom;
        double rel = y - _scroller.VerticalOffset;
        if (force || rel < view * 0.10 || rel + h > view * 0.80)
            _scroller.ScrollToVerticalOffset(Math.Max(0, y - view * 0.30));

        // When zoomed in wider than the window, keep the word horizontally in view too.
        if (_scroller.ExtentWidth > _scroller.ViewportWidth + 1)
        {
            try
            {
                var pt = _slots[i].TransformToAncestor(_scroller).Transform(new Point(g.Box.X * _zoom, 0));
                double w = _scroller.ViewportWidth;
                if (pt.X < w * 0.05 || pt.X > w * 0.85)
                    _scroller.ScrollToHorizontalOffset(Math.Max(0, _scroller.HorizontalOffset + pt.X - w * 0.2));
            }
            catch (InvalidOperationException) { }
        }
    }

    // ---- input -----------------------------------------------------------------------------------

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomAt(e.Delta > 0 ? 1.1 : 1 / 1.1, e.GetPosition(_scroller));
            e.Handled = true;
            return;
        }
        UserScrolled?.Invoke();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset - e.Delta);
        else
            _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - e.Delta * 1.2);
        e.Handled = true;
    }

    private void OnSlotMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not PageSlot slot || !_wordsByPage.TryGetValue(slot.PageNumber, out var words)) return;
        var pos = e.GetPosition(slot);
        float px = (float)(pos.X / _zoom), py = (float)(pos.Y / _zoom);

        (int Para, int Word)? best = null;
        float bestDistance = 30; // points
        foreach (var (para, word, box) in words)
        {
            if (IsSpokenParagraph is { } spoken && !spoken(para)) continue;
            float d = box.DistanceTo(px, py);
            if (d < bestDistance) { bestDistance = d; best = (para, word); }
        }
        if (best is { } hit)
        {
            WordClicked?.Invoke(hit.Para, hit.Word, e.ClickCount);
            e.Handled = true;
        }
    }
}
