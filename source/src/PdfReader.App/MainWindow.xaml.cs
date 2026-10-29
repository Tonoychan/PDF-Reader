using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PdfReader.App.Narration;
using PdfReader.App.Pdf;
using PdfReader.App.Reader;
using PdfReader.App.Speech;
using PdfReader.Core;
using UglyToad.PdfPig.Exceptions;

namespace PdfReader.App;

public partial class MainWindow : Window
{
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";
    private const double PointsToDip = 96.0 / 72.0;

    private readonly string _settingsPath = AppSettings.DefaultPath;
    private readonly AppSettings _settings;
    private readonly NarrationController _narrator;
    private readonly DispatcherTimer _speedDebounce;

    private BookDocument? _book;
    private PdfPageRenderer? _renderer;
    private ParagraphItem[] _paragraphs = Array.Empty<ParagraphItem>();
    private ParagraphItem? _readingParagraph;
    private ParagraphItem? _cursorParagraph;
    private (int Paragraph, int Word)? _cursor;
    private CancellationTokenSource? _loadCts;
    private bool _followSuspended;
    private int _lastSavedParagraph = -1;
    private string? _message;
    private bool _ready;

    public MainWindow(string? fileToOpen)
    {
        InitializeComponent();

        _settings = AppSettings.Load(_settingsPath);
        _narrator = new NarrationController(Dispatcher) { PauseAroundHeadings = _settings.Reading.PauseAroundHeadings };
        _narrator.StateChanged += OnNarrationStateChanged;
        _narrator.WordChanged += OnWordChanged;
        _narrator.ReachedEnd += () => ShowMessage("Finished — that's the end of the book.");
        _narrator.Error += message =>
        {
            ShowMessage(message);
            MessageBox.Show(this, message, "PDF Reader", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        // Voices
        var voices = _narrator.Voices;
        VoiceBox.ItemsSource = voices;
        VoiceBox.DisplayMemberPath = nameof(VoiceOption.Display);
        var voice = voices.FirstOrDefault(v => v.Key == _settings.VoiceKey)
                    ?? voices.FirstOrDefault(v => v.Key == _narrator.DefaultVoiceKey)
                    ?? voices.FirstOrDefault();
        VoiceBox.SelectedItem = voice;
        _narrator.Voice = voice;
        VoiceBox.DropDownClosed += (_, _) => Keyboard.ClearFocus();

        // Speed (debounced so dragging the slider doesn't restart speech on every step)
        _speedDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _speedDebounce.Tick += (_, _) =>
        {
            _speedDebounce.Stop();
            _narrator.Rate = SpeedSlider.Value;
            _settings.Rate = SpeedSlider.Value;
        };
        SpeedSlider.Value = _settings.Rate;
        _narrator.Rate = _settings.Rate;
        SpeedLabel.Text = $"{_settings.Rate:0.0}×";

        FollowCheck.IsChecked = _settings.FollowReading;
        ReaderList.FontSize = _settings.FontSize;

        // Reading options
        SkipHeadersCheck.IsChecked = _settings.Reading.SkipHeadersFooters;
        SkipFootnotesCheck.IsChecked = _settings.Reading.SkipFootnotes;
        SkipTocCheck.IsChecked = _settings.Reading.SkipTableOfContents;
        SkipCaptionsCheck.IsChecked = _settings.Reading.SkipCaptions;
        PauseHeadingsCheck.IsChecked = _settings.Reading.PauseAroundHeadings;

        // Text view interaction
        ReaderList.AddHandler(WordTextBlock.WordClickedEvent, new WordClickedEventHandler(OnTextWordClicked));
        ReaderList.PreviewMouseWheel += OnTextMouseWheel;
        ReaderList.PreviewMouseLeftButtonDown += OnReaderMouseDown;

        // Page view interaction
        PagesViewControl.WordClicked += (para, word, clicks) => HandleWordClick(para, word, clicks);
        PagesViewControl.UserScrolled += SuspendFollowing;
        PagesViewControl.ZoomChanged += zoom =>
        {
            _settings.PageZoom = zoom;
            UpdateZoomLabel();
        };
        PagesViewControl.PreviewMouseLeftButtonDown += OnReaderMouseDown;
        PagesViewControl.IsSpokenParagraph = i => _book is not null && i >= 0 && i < _book.Paragraphs.Count && _settings.Reading.IsSpoken(_book.Paragraphs[i]);

        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += OnDragOver;
        Drop += OnDrop;
        Closing += (_, _) => Shutdown();

        _ready = true;
        UpdateViewButtons();
        UpdateUi();

        if (voices.Count == 0)
            ShowMessage("No Windows voices were found. Add one in Settings › Time & language › Speech.");

        if (fileToOpen is not null)
            Loaded += async (_, _) => await OpenAsync(fileToOpen);
    }

    private bool PageMode => _settings.ViewMode == "page" && _renderer is not null;

    // ===== Opening a book ========================================================================

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a PDF",
            Filter = "PDF files (*.pdf)|*.pdf",
            CheckFileExists = true,
        };
        if (_settings.LastFolder is { } folder && Directory.Exists(folder)) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) == true) await OpenAsync(dialog.FileName);
    }

    private void OnCancelLoadClick(object sender, RoutedEventArgs e) => _loadCts?.Cancel();

    private async Task OpenAsync(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"File not found:\n{path}", "PDF Reader", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_narrator.State != NarrationState.Idle) _narrator.Stop();
        SaveProgress();

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        LoadingText.Text = $"Opening {Path.GetFileName(path)}…";
        LoadingProgress.Value = 0;
        LoadingPanel.Visibility = Visibility.Visible;

        var progress = new Progress<(int Page, int Total)>(p =>
        {
            if (cts.IsCancellationRequested) return;
            LoadingProgress.Value = p.Total == 0 ? 0 : (double)p.Page / p.Total;
            LoadingText.Text = $"Reading page {p.Page} of {p.Total}…";
        });

        BookDocument book;
        PdfPageRenderer? renderer = null;
        try
        {
            var rendererTask = OpenRendererAsync(path);
            book = await Task.Run(() => PdfTextExtractor.Extract(path, progress, cts.Token), cts.Token);
            renderer = await rendererTask;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (PdfDocumentEncryptedException)
        {
            MessageBox.Show(this, "This PDF is password-protected, so its text can't be read.", "PDF Reader",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't open this PDF.\n\n" + ex.Message, "PDF Reader",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) LoadingPanel.Visibility = Visibility.Collapsed;
        }

        if (cts.IsCancellationRequested) { renderer?.Dispose(); return; }

        if (book.IsEmpty || book.Paragraphs.All(p => !_settings.Reading.IsSpoken(p) && p.Kind == ParagraphKind.HeaderFooter))
        {
            renderer?.Dispose();
            MessageBox.Show(this,
                "This PDF has no selectable text — it's probably scanned page images.\n\n" +
                "Reading scanned pages needs text recognition (OCR), which isn't in this version yet.",
                "PDF Reader", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShowBook(book, renderer);
    }

    private static async Task<PdfPageRenderer?> OpenRendererAsync(string path)
    {
        try { return await PdfPageRenderer.OpenAsync(path); }
        catch { return null; } // page view unavailable; the text view still works
    }

    private void ShowBook(BookDocument book, PdfPageRenderer? renderer)
    {
        PagesViewControl.Clear();
        _renderer?.Dispose();
        _renderer = renderer;

        _book = book;
        _paragraphs = book.Paragraphs.Select(p => new ParagraphItem(p)).ToArray();
        _readingParagraph = null;
        _cursorParagraph = null;
        _cursor = null;
        _followSuspended = false;
        _lastSavedParagraph = -1;
        ApplySkipFlags();

        // Text view: paragraphs and pictures in reading order, with page dividers.
        var items = new List<object>(_paragraphs.Length + book.PageCount * 2);
        int lastPage = 0;
        foreach (var entry in book.ReflowEntries())
        {
            int page = entry switch
            {
                ReflowParagraph rp => rp.Paragraph.PageNumber,
                ReflowImage ri => ri.PageNumber,
                _ => lastPage,
            };
            if (page != lastPage)
            {
                items.Add(new PageHeaderItem(page, book.PageCount));
                lastPage = page;
            }
            if (entry is ReflowParagraph p) items.Add(_paragraphs[p.Paragraph.Index]);
            else if (entry is ReflowImage img && renderer is not null) items.Add(new ImageItem(img.PageNumber, img.Box) { Loader = RenderPictureAsync });
        }
        ReaderList.ItemsSource = items;

        // Page view: the real pages.
        if (renderer is not null) PagesViewControl.Load(book, renderer, _settings.PageZoom);

        _settings.LastFolder = Path.GetDirectoryName(book.FilePath);
        BookTitle.Text = book.Title;
        BookInfo.Text = $"{book.PageCount} pages · {book.Paragraphs.Count(p => p.Kind != ParagraphKind.HeaderFooter)} paragraphs";
        Title = $"{book.Title} — PDF Reader";
        EmptyState.Visibility = Visibility.Collapsed;

        var saved = _settings.Recall(book.FilePath, book);
        _narrator.Load(book, saved ?? ReadingPosition.Start, p => _settings.Reading.IsSpoken(p));
        _message = saved is { } s
            ? $"Picked up where you left off (page {book.Paragraphs[s.ParagraphIndex].PageNumber}). Press Play."
            : "Press Play to start reading, or click a word to start from there.";
        if (renderer is null && _settings.ViewMode == "page")
            _message = "Page view isn't available for this PDF, so it's shown as text.";

        ApplyViewMode();
        UpdateUi();
        Dispatcher.InvokeAsync(() => { FollowReading(force: true); UpdateZoomLabel(); }, DispatcherPriority.ContextIdle);
    }

    private async Task<ImageSource?> RenderPictureAsync(ImageItem item)
    {
        if (_renderer is not { } renderer || _book?.Page(item.PageNumber) is not { } page) return null;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int px = (int)Math.Round(Math.Max(item.DisplayWidth, 120) * dpi);
        return await renderer.RenderAsync(item.PageNumber, px, CancellationToken.None, item.Box, page.Width, page.Height);
    }

    // ===== View mode =============================================================================

    private void OnPageModeClick(object sender, RoutedEventArgs e) => SetViewMode("page");
    private void OnTextModeClick(object sender, RoutedEventArgs e) => SetViewMode("text");

    private void SetViewMode(string mode)
    {
        if (mode == "page" && _book is not null && _renderer is null)
        {
            ShowMessage("Page view isn't available for this PDF.");
            mode = "text";
        }
        _settings.ViewMode = mode;
        ApplyViewMode();
        Dispatcher.InvokeAsync(() => FollowReading(force: true), DispatcherPriority.ContextIdle);
    }

    private void ApplyViewMode()
    {
        bool hasBook = _book is not null;
        PagesViewControl.Visibility = hasBook && PageMode ? Visibility.Visible : Visibility.Collapsed;
        ReaderList.Visibility = hasBook && !PageMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateViewButtons();
        UpdateZoomLabel();
    }

    private void UpdateViewButtons()
    {
        bool page = _settings.ViewMode == "page" && (_book is null || _renderer is not null);
        PageModeButton.IsChecked = page;
        TextModeButton.IsChecked = !page;
    }

    private void UpdateZoomLabel()
    {
        ZoomLabel.Text = _book is not null && PageMode ? $"{PagesViewControl.Zoom / PointsToDip * 100:0}%" : "";
    }

    // ===== Reading options =======================================================================

    private void OnReadingOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var o = _settings.Reading;
        o.SkipHeadersFooters = SkipHeadersCheck.IsChecked == true;
        o.SkipFootnotes = SkipFootnotesCheck.IsChecked == true;
        o.SkipTableOfContents = SkipTocCheck.IsChecked == true;
        o.SkipCaptions = SkipCaptionsCheck.IsChecked == true;
        o.PauseAroundHeadings = PauseHeadingsCheck.IsChecked == true;

        _narrator.PauseAroundHeadings = o.PauseAroundHeadings;
        ApplySkipFlags();
        _narrator.SetSpokenFilter(p => _settings.Reading.IsSpoken(p));
        _settings.Save(_settingsPath);
    }

    private void ApplySkipFlags()
    {
        foreach (var p in _paragraphs) p.IsSkipped = !_settings.Reading.IsSpoken(p.Model);
    }

    // ===== Narration events ======================================================================

    private void OnWordChanged(int paragraphIndex, int wordIndex)
    {
        if (paragraphIndex < 0 || paragraphIndex >= _paragraphs.Length) return;
        var item = _paragraphs[paragraphIndex];

        if (!ReferenceEquals(_readingParagraph, item))
        {
            if (_readingParagraph is not null)
            {
                _readingParagraph.ReadingWord = -1;
                _readingParagraph.IsActive = false;
            }
            _readingParagraph = item;
            item.IsActive = true;
        }
        item.ReadingWord = wordIndex;
        PagesViewControl.SetReading(paragraphIndex, wordIndex);

        if (paragraphIndex != _lastSavedParagraph && _narrator.State == NarrationState.Playing)
        {
            _lastSavedParagraph = paragraphIndex;
            SaveProgress();
        }

        if (_narrator.State == NarrationState.Playing) _message = null;
        UpdateStatus();
        FollowReading(force: false);
    }

    private void OnNarrationStateChanged()
    {
        if (_narrator.State != NarrationState.Playing) SaveProgress();
        UpdateUi();
    }

    // ===== Transport =============================================================================

    private void OnPlayClick(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_book is null || _narrator.Voice is null) return;
        switch (_narrator.State)
        {
            case NarrationState.Playing:
                _narrator.Pause();
                break;
            case NarrationState.Paused:
                if (_cursor is not null) PlayFromCursor();
                else _narrator.Resume();
                break;
            default:
                ResumeFollowing();
                _narrator.PlayFrom(_narrator.Position);
                break;
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => StopReading();

    private void StopReading()
    {
        if (_book is null) return;
        ClearCursor();
        _narrator.Stop();
        ShowMessage("Stopped. Press Play to start this paragraph again.");
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => Skip(-1);
    private void OnNextClick(object sender, RoutedEventArgs e) => Skip(+1);

    private void Skip(int direction)
    {
        if (_book is null) return;
        ClearCursor();
        ResumeFollowing();
        if (direction < 0) _narrator.PreviousParagraph();
        else _narrator.NextParagraph();
    }

    private void OnFromCursorClick(object sender, RoutedEventArgs e) => PlayFromCursor();

    private void PlayFromCursor()
    {
        if (_cursor is not { } c || _book is null) return;
        ClearCursor();
        ResumeFollowing();
        _narrator.PlayFrom(new ReadingPosition(c.Paragraph, _book.Paragraphs[c.Paragraph].Words[c.Word].Start));
    }

    // ===== Clicking words (both views) ===========================================================

    private void OnTextWordClicked(object sender, WordClickedEventArgs e) =>
        HandleWordClick(e.Item.Model.Index, e.WordIndex, e.ClickCount);

    private void HandleWordClick(int paragraphIndex, int wordIndex, int clickCount)
    {
        if (_book is null || paragraphIndex < 0 || paragraphIndex >= _book.Paragraphs.Count) return;
        var para = _book.Paragraphs[paragraphIndex];
        if (wordIndex < 0 || wordIndex >= para.Words.Count) return;
        var pos = new ReadingPosition(para.Index, para.Words[wordIndex].Start);
        string word = para.WordText(wordIndex);

        if (clickCount >= 2)
        {
            // Double-click: read from here right away.
            ClearCursor();
            ResumeFollowing();
            _narrator.PlayFrom(pos);
            return;
        }

        if (_narrator.State == NarrationState.Idle)
        {
            // Not reading: just move the start point here.
            _narrator.MoveTo(pos);
            ShowMessage($"Will start from “{word}” (page {para.PageNumber}). Press Play.");
        }
        else
        {
            // Reading or paused: drop a cursor; Play/Resume or "Read from cursor" jumps to it.
            SetCursor(paragraphIndex, wordIndex);
            ShowMessage(_narrator.State == NarrationState.Paused
                ? $"Cursor on “{word}”. Press Play to read from here."
                : $"Cursor on “{word}”. Click “Read from cursor” to jump there.");
        }
    }

    private void SetCursor(int paragraphIndex, int word)
    {
        if (_cursorParagraph is not null) _cursorParagraph.CursorWord = -1;
        var item = _paragraphs[paragraphIndex];
        _cursorParagraph = item;
        item.CursorWord = word;
        _cursor = (paragraphIndex, word);
        PagesViewControl.SetCursor(paragraphIndex, word);
        UpdateUi();
    }

    private void ClearCursor()
    {
        if (_cursorParagraph is not null) _cursorParagraph.CursorWord = -1;
        _cursorParagraph = null;
        _cursor = null;
        PagesViewControl.SetCursor(-1, -1);
        UpdateUi();
    }

    // ===== Following the reading position ========================================================

    private bool FollowEnabled => FollowCheck.IsChecked == true && !_followSuspended;

    private void OnFollowChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.FollowReading = FollowCheck.IsChecked == true;
        if (FollowCheck.IsChecked == true) ResumeFollowing();
        UpdateUi();
    }

    private void OnBackToReadingClick(object sender, RoutedEventArgs e) => ResumeFollowing();

    private void ResumeFollowing()
    {
        _followSuspended = false;
        UpdateUi();
        FollowReading(force: true);
    }

    private void SuspendFollowing()
    {
        if (_book is null || _followSuspended) return;
        _followSuspended = true;
        UpdateUi();
    }

    private void FollowReading(bool force)
    {
        if (_readingParagraph is null) return;
        if (!force && !FollowEnabled) return;
        if (force && FollowCheck.IsChecked != true && _narrator.State == NarrationState.Playing) return;

        if (PageMode) PagesViewControl.FollowReading(force);
        else FollowInText(force, retry: true);
    }

    /// <summary>Text view: keeps the word being read in the upper third of the view.</summary>
    private void FollowInText(bool force, bool retry)
    {
        if (_readingParagraph is null) return;
        var item = _readingParagraph;
        var scroller = FindDescendant<ScrollViewer>(ReaderList);
        if (scroller is null) return;

        if (ReaderList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
        {
            if (!retry) return;
            ReaderList.ScrollIntoView(item);
            Dispatcher.InvokeAsync(() => FollowInText(true, retry: false), DispatcherPriority.Loaded);
            return;
        }

        var text = FindDescendant<WordTextBlock>(container);
        if (text is null) return;

        try
        {
            var rect = text.GetWordRect(item.ReadingWord);
            if (rect.IsEmpty) rect = new Rect(0, 0, 1, text.ActualHeight > 0 ? Math.Min(text.ActualHeight, 30) : 30);
            var top = text.TransformToAncestor(scroller).Transform(rect.TopLeft);
            double view = scroller.ViewportHeight;
            if (view <= 0) return;

            bool outside = top.Y < view * 0.10 || top.Y + rect.Height > view * 0.80;
            if (force || outside)
                scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + top.Y - view * 0.30));
        }
        catch (InvalidOperationException)
        {
            // The item was recycled mid-layout; the next word will try again.
        }
    }

    private void OnTextMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ChangeFontSize(e.Delta > 0 ? +1 : -1);
            e.Handled = true;
            return;
        }
        SuspendFollowing();

        // WPF's default wheel step in pixel-scrolling lists is tiny; scroll about three lines per notch.
        if (FindDescendant<ScrollViewer>(ReaderList) is { } scroller)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta * 1.2);
            e.Handled = true;
        }
    }

    private void OnReaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ScrollBar>(d) is not null) SuspendFollowing();
    }

    private void ChangeFontSize(int delta)
    {
        double size = Math.Clamp(ReaderList.FontSize + delta, 12, 36);
        ReaderList.FontSize = size;
        _settings.FontSize = size;
        Dispatcher.InvokeAsync(() => FollowReading(force: !_followSuspended), DispatcherPriority.Loaded);
    }

    private void Zoom(int direction)
    {
        if (PageMode) PagesViewControl.ZoomBy(direction > 0 ? 1.1 : 1 / 1.1);
        else ChangeFontSize(direction);
    }

    // ===== Voice & speed =========================================================================

    private void OnVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || VoiceBox.SelectedItem is not VoiceOption voice) return;
        _narrator.Voice = voice;
        _settings.VoiceKey = voice.Key;
        _settings.Save(_settingsPath);
        UpdateUi();
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedLabel is not null) SpeedLabel.Text = $"{e.NewValue:0.0}×";
        if (!_ready) return;
        _speedDebounce.Stop();
        _speedDebounce.Start();
    }

    // ===== Keyboard & drag-drop ==================================================================

    private ScrollViewer? ActiveScroller() => PageMode ? PagesViewControl.Scroller : FindDescendant<ScrollViewer>(ReaderList);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl && e.Key == Key.O) { OnOpenClick(this, e); e.Handled = true; return; }
        if (Keyboard.FocusedElement is ComboBox or ComboBoxItem or TextBox) return;
        if (OptionsPopup.IsOpen) return;

        var scroller = ActiveScroller();
        switch (e.Key)
        {
            case Key.Space:
            case Key.MediaPlayPause:
                TogglePlay(); break;
            case Key.Escape:
            case Key.MediaStop:
                StopReading(); break;
            case Key.Left when ctrl:
            case Key.MediaPreviousTrack:
                Skip(-1); break;
            case Key.Right when ctrl:
            case Key.MediaNextTrack:
                Skip(+1); break;
            case Key.OemPlus when ctrl:
            case Key.Add when ctrl:
                Zoom(+1); break;
            case Key.OemMinus when ctrl:
            case Key.Subtract when ctrl:
                Zoom(-1); break;
            case Key.D0 when ctrl:
            case Key.NumPad0 when ctrl:
                if (PageMode) { PagesViewControl.FitWidth(); _settings.PageZoom = null; UpdateZoomLabel(); }
                break;
            case Key.PageDown: SuspendFollowing(); scroller?.ScrollToVerticalOffset(scroller.VerticalOffset + scroller.ViewportHeight * 0.9); break;
            case Key.PageUp: SuspendFollowing(); scroller?.ScrollToVerticalOffset(scroller.VerticalOffset - scroller.ViewportHeight * 0.9); break;
            case Key.Down: SuspendFollowing(); scroller?.ScrollToVerticalOffset(scroller.VerticalOffset + 60); break;
            case Key.Up: SuspendFollowing(); scroller?.ScrollToVerticalOffset(scroller.VerticalOffset - 60); break;
            case Key.Home when ctrl: SuspendFollowing(); scroller?.ScrollToTop(); break;
            case Key.End when ctrl: SuspendFollowing(); scroller?.ScrollToBottom(); break;
            default: return;
        }
        e.Handled = true;
    }

    private static string? DroppedPdf(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPdf(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DroppedPdf(e) is { } path) await OpenAsync(path);
    }

    // ===== UI state ==============================================================================

    private void ShowMessage(string message)
    {
        _message = message;
        UpdateStatus();
    }

    private void UpdateUi()
    {
        if (!_ready) return;
        bool hasBook = _book is not null;
        bool canSpeak = hasBook && _narrator.Voice is not null;
        var state = _narrator.State;

        PlayButton.IsEnabled = canSpeak;
        PlayButton.Content = state == NarrationState.Playing ? PauseGlyph : PlayGlyph;
        PlayButton.ToolTip = state switch
        {
            NarrationState.Playing => "Pause (Space)",
            NarrationState.Paused => _cursor is null ? "Resume (Space)" : "Read from cursor (Space)",
            _ => "Play (Space)",
        };
        StopButton.IsEnabled = hasBook && state != NarrationState.Idle;
        PrevButton.IsEnabled = canSpeak;
        NextButton.IsEnabled = canSpeak;
        FromCursorButton.IsEnabled = canSpeak && _cursor is not null;
        BackToReadingButton.Visibility = hasBook && _followSuspended && FollowCheck.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (!_ready) return;
        if (_book is null || _book.IsEmpty)
        {
            StatusText.Text = _message ?? "";
            return;
        }

        var pos = _narrator.Position;
        var para = _book.Paragraphs[Math.Clamp(pos.ParagraphIndex, 0, _book.Paragraphs.Count - 1)];
        string where = $"Page {para.PageNumber} of {_book.PageCount}";
        string state = _narrator.State switch
        {
            NarrationState.Playing => "Reading",
            NarrationState.Paused => "Paused",
            _ => "Ready",
        };
        StatusText.Text = _message is null ? $"{state} · {where}" : $"{_message}   ·   {where}";
    }

    private void SaveProgress()
    {
        if (_book is null) return;
        _settings.Remember(_book.FilePath, _narrator.Position);
        _settings.Save(_settingsPath);
    }

    private void Shutdown()
    {
        _loadCts?.Cancel();
        SaveProgress();
        _settings.Save(_settingsPath);
        _narrator.Dispose();
        _renderer?.Dispose();
    }

    // ===== Visual tree helpers ===================================================================

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T match) return match;
                queue.Enqueue(child);
            }
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        DependencyObject? current = node;
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
