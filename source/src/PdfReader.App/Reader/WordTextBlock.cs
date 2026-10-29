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
    private readonly Dictionary<Run, int> _runToWord = new();
    private int _shownReading = -1;
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
        if (e.PropertyName is nameof(ParagraphItem.ReadingWord) or nameof(ParagraphItem.CursorWord))
            ApplyHighlights();
    }

    private void Rebuild()
    {
        Inlines.Clear();
        _runToWord.Clear();
        _shownReading = _shownCursor = -1;
        var item = Item;
        if (item is null) { _wordRuns = Array.Empty<Run>(); return; }

        string text = item.Model.Text;
        var words = item.Model.Words;
        _wordRuns = new Run[words.Count];
        int pos = 0;
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (w.Start > pos)
            {
                var gap = new Run(text.Substring(pos, w.Start - pos));
                _runToWord[gap] = i; // clicking a space picks the next word
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
        int cursor = item?.CursorWord ?? -1;
        if (reading == _shownReading && cursor == _shownCursor) return;

        int oldReading = _shownReading, oldCursor = _shownCursor;
        _shownReading = reading;
        _shownCursor = cursor;
        StyleWord(oldReading);
        StyleWord(oldCursor);
        StyleWord(reading);
        StyleWord(cursor);
    }

    private void StyleWord(int index)
    {
        if (index < 0 || index >= _wordRuns.Length) return;
        var run = _wordRuns[index];
        if (index == _shownReading)
        {
            run.Background = (Brush)FindResource("ReadingWordBrush");
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
            if (index != _shownReading) run.Background = (Brush)FindResource("CursorWordBrush");
        }
        else
        {
            run.TextDecorations = null;
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
