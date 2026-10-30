using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using PdfReader.App.Library;
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

    // Opening: the pages can be shown before the text is ready.
    private bool _pagesOnly;
    private double _textProgress;

    // Sidebar
    private OutlineItem[] _outlineItems = Array.Empty<OutlineItem>();
    private bool _syncingContents;
    private readonly DispatcherTimer _searchDebounce;
    private CancellationTokenSource? _searchCts;
    private int _searchIndex = -1;
    private bool _editingPage;

    // Library & bookmarks
    private readonly string _libraryPath = LibraryStore.DefaultPath;
    private readonly LibraryStore _library;
    private BookNotes? _notes;
    private readonly DispatcherTimer _notesSave;
    private bool _scanningFolder;

    // Background listening: Windows media controls and taskbar buttons
    private MediaSession? _media;
    private ThumbButtonInfo? _thumbPrev, _thumbPlay, _thumbNext;
    private ImageSource? _thumbPlayImage, _thumbPauseImage;

    public MainWindow(string? fileToOpen)
    {
        InitializeComponent();

        _settings = AppSettings.Load(_settingsPath);
        _library = LibraryStore.Load(_libraryPath);
        Theme.Apply(_settings.Theme, _settings.MatchPageColors);
        if (_settings.Maximized) WindowState = WindowState.Maximized;
        _notesSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _notesSave.Tick += (_, _) => { _notesSave.Stop(); _notes?.Save(BookNotes.DefaultFolder); };
        _narrator = new NarrationController(Dispatcher) { PauseAroundHeadings = _settings.Reading.PauseAroundHeadings };
        _narrator.StateChanged += OnNarrationStateChanged;
        _narrator.HighlightChanged += OnWordChanged;
        _narrator.Pronouncer.Enabled = _settings.SmartPronunciation;
        _narrator.Pronouncer.UserWords = _settings.WordList;
        _narrator.ReachedEnd += () => ShowMessage("Finished — that's the end of the book.");
        _narrator.Error += message =>
        {
            ShowMessage(message);
            MessageBox.Show(this, message, "PDF Reader", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        // Voices
        var voices = _narrator.Voices;
        VoiceBox.ItemsSource = GroupedVoices(voices);
        var voice = voices.FirstOrDefault(v => v.Key == _settings.VoiceKey)
                    ?? voices.FirstOrDefault(v => v.Key == _narrator.DefaultVoiceKey)
                    ?? voices.FirstOrDefault();
        VoiceBox.SelectedItem = voice;
        _narrator.Voice = voice;

        // Speed (debounced so dragging the slider doesn't restart speech on every step)
        _speedDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _speedDebounce.Tick += (_, _) =>
        {
            _speedDebounce.Stop();
            _narrator.Rate = SpeedSlider.Value;
            _settings.Rate = SpeedSlider.Value;
        };
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); RunSearch(); };

        SpeedSlider.Value = _settings.Rate;
        _narrator.Rate = _settings.Rate;
        SpeedLabel.Text = SpeedText(_settings.Rate);

        FollowCheck.IsChecked = _settings.FollowReading;
        ReaderList.FontSize = _settings.FontSize;
        FontSizeSlider.Value = _settings.FontSize;

        // Reading options
        SkipHeadersCheck.IsChecked = _settings.Reading.SkipHeadersFooters;
        SkipFootnotesCheck.IsChecked = _settings.Reading.SkipFootnotes;
        SkipTocCheck.IsChecked = _settings.Reading.SkipTableOfContents;
        SkipCaptionsCheck.IsChecked = _settings.Reading.SkipCaptions;
        PauseHeadingsCheck.IsChecked = _settings.Reading.PauseAroundHeadings;
        SmartPronunciationCheck.IsChecked = _settings.SmartPronunciation;

        // Theme
        (Theme.Normalize(_settings.Theme) switch { Theme.Sepia => SepiaThemeRadio, Theme.Dark => DarkThemeRadio, _ => LightThemeRadio }).IsChecked = true;
        MatchPagesCheck.IsChecked = _settings.MatchPageColors;
        PagesViewControl.SetDarkPages(Theme.TintFor(_settings.Theme, _settings.MatchPageColors) == PageTint.Dark);
        SourceInitialized += OnSourceInitialized;

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
        PagesViewControl.VisiblePageChanged += _ => UpdatePageBox();
        PagesViewControl.WordRightClicked += ShowWordMenu;
        ReaderList.AddHandler(WordTextBlock.WordRightClickedEvent, new WordClickedEventHandler((_, e) => ShowWordMenu(e.Item.Model.Index, e.WordIndex)));
        ReaderList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => UpdatePageBox()));
        PagesViewControl.IsSpokenParagraph = i => _book is not null && i >= 0 && i < _book.Paragraphs.Count && _settings.Reading.IsSpoken(_book.Paragraphs[i]);

        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += OnDragOver;
        Drop += OnDrop;
        Closing += (_, _) => Shutdown();

        _ready = true;
        UpdateViewButtons();
        UpdateUi();
        if (_settings.SidebarOpen) ShowSidebar(_settings.SidebarTab);

        if (voices.Count == 0)
            ShowMessage("No Windows voices were found. Add one in Settings › Time & language › Speech.");

        EmptyState.Visibility = Visibility.Collapsed;
        if (fileToOpen is not null)
            Loaded += async (_, _) => await OpenAsync(fileToOpen);
        else
            ShowLibrary();
        if (_settings.LibraryFolder is not null) Loaded += async (_, _) => await RescanFolderAsync();
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
        string cacheFolder = BookCache.DefaultFolder;

        // 1. The pages (Windows' PDF engine) and any cached text, side by side.
        var rendererTask = OpenRendererAsync(path);
        var cachedTask = Task.Run(() => BookCache.TryLoad(cacheFolder, path));
        var renderer = await rendererTask;
        var cached = await cachedTask;
        if (cts.IsCancellationRequested) { renderer?.Dispose(); return; }

        if (cached is not null && !cached.IsEmpty)
        {
            ShowBook(cached, renderer, pagesShown: false);
            return;
        }

        // 2. Not seen before: show the pages now and prepare the text in the background.
        bool pagesShown = renderer is not null;
        if (renderer is not null) ShowPagesOnly(path, renderer);
        else
        {
            HideLibrary(force: true);
            LoadingText.Text = $"Opening {Path.GetFileName(path)}…";
            LoadingProgress.Value = 0;
            LoadingPanel.Visibility = Visibility.Visible;
        }

        var progress = new Progress<(int Page, int Total)>(p =>
        {
            if (cts.IsCancellationRequested) return;
            double f = p.Total == 0 ? 0 : (double)p.Page / p.Total;
            LoadingProgress.Value = f;
            LoadingText.Text = $"Reading page {p.Page} of {p.Total}…";
            _textProgress = f;
            if (_pagesOnly) UpdateStatus();
        });

        BookDocument book;
        try
        {
            book = await Task.Run(() => PdfTextExtractor.Extract(path, progress, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (PdfDocumentEncryptedException)
        {
            if (ReferenceEquals(_loadCts, cts)) ResetToEmpty();
            MessageBox.Show(this, "This PDF is password-protected, so its text can't be read.", "PDF Reader",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_loadCts, cts)) ResetToEmpty();
            MessageBox.Show(this, "Couldn't open this PDF.\n\n" + ex.Message, "PDF Reader",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) LoadingPanel.Visibility = Visibility.Collapsed;
        }

        if (cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts)) return;

        if (book.IsEmpty || book.Paragraphs.All(p => p.Kind == ParagraphKind.HeaderFooter))
        {
            if (!pagesShown) renderer?.Dispose();
            _pagesOnly = false;
            ShowMessage("This PDF has no selectable text (probably scanned pages), so it can't be read aloud yet.");
            if (!pagesShown) ResetToEmpty();
            MessageBox.Show(this,
                "This PDF has no selectable text — it's probably scanned page images.\n\n" +
                "Reading scanned pages needs text recognition (OCR), which isn't in this version yet.",
                "PDF Reader", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateUi();
            return;
        }

        ShowBook(book, renderer, pagesShown);
        _ = Task.Run(() => BookCache.TrySave(cacheFolder, book));
    }

    /// <summary>Shows the real pages straight away while the text for reading aloud is still being prepared.</summary>
    private void ShowPagesOnly(string path, PdfPageRenderer renderer)
    {
        ResetToEmpty();
        _renderer = renderer;
        _pagesOnly = true;
        _textProgress = 0;
        _followSuspended = false;

        var sizes = new List<(float, float)>(renderer.PageCount);
        for (int n = 1; n <= renderer.PageCount; n++) sizes.Add(renderer.PageSizePoints(n));
        PagesViewControl.LoadPages(renderer, sizes, _settings.PageZoom);

        string title = Path.GetFileNameWithoutExtension(path);
        HideLibrary(force: true);
        BookTitle.Text = title;
        BookInfo.Text = $"{renderer.PageCount} pages · preparing text…";
        Title = $"{title} — PDF Reader";
        EmptyState.Visibility = Visibility.Collapsed;
        _message = null;
        ApplyViewMode();
        UpdateUi();
    }

    /// <summary>Back to the "no book open" state.</summary>
    private void ResetToEmpty()
    {
        PagesViewControl.Clear();
        _renderer?.Dispose();
        _renderer = null;
        _book = null;
        _pagesOnly = false;
        _paragraphs = Array.Empty<ParagraphItem>();
        _readingParagraph = _cursorParagraph = null;
        _cursor = null;
        ReaderList.ItemsSource = null;
        _outlineItems = Array.Empty<OutlineItem>();
        ContentsList.ItemsSource = null;
        ClearSearch();
        BookTitle.Text = "No book open";
        BookInfo.Text = "Open a PDF to start";
        Title = "PDF Reader";
        _notes = null;
        _media?.Clear();
        RefreshBookmarks();
        ApplyViewMode();
        UpdateUi();
        ShowLibrary();
    }

    private async Task<PdfPageRenderer?> OpenRendererAsync(string path)
    {
        try
        {
            var renderer = await PdfPageRenderer.OpenAsync(path);
            renderer.Tint = Theme.TintFor(_settings.Theme, _settings.MatchPageColors);
            return renderer;
        }
        catch { return null; } // page view unavailable; the text view still works
    }

    private void ShowBook(BookDocument book, PdfPageRenderer? renderer, bool pagesShown)
    {
        if (!pagesShown)
        {
            PagesViewControl.Clear();
            if (!ReferenceEquals(_renderer, renderer)) _renderer?.Dispose();
            _renderer = renderer;
        }
        _pagesOnly = false;

        _book = book;
        _paragraphs = book.Paragraphs.Select(p => new ParagraphItem(p)).ToArray();
        _readingParagraph = null;
        _cursorParagraph = null;
        _cursor = null;
        if (!pagesShown) _followSuspended = false;
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

        // Page view: the real pages (already on screen if the text was prepared while you looked at them).
        if (renderer is not null)
        {
            if (pagesShown) PagesViewControl.AttachBook(book);
            else PagesViewControl.Load(book, renderer, _settings.PageZoom);
        }

        // Sidebar
        _outlineItems = book.Outline.Select(o => new OutlineItem(o)).ToArray();
        ContentsList.ItemsSource = _outlineItems;
        ContentsEmpty.Visibility = _outlineItems.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearch();
        if (SearchBox.Text.Trim().Length >= 2) RunSearch();

        _settings.LastFolder = Path.GetDirectoryName(book.FilePath);
        BookTitle.Text = book.Title;
        BookInfo.Text = $"{book.PageCount} pages · {book.Paragraphs.Count(p => p.Kind != ParagraphKind.HeaderFooter)} paragraphs";
        Title = $"{book.Title} — PDF Reader";
        EmptyState.Visibility = Visibility.Collapsed;

        // Library and bookmarks
        _library.Touch(book.FilePath, book.Title, book.PageCount);
        _notes = BookNotes.Load(BookNotes.DefaultFolder, book.FilePath);
        RefreshBookmarks();
        HideLibrary(force: true);
        _ = EnsureCoverAsync(book.FilePath);

        var saved = _settings.Recall(book.FilePath, book);
        _narrator.Load(book, saved ?? ReadingPosition.Start, p => _settings.Reading.IsSpoken(p));
        UpdateLibraryProgress();
        _message = saved is { } s
            ? $"Picked up where you left off (page {book.Paragraphs[s.ParagraphIndex].PageNumber}). Press Play."
            : "Press Play to start reading, or click a word to start from there.";
        if (renderer is null && _settings.ViewMode == "page")
            _message = "Page view isn't available for this PDF, so it's shown as text.";

        ApplyViewMode();
        UpdateUi();
        bool userBrowsing = pagesShown && _followSuspended;
        Dispatcher.InvokeAsync(() =>
        {
            if (!userBrowsing) FollowReading(force: true);
            UpdateZoomLabel();
            UpdatePageBox();
            SyncContents();
        }, DispatcherPriority.ContextIdle);
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
        bool showPages = (hasBook && PageMode) || (_pagesOnly && _renderer is not null);
        PagesViewControl.Visibility = showPages ? Visibility.Visible : Visibility.Collapsed;
        ReaderList.Visibility = hasBook && !showPages ? Visibility.Visible : Visibility.Collapsed;
        TextModeButton.IsEnabled = !_pagesOnly;
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
        bool pages = (_book is not null && PageMode) || _pagesOnly;
        ZoomLabel.Text = pages ? $"{PagesViewControl.Zoom / PointsToDip * 100:0}%" : "—";
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

    private void OnWordChanged(int paragraphIndex, int wordIndex, int lastWord)
    {
        if (paragraphIndex < 0 || paragraphIndex >= _paragraphs.Length) return;
        var item = _paragraphs[paragraphIndex];

        if (!ReferenceEquals(_readingParagraph, item))
        {
            if (_readingParagraph is not null)
            {
                _readingParagraph.ReadingWord = -1;
                _readingParagraph.ReadingWordEnd = -1;
                _readingParagraph.IsActive = false;
            }
            _readingParagraph = item;
            item.IsActive = true;
        }
        item.ReadingWordEnd = Math.Max(wordIndex, lastWord);
        item.ReadingWord = wordIndex;
        PagesViewControl.SetReading(paragraphIndex, wordIndex, lastWord);

        if (paragraphIndex != _lastSavedParagraph && _narrator.State == NarrationState.Playing)
        {
            _lastSavedParagraph = paragraphIndex;
            SaveProgress();
        }

        if (_narrator.State == NarrationState.Playing) _message = null;
        UpdateStatus();
        FollowReading(force: false);
        SyncContents();
        UpdatePageBox();
        UpdateMedia();
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
        if ((_book is null && !_pagesOnly) || _followSuspended) return;
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
        if (Math.Abs(FontSizeSlider.Value - size) > 0.01) FontSizeSlider.Value = size;
        Dispatcher.InvokeAsync(() => FollowReading(force: !_followSuspended), DispatcherPriority.Loaded);
    }

    private void Zoom(int direction)
    {
        if (PageMode || _pagesOnly) PagesViewControl.ZoomBy(direction > 0 ? 1.1 : 1 / 1.1);
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
        if (SpeedLabel is not null) SpeedLabel.Text = SpeedText(e.NewValue);
        UpdateVoiceChip();
        if (!_ready) return;
        _speedDebounce.Stop();
        _speedDebounce.Start();
    }

    // ===== Natural voices & pronunciation ===========================================================

    private void OnVoicesClick(object sender, RoutedEventArgs e)
    {
        var window = new VoicesWindow { Owner = this };
        window.ShowDialog();
        if (!window.Changed && window.ChosenVoiceKey is null) return;

        string? keep = window.ChosenVoiceKey ?? (VoiceBox.SelectedItem as VoiceOption)?.Key;
        _narrator.RefreshVoices();
        RebindVoices(keep);
        if (window.ChosenVoiceKey is not null && VoiceBox.SelectedItem is VoiceOption v && v.Key == window.ChosenVoiceKey)
            ShowMessage($"Now using the natural voice {v.Name}. The sentence being read is highlighted.");
    }

    /// <summary>Re-fills the voice list, selecting <paramref name="key"/> if it's still there.</summary>
    private void RebindVoices(string? key)
    {
        var voices = _narrator.Voices;
        bool wasReady = _ready;
        _ready = false; // don't treat the re-fill as the user picking a voice
        VoiceBox.ItemsSource = GroupedVoices(voices);
        _ready = wasReady;
        var pick = voices.FirstOrDefault(v => v.Key == key)
                   ?? voices.FirstOrDefault(v => v.Key == _settings.VoiceKey)
                   ?? voices.FirstOrDefault(v => v.Key == _narrator.DefaultVoiceKey)
                   ?? voices.FirstOrDefault();
        VoiceBox.SelectedItem = pick;
        if (pick is not null && !Equals(_narrator.Voice, pick))
        {
            _narrator.Voice = pick;
            _settings.VoiceKey = pick.Key;
            _settings.Save(_settingsPath);
        }
        UpdateUi();
    }

    private void OnPronunciationChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.SmartPronunciation = SmartPronunciationCheck.IsChecked == true;
        _narrator.Pronouncer.Enabled = _settings.SmartPronunciation;
        _settings.Save(_settingsPath);
        _narrator.PronunciationChanged();
    }

    private void OnWordListClick(object sender, RoutedEventArgs e)
    {
        var window = new WordListWindow(_settings.WordList) { Owner = this };
        if (window.ShowDialog() != true || window.Result is null) return;
        _settings.WordList = window.Result;
        _narrator.Pronouncer.UserWords = window.Result;
        _settings.Save(_settingsPath);
        _narrator.PronunciationChanged();
        ShowMessage($"Word list saved ({window.Result.Count} word{(window.Result.Count == 1 ? "" : "s")}).");
    }

    // ===== Keyboard & drag-drop ==================================================================

    private ScrollViewer? ActiveScroller() => PageMode || _pagesOnly ? PagesViewControl.Scroller : FindDescendant<ScrollViewer>(ReaderList);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl && e.Key == Key.O) { OnOpenClick(this, e); e.Handled = true; return; }
        if (ctrl && e.Key == Key.L) { if (LibraryPanel.Visibility == Visibility.Visible) HideLibrary(); else ShowLibrary(); e.Handled = true; return; }
        if (LibraryPanel.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape && HideLibrary()) e.Handled = true;
            return; // the rest of the shortcuts are for the reader
        }
        if (ctrl && e.Key == Key.F) { ShowSidebar("search"); e.Handled = true; return; }
        if (ctrl && e.Key == Key.D) { AddBookmarkHere(focusNote: false); e.Handled = true; return; }
        if (ctrl && e.Key == Key.G) { PageBox.Focus(); PageBox.SelectAll(); e.Handled = true; return; }
        if (e.Key == Key.F3) { NextSearchHit(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : +1); e.Handled = true; return; }
        if (Keyboard.FocusedElement is ComboBox or ComboBoxItem or TextBox) return;

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
                if (PageMode || _pagesOnly) { PagesViewControl.FitWidth(); _settings.PageZoom = null; UpdateZoomLabel(); }
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

    // ===== Library =================================================================================

    private void OnLibraryButtonClick(object sender, RoutedEventArgs e)
    {
        if (LibraryButton.IsChecked == true) ShowLibrary();
        else if (!HideLibrary()) LibraryButton.IsChecked = true;
    }

    private void OnLibraryBackClick(object sender, RoutedEventArgs e) => HideLibrary();

    private void ShowLibrary()
    {
        UpdateLibraryProgress();
        RefreshLibrary();
        LibraryPanel.Visibility = Visibility.Visible;
        LibraryButton.IsChecked = true;
        bool hasBook = _book is not null || _pagesOnly;
        LibraryBackButton.Visibility = hasBook ? Visibility.Visible : Visibility.Collapsed;
        LibraryBackText.Text = hasBook ? $"Back to {Truncate(BookTitle.Text, 28)}" : "Back";
        Keyboard.ClearFocus();
    }

    /// <summary>Hides the library (only when a book is open, unless forced). Returns true if it was hidden.</summary>
    private bool HideLibrary(bool force = false)
    {
        if (!force && _book is null && !_pagesOnly) return false;
        LibraryPanel.Visibility = Visibility.Collapsed;
        LibraryButton.IsChecked = false;
        return true;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void OnLibraryFilterChanged(object sender, TextChangedEventArgs e)
    {
        LibraryFilterHint.Visibility = LibraryFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshLibrary();
    }

    private void RefreshLibrary()
    {
        if (!_ready) return;
        string filter = LibraryFilter.Text.Trim();
        bool Match(LibraryEntry e) => filter.Length == 0 ||
            e.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
            Path.GetFileName(e.Path).Contains(filter, StringComparison.CurrentCultureIgnoreCase);
        string? current = _book?.FilePath;

        var recent = _library.Recent.Where(Match)
            .Select(e => new LibraryCard(e, string.Equals(e.Path, current, StringComparison.OrdinalIgnoreCase))).ToList();

        // The most recent book gets the big "Continue reading" card (unless you're searching).
        LibraryCard? continueCard = filter.Length == 0 ? recent.FirstOrDefault(c => !c.IsMissing) : null;
        if (continueCard is not null) recent.Remove(continueCard);
        ContinueCard.DataContext = continueCard;
        ContinueCard.Visibility = continueCard is null ? Visibility.Collapsed : Visibility.Visible;
        if (continueCard is not null)
            ContinueListenText.Text = continueCard.Entry.WasOpened && continueCard.Entry.Page > 1 ? $"Listen from page {continueCard.Entry.Page}" : "Start listening";

        RecentList.ItemsSource = recent;
        RecentHeader.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentHeader.Text = continueCard is null ? "Continue reading" : "Recently opened";

        bool hasFolder = _settings.LibraryFolder is not null;
        const int maxShown = 400;
        var all = hasFolder ? _library.FolderBooks.Where(Match).ToList() : new List<LibraryEntry>();
        FolderList.ItemsSource = all.Take(maxShown)
            .Select(e => new LibraryCard(e, string.Equals(e.Path, current, StringComparison.OrdinalIgnoreCase))).ToList();
        FolderHeader.Visibility = hasFolder ? Visibility.Visible : Visibility.Collapsed;
        FolderPathText.Text = _settings.LibraryFolder ?? "";
        FolderStatus.Visibility = hasFolder ? Visibility.Visible : Visibility.Collapsed;
        FolderStatus.Text = _scanningFolder ? "Looking for PDFs…"
            : all.Count == 0 ? (filter.Length > 0 ? "No books match." : "No PDFs found in this folder.")
            : all.Count > maxShown ? $"{all.Count} books · showing the first {maxShown}, type in Find a book to narrow it down"
            : $"{all.Count} book{(all.Count == 1 ? "" : "s")}";

        LibraryHero.Visibility = _library.Recent.Count == 0 && !hasFolder ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnContinueOpenClick(object sender, RoutedEventArgs e)
    {
        if (ContinueCard.DataContext is LibraryCard card) await OpenCardAsync(card);
    }

    /// <summary>Opens the book you were reading and starts reading aloud where you left off.</summary>
    private async void OnContinueListenClick(object sender, RoutedEventArgs e)
    {
        if (ContinueCard.DataContext is not LibraryCard card) return;
        await OpenCardAsync(card);
        if (_book is not null && string.Equals(_book.FilePath, card.Path, StringComparison.OrdinalIgnoreCase)
            && _narrator.State != NarrationState.Playing)
            TogglePlay();
    }

    private async void OnBookCardClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LibraryCard card) await OpenCardAsync(card);
    }

    private async Task OpenCardAsync(LibraryCard card)
    {
        if (card.IsMissing)
        {
            var answer = MessageBox.Show(this, $"This file can't be found any more:\n{card.Path}\n\nRemove it from the library?",
                "PDF Reader", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                _library.Remove(card.Path, alsoFromFolder: true);
                _library.Save(_libraryPath);
                RefreshLibrary();
            }
            return;
        }
        if (card.IsCurrent) { HideLibrary(); return; }
        await OpenAsync(card.Path);
    }

    private static LibraryCard? CardOf(object sender) => (sender as FrameworkElement)?.DataContext as LibraryCard;

    private async void OnCardOpenMenu(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) await OpenCardAsync(card);
    }

    private void OnCardShowInFolderMenu(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card) return;
        try
        {
            if (File.Exists(card.Path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{card.Path}\"");
            else if (Path.GetDirectoryName(card.Path) is { } dir && Directory.Exists(dir))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch { }
    }

    private void OnCardRemoveRecentMenu(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card) return;
        _library.Remove(card.Path, alsoFromFolder: false);
        _library.Save(_libraryPath);
        RefreshLibrary();
    }

    private void OnCardRemoveLibraryMenu(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card) return;
        _library.Remove(card.Path, alsoFromFolder: true);
        _library.Save(_libraryPath);
        RefreshLibrary();
    }

    private async void OnChooseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose your books folder" };
        if (_settings.LibraryFolder is { } current && Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) != true) return;
        _settings.LibraryFolder = dialog.FolderName;
        _settings.Save(_settingsPath);
        await RescanFolderAsync();
    }

    private async void OnRefreshFolderClick(object sender, RoutedEventArgs e) => await RescanFolderAsync();

    private void OnForgetFolderClick(object sender, RoutedEventArgs e)
    {
        _settings.LibraryFolder = null;
        _settings.Save(_settingsPath);
        _library.ClearFolder();
        _library.Save(_libraryPath);
        RefreshLibrary();
    }

    private async Task RescanFolderAsync()
    {
        if (_settings.LibraryFolder is not { } folder || _scanningFolder) return;
        _scanningFolder = true;
        RefreshLibrary();
        try
        {
            var found = await Task.Run(() => Directory.Exists(folder) ? LibraryStore.ScanFolder(folder) : new List<string>());
            _library.ApplyFolderScan(found);
            _library.Save(_libraryPath);
        }
        finally
        {
            _scanningFolder = false;
            RefreshLibrary();
        }
    }

    // ===== Bookmarks & notes =======================================================================

    private void RefreshBookmarks(Bookmark? select = null)
    {
        var items = _notes?.Items.Select(b =>
        {
            var item = new BookmarkItem(b);
            item.Changed += () => { _notesSave.Stop(); _notesSave.Start(); };
            return item;
        }).ToList() ?? new List<BookmarkItem>();

        BookmarksList.ItemsSource = items;
        BookmarksEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BookmarksCount.Text = items.Count switch { 0 => "", 1 => "1 bookmark", _ => $"{items.Count} bookmarks" };
        ExportNotesButton.IsEnabled = items.Count > 0;

        var marked = new HashSet<int>(_notes?.Items.Select(b => b.Paragraph) ?? Enumerable.Empty<int>());
        foreach (var p in _paragraphs) p.HasBookmark = marked.Contains(p.Model.Index);

        if (_book is not null && _notes is not null)
            PagesViewControl.SetBookmarks(_notes.Items
                .Where(b => b.Paragraph >= 0 && b.Paragraph < _book.Paragraphs.Count)
                .Select(b => (b.Paragraph, Math.Max(0, _book.Paragraphs[b.Paragraph].WordIndexAt(b.Char)))));
        else
            PagesViewControl.SetBookmarks(Array.Empty<(int, int)>());

        if (select is not null && items.FirstOrDefault(i => i.Bookmark.Id == select.Id) is { } sel)
        {
            BookmarksList.SelectedItem = sel;
            BookmarksList.ScrollIntoView(sel);
        }
    }

    private void OnAddBookmarkClick(object sender, RoutedEventArgs e) => AddBookmarkHere(focusNote: false);

    /// <summary>Bookmarks the cursor if there is one, otherwise the word being read.</summary>
    private void AddBookmarkHere(bool focusNote)
    {
        if (_book is null) return;
        var pos = _cursor is { } c
            ? new ReadingPosition(c.Paragraph, _book.Paragraphs[c.Paragraph].Words[c.Word].Start)
            : _narrator.Position;
        AddBookmarkAt(pos, focusNote);
    }

    private void AddBookmarkAt(ReadingPosition pos, bool focusNote)
    {
        if (_book is null || _notes is null || _book.IsEmpty) return;
        var b = _notes.Add(_book, pos);
        _notes.Save(BookNotes.DefaultFolder);
        RefreshBookmarks(select: b);
        ShowSidebar("bookmarks");
        ShowMessage($"Bookmarked page {b.Page}.");
        if (focusNote) Dispatcher.InvokeAsync(() => FocusNoteBox(b), DispatcherPriority.Loaded);
    }

    private void FocusNoteBox(Bookmark b)
    {
        var item = (BookmarksList.ItemsSource as IEnumerable<BookmarkItem>)?.FirstOrDefault(i => i.Bookmark.Id == b.Id);
        if (item is null || BookmarksList.ItemContainerGenerator.ContainerFromItem(item) is not DependencyObject container) return;
        if (FindDescendant<TextBox>(container) is { } box)
        {
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }
    }

    private void OnBookmarkClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d) return;
        if (FindAncestor<TextBox>(d) is not null || FindAncestor<Button>(d) is not null) return; // editing the note / deleting
        if (FindAncestor<ListBoxItem>(d) is not { DataContext: BookmarkItem item } || _book is null) return;
        var pos = item.Bookmark.Position;
        if (pos.ParagraphIndex < 0 || pos.ParagraphIndex >= _book.Paragraphs.Count) return;
        ClearCursor();
        if (_narrator.State == NarrationState.Playing) _narrator.PlayFrom(pos);
        else
        {
            _narrator.MoveTo(pos);
            ShowMessage($"Bookmark on page {item.Bookmark.Page}. Press Play to read from here.");
        }
        ResumeFollowing();
    }

    private void OnDeleteBookmarkClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BookmarkItem item || _notes is null) return;
        _notes.Remove(item.Bookmark.Id);
        _notes.Save(BookNotes.DefaultFolder);
        RefreshBookmarks();
    }

    private void OnExportNotesClick(object sender, RoutedEventArgs e)
    {
        if (_book is null || _notes is null || _notes.Items.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export bookmarks and notes",
            FileName = $"{_book.Title} - notes.md",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            InitialDirectory = Path.GetDirectoryName(_book.FilePath),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _notes.ToMarkdown(_book.Title));
            ShowMessage($"Saved {_notes.Items.Count} bookmark(s) to {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't save the file.\n\n" + ex.Message, "PDF Reader", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ===== Right-click on a word =====================================================================

    private void ShowWordMenu(int paragraphIndex, int wordIndex)
    {
        if (_book is null || paragraphIndex < 0 || paragraphIndex >= _book.Paragraphs.Count) return;
        var para = _book.Paragraphs[paragraphIndex];
        if (wordIndex < 0 || wordIndex >= para.Words.Count) return;
        var pos = new ReadingPosition(paragraphIndex, para.Words[wordIndex].Start);

        MenuItem Item(string header, Action action, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            mi.Click += (_, _) => action();
            return mi;
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("Read from here", () => { ClearCursor(); ResumeFollowing(); _narrator.PlayFrom(pos); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Add bookmark", () => AddBookmarkAt(pos, focusNote: false), "Ctrl+D"));
        menu.Items.Add(Item("Add note…", () => AddBookmarkAt(pos, focusNote: true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy paragraph", () => { try { Clipboard.SetText(para.Text); ShowMessage("Paragraph copied."); } catch { } }));
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // ===== Sidebar: Contents ======================================================================

    private void OnContentsTabClick(object sender, RoutedEventArgs e) => TogglePanel("contents");
    private void OnSearchTabClick(object sender, RoutedEventArgs e) => TogglePanel("search");
    private void OnBookmarksTabClick(object sender, RoutedEventArgs e) => TogglePanel("bookmarks");
    private void OnOptionsTabClick(object sender, RoutedEventArgs e) => TogglePanel("options");
    private void OnLookTabClick(object sender, RoutedEventArgs e) => TogglePanel("look");
    private void OnVoiceChipClick(object sender, RoutedEventArgs e) => TogglePanel("voices");
    private void OnClosePanelClick(object sender, RoutedEventArgs e) => HideSidebar();

    private static readonly string[] PanelNames = { "contents", "search", "bookmarks", "options", "look", "voices" };

    /// <summary>A top-bar button: opens its panel, or closes it if it's already open.</summary>
    private void TogglePanel(string tab)
    {
        if (Sidebar.Visibility == Visibility.Visible && _settings.SidebarTab == tab) HideSidebar();
        else ShowSidebar(tab);
    }

    /// <summary>Opens the side panel on: contents, search, bookmarks, options, look (appearance) or voices.</summary>
    private void ShowSidebar(string tab)
    {
        if (!PanelNames.Contains(tab)) tab = "contents";
        Sidebar.Visibility = Visibility.Visible;
        ContentsTab.IsChecked = tab == "contents";
        SearchTab.IsChecked = tab == "search";
        BookmarksTab.IsChecked = tab == "bookmarks";
        OptionsTab.IsChecked = tab == "options";
        LookTab.IsChecked = tab == "look";
        ContentsPanel.Visibility = tab == "contents" ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = tab == "search" ? Visibility.Visible : Visibility.Collapsed;
        BookmarksPanel.Visibility = tab == "bookmarks" ? Visibility.Visible : Visibility.Collapsed;
        OptionsPanel.Visibility = tab == "options" ? Visibility.Visible : Visibility.Collapsed;
        LookPanel.Visibility = tab == "look" ? Visibility.Visible : Visibility.Collapsed;
        VoicesPanel.Visibility = tab == "voices" ? Visibility.Visible : Visibility.Collapsed;
        PanelTitle.Text = tab switch
        {
            "search" => "Search",
            "bookmarks" => "Bookmarks",
            "options" => "Reading options",
            "look" => "Appearance",
            "voices" => "Voice and speed",
            _ => "Contents",
        };
        _settings.SidebarOpen = true;
        _settings.SidebarTab = tab;
        if (tab == "search")
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else
        {
            Keyboard.ClearFocus();
            if (tab == "contents") SyncContents(scroll: true);
            if (tab == "voices" && VoiceBox.SelectedItem is { } v) VoiceBox.ScrollIntoView(v);
        }
    }

    private void HideSidebar()
    {
        Sidebar.Visibility = Visibility.Collapsed;
        ContentsTab.IsChecked = SearchTab.IsChecked = BookmarksTab.IsChecked = OptionsTab.IsChecked = LookTab.IsChecked = false;
        _settings.SidebarOpen = false;
        Keyboard.ClearFocus();
    }

    private void OnContentsClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || FindAncestor<ListBoxItem>(d) is not { DataContext: OutlineItem item }) return;
        JumpToParagraph(item.Entry.ParagraphIndex);
        _syncingContents = true;
        ContentsList.SelectedItem = item;
        _syncingContents = false;
    }

    /// <summary>Selects the chapter being read in the Contents list.</summary>
    private void SyncContents(bool scroll = false)
    {
        if (_book is null || _outlineItems.Length == 0 || _syncingContents) return;
        int i = OutlineBuilder.EntryFor(_book.Outline, _narrator.Position.ParagraphIndex);
        var item = i >= 0 ? _outlineItems[i] : null;
        if (!ReferenceEquals(ContentsList.SelectedItem, item))
        {
            _syncingContents = true;
            ContentsList.SelectedItem = item;
            _syncingContents = false;
            scroll = true;
        }
        if (scroll && item is not null && Sidebar.Visibility == Visibility.Visible && ContentsPanel.Visibility == Visibility.Visible)
            ContentsList.ScrollIntoView(item);
    }

    /// <summary>Moves reading to the start of a paragraph (keeps reading if it was reading).</summary>
    private void JumpToParagraph(int paragraphIndex)
    {
        if (_book is null || paragraphIndex < 0 || paragraphIndex >= _book.Paragraphs.Count) return;
        ClearCursor();
        var pos = new ReadingPosition(paragraphIndex, 0);
        if (_narrator.State == NarrationState.Playing) _narrator.PlayFrom(pos);
        else _narrator.MoveTo(pos);
        ResumeFollowing();
        var p = _book.Paragraphs[paragraphIndex];
        if (_narrator.State != NarrationState.Playing)
            ShowMessage($"Moved to page {p.PageNumber}. Press Play to read from here.");
    }

    // ===== Sidebar: Search =======================================================================

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _searchDebounce.Stop();
            if (SearchResults.Items.Count == 0 || _searchIndex < 0 && SearchStatus.Tag as string != SearchBox.Text) RunSearch(thenGoToFirst: true);
            else NextSearchHit(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : +1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && SearchResults.Items.Count > 0)
        {
            NextSearchHit(+1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && SearchResults.Items.Count > 0)
        {
            NextSearchHit(-1);
            e.Handled = true;
        }
    }

    private async void RunSearch(bool thenGoToFirst = false)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        string query = SearchBox.Text;
        _searchIndex = -1;

        if (_book is not { } book)
        {
            SearchResults.ItemsSource = null;
            SearchStatus.Text = _pagesOnly ? "Search is ready once the text is prepared." : "";
            return;
        }
        if (query.Trim().Length < 2)
        {
            SearchResults.ItemsSource = null;
            SearchStatus.Text = query.Trim().Length == 1 ? "Type at least 2 letters." : "";
            SearchStatus.Tag = null;
            return;
        }

        const int max = 500;
        SearchStatus.Text = "Searching…";
        var hits = await Task.Run(() => BookSearch.Find(book, query, max));
        if (cts.IsCancellationRequested || !ReferenceEquals(book, _book)) return;

        SearchResults.ItemsSource = hits.Select(h => new SearchItem(h)).ToArray();
        SearchStatus.Tag = query;
        SearchStatus.Text = hits.Count switch
        {
            0 => "No matches.",
            1 => "1 match",
            >= max => $"{max}+ matches (showing the first {max})",
            _ => $"{hits.Count} matches · Enter / F3 for the next",
        };
        if (thenGoToFirst && hits.Count > 0) NextSearchHit(+1);
    }

    private void ClearSearch()
    {
        _searchCts?.Cancel();
        _searchIndex = -1;
        SearchResults.ItemsSource = null;
        SearchStatus.Text = "";
        SearchStatus.Tag = null;
    }

    private void OnSearchResultClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || FindAncestor<ListBoxItem>(d) is not { DataContext: SearchItem item }) return;
        _searchIndex = SearchResults.Items.IndexOf(item);
        GoToHit(item.Hit);
    }

    private void NextSearchHit(int direction)
    {
        int count = SearchResults.Items.Count;
        if (count == 0) { if (SearchBox.Text.Trim().Length >= 2) RunSearch(thenGoToFirst: true); return; }
        _searchIndex = _searchIndex < 0 ? (direction > 0 ? 0 : count - 1) : ((_searchIndex + direction) % count + count) % count;
        SearchResults.SelectedIndex = _searchIndex;
        SearchResults.ScrollIntoView(SearchResults.SelectedItem);
        if (SearchResults.SelectedItem is SearchItem item) GoToHit(item.Hit);
    }

    /// <summary>
    /// Shows a search result. While reading, it drops the cursor there (reading carries on; "Read from cursor" jumps);
    /// otherwise it moves the start point there.
    /// </summary>
    private void GoToHit(SearchHit hit)
    {
        if (_book is null) return;
        HandleWordClick(hit.ParagraphIndex, hit.WordIndex, 1);
        if (_narrator.State == NarrationState.Idle)
        {
            ResumeFollowing();
        }
        else
        {
            SuspendFollowing();
            ScrollToWord(hit.ParagraphIndex, hit.WordIndex);
        }
    }

    private void ScrollToWord(int paragraphIndex, int wordIndex)
    {
        if (PageMode) { PagesViewControl.ScrollToWord(paragraphIndex, wordIndex); return; }
        if (paragraphIndex < 0 || paragraphIndex >= _paragraphs.Length) return;
        var item = _paragraphs[paragraphIndex];
        ReaderList.ScrollIntoView(item);
        Dispatcher.InvokeAsync(() =>
        {
            var scroller = FindDescendant<ScrollViewer>(ReaderList);
            if (scroller is null || ReaderList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement c) return;
            try
            {
                var top = c.TransformToAncestor(scroller).Transform(new Point(0, 0));
                scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + top.Y - scroller.ViewportHeight * 0.3));
            }
            catch (InvalidOperationException) { }
        }, DispatcherPriority.Loaded);
    }

    // ===== Go to page ============================================================================

    private void OnPageBoxFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _editingPage = true;
        Dispatcher.InvokeAsync(PageBox.SelectAll, DispatcherPriority.Input);
    }

    private void OnPageBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _editingPage = false;
        UpdatePageBox();
    }

    private void OnPageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (int.TryParse(PageBox.Text.Trim(), out int page)) GoToPage(page);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private int TotalPages => _book?.PageCount ?? (_pagesOnly ? PagesViewControl.PageCount : 0);

    /// <summary>Scrolls the view to a page. Reading keeps its place; double-click a word there to read from it.</summary>
    private void GoToPage(int page)
    {
        int total = TotalPages;
        if (total == 0) return;
        page = Math.Clamp(page, 1, total);
        SuspendFollowing();

        if (PageMode || _pagesOnly) PagesViewControl.ScrollToPage(page);
        else if (ReaderList.ItemsSource is IEnumerable<object> items &&
                 items.FirstOrDefault(i => i is PageHeaderItem h && h.PageNumber >= page) is { } header)
        {
            ReaderList.ScrollIntoView(header);
            Dispatcher.InvokeAsync(() =>
            {
                var scroller = FindDescendant<ScrollViewer>(ReaderList);
                if (scroller is null || ReaderList.ItemContainerGenerator.ContainerFromItem(header) is not FrameworkElement c) return;
                try
                {
                    var top = c.TransformToAncestor(scroller).Transform(new Point(0, 0));
                    scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + top.Y));
                }
                catch (InvalidOperationException) { }
            }, DispatcherPriority.Loaded);
        }

        if (_book is not null && _narrator.State == NarrationState.Idle)
        {
            int first = _book.FirstParagraphOnPage(page);
            if (first >= 0 && _book.Paragraphs[first].PageNumber == page)
            {
                _narrator.MoveTo(new ReadingPosition(first, 0));
                ShowMessage($"Page {page}. Press Play to read from the top of this page.");
            }
        }
        UpdatePageBox();
    }

    /// <summary>Shows the page in view (page view) or the page being read (text view).</summary>
    private void UpdatePageBox()
    {
        if (!_ready) return;
        int total = TotalPages;
        PageTotal.Text = total > 0 ? $"of {total}" : "";
        PageBox.IsEnabled = total > 0;
        if (_editingPage) return;
        int page = 0;
        if ((PageMode || _pagesOnly) && PagesViewControl.PageCount > 0) page = PagesViewControl.VisiblePage;
        else if (_book is not null && !_book.IsEmpty)
            page = _book.Paragraphs[Math.Clamp(_narrator.Position.ParagraphIndex, 0, _book.Paragraphs.Count - 1)].PageNumber;
        PageBox.Text = page > 0 ? page.ToString() : "";
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
        SearchBox.IsEnabled = hasBook || _pagesOnly;
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
        FromCursorButton.Visibility = canSpeak && _cursor is not null ? Visibility.Visible : Visibility.Collapsed;
        BackToReadingButton.Visibility = hasBook && _followSuspended && FollowCheck.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;

        UpdateVoiceChip();
        UpdateStatus();
        UpdateMedia();
    }

    /// <summary>The note floating above the player ("Picked up where you left off…"), and the reading progress bar.</summary>
    private void UpdateStatus()
    {
        if (!_ready) return;
        string? text = _pagesOnly
            ? _message ?? $"Preparing text for reading aloud… {_textProgress * 100:0}%  You can look through the pages meanwhile."
            : _message;
        StatusText.Text = text ?? "";
        StatusToast.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

        if (_book is { IsEmpty: false } book)
        {
            int i = Math.Clamp(_narrator.Position.ParagraphIndex, 0, book.Paragraphs.Count - 1);
            ReadProgress.Value = book.Paragraphs.Count <= 1 ? 0 : (double)i / (book.Paragraphs.Count - 1);
        }
        else ReadProgress.Value = 0;
    }

    private static string SpeedText(double rate) => $"{rate:0.0#}×";

    /// <summary>"Lessac · 1.0×" on the player's voice button.</summary>
    private void UpdateVoiceChip()
    {
        if (VoiceChipText is null) return;
        string name = _narrator?.Voice?.Name ?? "Choose a voice";
        VoiceChipText.Text = $"{name} · {SpeedText(SpeedSlider.Value)}";

        // Light up the quick speed that matches.
        foreach (var child in QuickSpeeds.Children)
        {
            if (child is not Button b || b.Tag is not string tag ||
                !double.TryParse(tag, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out double r)) continue;
            bool on = Math.Abs(r - SpeedSlider.Value) < 0.001;
            if (on)
            {
                b.SetResourceReference(BorderBrushProperty, "AccentBrush");
                b.SetResourceReference(BackgroundProperty, "SelectedBrush");
                b.SetResourceReference(ForegroundProperty, "SelectedTextBrush");
            }
            else
            {
                b.ClearValue(BorderBrushProperty);
                b.ClearValue(BackgroundProperty);
                b.ClearValue(ForegroundProperty);
            }
        }
    }

    private static System.Windows.Data.ListCollectionView GroupedVoices(IReadOnlyList<VoiceOption> voices)
    {
        var view = new System.Windows.Data.ListCollectionView(voices.ToList());
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(VoiceOption.Group)));
        return view;
    }

    private void OnQuickSpeedClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag &&
            double.TryParse(tag, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
            SpeedSlider.Value = rate;
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => Zoom(+1);
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Zoom(-1);

    private void OnFitWidthClick(object sender, RoutedEventArgs e)
    {
        if (!(PageMode || _pagesOnly)) return;
        PagesViewControl.FitWidth();
        _settings.PageZoom = null;
        UpdateZoomLabel();
    }

    private void OnFontSizeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || Math.Abs(ReaderList.FontSize - e.NewValue) < 0.01) return;
        ChangeFontSize((int)Math.Round(e.NewValue - ReaderList.FontSize));
    }

    private void SaveProgress()
    {
        if (_book is null) return;
        _settings.Remember(_book.FilePath, _narrator.Position);
        _settings.Save(_settingsPath);
        UpdateLibraryProgress();
    }

    /// <summary>Stores where you are in the current book for its library card.</summary>
    private void UpdateLibraryProgress()
    {
        if (_book is null) return;
        var pos = _narrator.Position;
        int i = Math.Clamp(pos.ParagraphIndex, 0, Math.Max(0, _book.Paragraphs.Count - 1));
        if (_book.Paragraphs.Count > 0)
        {
            double progress = _book.Paragraphs.Count <= 1 ? 0 : (double)i / (_book.Paragraphs.Count - 1);
            _library.UpdateProgress(_book.FilePath, _book.Paragraphs[i].PageNumber, progress);
            _library.Save(_libraryPath);
        }
    }

    private void Shutdown()
    {
        _loadCts?.Cancel();
        SaveProgress();
        _settings.Maximized = WindowState == WindowState.Maximized;
        _settings.Save(_settingsPath);
        _narrator.Dispose();
        _renderer?.Dispose();
        _notesSave.Stop();
        _notes?.Save(BookNotes.DefaultFolder);
        _library.Save(_libraryPath);
    }

    // ===== Background listening: media keys, Windows media panel, taskbar buttons ================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Theme.ApplyTitleBar(this);
        _media = MediaSession.TryCreate(new System.Windows.Interop.WindowInteropHelper(this).Handle, Dispatcher);
        if (_media is not null) _media.ButtonPressed += OnMediaButton;
        SetUpTaskbarButtons();
        UpdateMedia();
    }

    private void OnMediaButton(global::Windows.Media.SystemMediaTransportControlsButton button)
    {
        switch (button)
        {
            case global::Windows.Media.SystemMediaTransportControlsButton.Play:
                if (_narrator.State != NarrationState.Playing) TogglePlay();
                break;
            case global::Windows.Media.SystemMediaTransportControlsButton.Pause:
                if (_narrator.State == NarrationState.Playing) _narrator.Pause();
                break;
            case global::Windows.Media.SystemMediaTransportControlsButton.Stop:
                StopReading();
                break;
            case global::Windows.Media.SystemMediaTransportControlsButton.Next:
                Skip(+1);
                break;
            case global::Windows.Media.SystemMediaTransportControlsButton.Previous:
                Skip(-1);
                break;
        }
    }

    /// <summary>Previous / Play-Pause / Next under the taskbar thumbnail.</summary>
    private void SetUpTaskbarButtons()
    {
        var ink = TaskbarUsesLightTheme() ? Color.FromRgb(0x20, 0x20, 0x20) : Colors.White;
        _thumbPlayImage = GlyphImage("\uE768", ink);
        _thumbPauseImage = GlyphImage("\uE769", ink);
        _thumbPrev = new ThumbButtonInfo { Description = "Previous paragraph", ImageSource = GlyphImage("\uE892", ink), DismissWhenClicked = false };
        _thumbPlay = new ThumbButtonInfo { Description = "Play", ImageSource = _thumbPlayImage, DismissWhenClicked = false };
        _thumbNext = new ThumbButtonInfo { Description = "Next paragraph", ImageSource = GlyphImage("\uE893", ink), DismissWhenClicked = false };
        _thumbPrev.Click += (_, _) => Skip(-1);
        _thumbPlay.Click += (_, _) => TogglePlay();
        _thumbNext.Click += (_, _) => Skip(+1);
        TaskbarItemInfo = new TaskbarItemInfo
        {
            ThumbButtonInfos = new ThumbButtonInfoCollection { _thumbPrev, _thumbPlay, _thumbNext },
        };
    }

    private static bool TaskbarUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>An icon-font glyph as a small picture (for the taskbar buttons).</summary>
    private ImageSource GlyphImage(string glyph, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var typeface = new Typeface((FontFamily)FindResource("IconFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 16, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(brush, null, text.BuildGeometry(new Point((16 - text.Width) / 2, (16 - text.Height) / 2))));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>Keeps the media panel and taskbar buttons in step with the reader.</summary>
    private void UpdateMedia()
    {
        if (!_ready) return;
        bool canSpeak = _book is not null && _narrator.Voice is not null;
        bool playing = _narrator.State == NarrationState.Playing;
        if (_thumbPlay is not null)
        {
            _thumbPlay.ImageSource = playing ? _thumbPauseImage : _thumbPlayImage;
            _thumbPlay.Description = playing ? "Pause" : _narrator.State == NarrationState.Paused ? "Resume" : "Play";
            _thumbPlay.IsEnabled = canSpeak;
            _thumbPrev!.IsEnabled = canSpeak;
            _thumbNext!.IsEnabled = canSpeak;
        }

        // Under the title in the top bar: the chapter being read.
        if (_book is { IsEmpty: false } open)
        {
            int para = Math.Clamp(_narrator.Position.ParagraphIndex, 0, open.Paragraphs.Count - 1);
            int ch = OutlineBuilder.EntryFor(open.Outline, para);
            BookInfo.Text = ch >= 0 && ch < open.Outline.Count ? open.Outline[ch].Title : $"{open.PageCount} pages";
        }

        if (_media is null) return;
        _media.SetState(_book is not null && !_book.IsEmpty, _narrator.State);
        if (_book is { IsEmpty: false } book)
            _media.SetInfo(book.Title, MediaSubtitle(book), CoverCache.SavedFile(book.FilePath));
    }

    /// <summary>"Chapter · Page 12 of 80" for the media panel.</summary>
    private string MediaSubtitle(BookDocument book)
    {
        int para = Math.Clamp(_narrator.Position.ParagraphIndex, 0, book.Paragraphs.Count - 1);
        string page = $"Page {book.Paragraphs[para].PageNumber} of {book.PageCount}";
        int i = OutlineBuilder.EntryFor(book.Outline, para);
        return i >= 0 && i < book.Outline.Count ? $"{book.Outline[i].Title} · {page}" : page;
    }

    private async Task EnsureCoverAsync(string pdfPath)
    {
        if (CoverCache.SavedFile(pdfPath) is null) await CoverCache.GetAsync(pdfPath);
        UpdateMedia();
    }

    // ===== Theme ==================================================================================

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.Theme = DarkThemeRadio.IsChecked == true ? Theme.Dark : SepiaThemeRadio.IsChecked == true ? Theme.Sepia : Theme.Light;
        ApplyTheme();
    }

    private void OnMatchPagesChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.MatchPageColors = MatchPagesCheck.IsChecked == true;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        Theme.Apply(_settings.Theme, _settings.MatchPageColors);
        var tint = Theme.TintFor(_settings.Theme, _settings.MatchPageColors);
        PagesViewControl.SetDarkPages(tint == PageTint.Dark);
        if (_renderer is not null && _renderer.Tint != tint)
        {
            _renderer.Tint = tint;
            PagesViewControl.Redraw();
        }
        _settings.Save(_settingsPath);
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
