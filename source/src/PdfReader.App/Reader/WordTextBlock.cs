using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PdfReader.App.Reader;

public sealed class WordClickedEventArgs : RoutedEventArgs
{
    public WordClickedEventArgs(RoutedEvent e, ParagraphItem item, int wordIndex, int clickCount) : base(e)
    {
        Item = item;
        WordIndex = wordIndex;
        ClickCount = clickCount;
    }

    public ParagraphItem Item { get; }
    public int WordIndex { get; }
    public int ClickCount { get; }
}

public delegate void WordClickedEventHandler(object sender, WordClickedEventArgs e);

/// <summary>
/// Shows one paragraph as a run per word so single words can be highlighted and clicked.
/// </summary>
public sealed class WordTextBlock : TextBlock
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(ParagraphItem), typeof(WordTextBlock), new PropertyMetadata(null, OnItemChanged));

    public static readonly RoutedEvent WordClickedEvent = EventManager.RegisterRoutedEvent(
        "WordClicked", RoutingStrategy.Bubble, typeof(WordClickedEventHandler), typeof(WordTextBlock));

    /// <summary>Right-click on a word (ClickCount is 1).</summary>
    public static readonly RoutedEvent WordRightClickedEvent = EventManager.RegisterRoutedEvent(
        "WordRightClicked", RoutingStrategy.Bubble, typeof(WordClickedEventHandler), typeof(WordTextBlock));

    private Run[] _wordRuns = Array.Empty<Run>();
    private Run?[] _gapRuns = Array.Empty<Run?>();
    private readonly Dictionary<Run, int> _runToWord = new();
    private int _shownReading = -1;
    private int _shownReadingEnd = -1;
    private int _shownCursor = -1;

    public WordTextBlock()
    {
        TextWrapping = TextWrapping.Wrap;
        Cursor = Cursors.Hand;
        // Only listen to the item while on screen, so recycled/unloaded controls don't leak.
        Loaded += (_, _) => { if (Item is { } i) { i.PropertyChanged -= OnItemPropertyChanged; i.PropertyChanged += OnItemPropertyChanged; ApplyHighlights(); } };
        Unloaded += (_, _) => { if (Item is { } i) i.PropertyChanged -= OnItemPropertyChanged; };
    }

    public ParagraphItem? Item
    {
        get => (ParagraphItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public event WordClickedEventHandler WordClicked
    {
        add => AddHandler(WordClickedEvent, value);
        remove => RemoveHandler(WordClickedEvent, value);
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tb = (WordTextBlock)d;
        if (e.OldValue is ParagraphItem oldItem) oldItem.PropertyChanged -= tb.OnItemPropertyChanged;
        if (e.NewValue is ParagraphItem newItem) newItem.PropertyChanged += tb.OnItemPropertyChanged;
        tb.Rebuild();
    }

    static WordTextBlock()
    {
        // Keep generous line spacing in step with the font size (Ctrl + mouse wheel changes it).
        FontSizeProperty.OverrideMetadata(typeof(WordTextBlock),
            new FrameworkPropertyMetadata((d, _) => ((WordTextBlock)d).LineHeight = ((WordTextBlock)d).FontSize * 1.55));
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ParagraphItem.ReadingWord) or nameof(ParagraphItem.ReadingWordEnd) or nameof(ParagraphItem.CursorWord))
            ApplyHighlights();
    }

    private void Rebuild()
    {
        Inlines.Clear();
        _runToWord.Clear();
        _shownReading = _shownReadingEnd = _shownCursor = -1;
        var item = Item;
        if (item is null) { _wordRuns = Array.Empty<Run>(); _gapRuns = Array.Empty<Run?>(); return; }

        string text = item.Model.Text;
        var words = item.Model.Words;
        _wordRuns = new Run[words.Count];
        _gapRuns = new Run?[words.Count];
        int pos = 0;
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (w.Start > pos)
            {
                var gap = new Run(text.Substring(pos, w.Start - pos));
                _runToWord[gap] = i; // clicking a space picks the next word
                _gapRuns[i] = gap;
                Inlines.Add(gap);
            }
            var run = new Run(text.Substring(w.Start, w.Length));
            _wordRuns[i] = run;
            _runToWord[run] = i;
            Inlines.Add(run);
            pos = w.End;
        }
        if (pos < text.Length) Inlines.Add(new Run(text[pos..]));
        LineHeight = FontSize * 1.55;
        ApplyHighlights();
    }

    private void ApplyHighlights()
    {
        var item = Item;
        int reading = item?.ReadingWord ?? -1;
        int readingEnd = reading < 0 ? -1 : Math.Max(reading, item?.ReadingWordEnd ?? reading);
        int cursor = item?.CursorWord ?? -1;
        if (reading == _shownReading && readingEnd == _shownReadingEnd && cursor == _shownCursor) return;

        int oldStart = _shownReading, oldEnd = _shownReadingEnd, oldCursor = _shownCursor;
        _shownReading = reading;
        _shownReadingEnd = readingEnd;
        _shownCursor = cursor;
        for (int i = oldStart; i <= oldEnd && i >= 0; i++) StyleWord(i);
        StyleWord(oldCursor);
        for (int i = reading; i <= readingEnd && i >= 0; i++) StyleWord(i);
        StyleWord(cursor);
    }

    private bool IsReading(int index) => _shownReading >= 0 && index >= _shownReading && index <= _shownReadingEnd;

    private void StyleWord(int index)
    {
        if (index < 0 || index >= _wordRuns.Length) return;
        var run = _wordRuns[index];
        if (IsReading(index))
        {
            // A whole sentence (natural voices) gets a softer colour than a single word.
            bool sentence = _shownReadingEnd > _shownReading;
            run.Background = (Brush)FindResource(sentence ? "ReadingSentenceBrush" : "ReadingWordBrush");
            run.Foreground = (Brush)FindResource("ReadingWordTextBrush");
        }
        else
        {
            run.ClearValue(TextElement.BackgroundProperty);
            run.ClearValue(TextElement.ForegroundProperty);
        }

        if (index == _shownCursor)
        {
            run.TextDecorations = CursorDecoration();
            if (!IsReading(index)) run.Background = (Brush)FindResource("CursorWordBrush");
        }
        else if (IsReading(index) && _shownReadingEnd > _shownReading)
        {
            run.TextDecorations = SentenceDecoration; // stays visible on warm (Night light) screens
        }
        else
        {
            run.TextDecorations = null;
        }

        // In a highlighted sentence the spaces between words are highlighted too, so it reads as one band.
        if (index < _gapRuns.Length && _gapRuns[index] is { } gap)
        {
            if (index > _shownReading && IsReading(index) && _shownReadingEnd > _shownReading)
            {
                gap.Background = (Brush)FindResource("ReadingSentenceBrush");
                gap.TextDecorations = SentenceDecoration;
            }
            else
            {
                gap.ClearValue(TextElement.BackgroundProperty);
                gap.TextDecorations = null;
            }
        }
    }

    private static TextDecorationCollection? _sentenceDecoration;

    private static TextDecorationCollection SentenceDecoration
    {
        get
        {
            if (_sentenceDecoration is not null) return _sentenceDecoration;
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x9A, 0x00)), 2);
            pen.Freeze();
            var deco = new TextDecorationCollection { new TextDecoration(TextDecorationLocation.Underline, pen, 2, TextDecorationUnit.FontRecommended, TextDecorationUnit.Pixel) };
            deco.Freeze();
            return _sentenceDecoration = deco;
        }
    }

    private TextDecorationCollection CursorDecoration()
    {
        var pen = new Pen((Brush)FindResource("AccentBrush"), 2.5);
        pen.Freeze();
        var deco = new TextDecoration(TextDecorationLocation.Underline, pen, 2, TextDecorationUnit.FontRecommended, TextDecorationUnit.Pixel);
        return new TextDecorationCollection { deco };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var item = Item;
        if (item is null) return;
        if (e.OriginalSource is Run run && _runToWord.TryGetValue(run, out int word))
        {
            RaiseEvent(new WordClickedEventArgs(WordClickedEvent, item, word, e.ClickCount));
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var item = Item;
        if (item is null) return;
        if (e.OriginalSource is Run run && _runToWord.TryGetValue(run, out int word))
        {
            RaiseEvent(new WordClickedEventArgs(WordRightClickedEvent, item, word, 1));
            e.Handled = true;
        }
    }

    /// <summary>Where a word sits, in this control's coordinates (empty if unknown).</summary>
    public Rect GetWordRect(int index)
    {
        if (index < 0 || index >= _wordRuns.Length) return Rect.Empty;
        var run = _wordRuns[index];
        var a = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var b = run.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        a.Union(b);
        return a;
    }
}
