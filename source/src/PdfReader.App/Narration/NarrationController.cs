using System.Windows.Threading;
using PdfReader.App.Speech;
using PdfReader.Core;

namespace PdfReader.App.Narration;

public enum NarrationState { Idle, Playing, Paused }

/// <summary>
/// Drives reading aloud: walks the book chunk by chunk, keeps the next chunk prepared,
/// and reports which word is being spoken. Everything here runs on the UI thread;
/// engine callbacks are marshalled back through the dispatcher.
/// </summary>
public sealed class NarrationController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly OneCoreSpeechEngine? _oneCore;
    private readonly SapiSpeechEngine? _sapi;
    private readonly PiperSpeechEngine? _piper;

    private BookDocument? _book;
    private ReadingPlan? _plan;
    private int _generation;
    private CancellationTokenSource _cts = new();
    private SpeechChunk? _nextChunk;
    private Task<IPreparedSpeech>? _nextPrepared;
    private SpeechInput? _nextInput;
    private VoiceOption? _voice;
    private double _rate = 1.0;
    private Func<Paragraph, bool> _isSpoken = _ => true;

    /// <summary>Short silence before and after headings so they sound like headings.</summary>
    public bool PauseAroundHeadings { get; set; } = true;
    private static readonly TimeSpan HeadingPause = TimeSpan.FromMilliseconds(450);

    public NarrationController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _oneCore = OneCoreSpeechEngine.TryCreate();
        _sapi = SapiSpeechEngine.TryCreate();
        try { _piper = new PiperSpeechEngine(); } catch { _piper = null; }
        RefreshVoices();
        DefaultVoiceKey = _oneCore?.DefaultVoiceKey ?? Voices.FirstOrDefault()?.Key;
    }

    /// <summary>Natural (Piper) voices first, then the Windows voices.</summary>
    public IReadOnlyList<VoiceOption> Voices { get; private set; } = Array.Empty<VoiceOption>();
    public string? DefaultVoiceKey { get; }

    /// <summary>How text is turned into what is said (abbreviations, code names, the user's word list).</summary>
    public Pronouncer Pronouncer { get; } = new();

    /// <summary>Re-reads the installed voices (after downloading or removing a natural voice).</summary>
    public void RefreshVoices()
    {
        _piper?.Refresh();
        var voices = new List<VoiceOption>();
        if (_piper is not null) voices.AddRange(_piper.Voices);
        if (_oneCore is not null) voices.AddRange(_oneCore.Voices.OrderBy(v => v.Name));
        if (_sapi is not null)
        {
            // A SAPI voice that also exists as a modern voice is a duplicate; keep the modern one.
            var modernNames = voices.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            voices.AddRange(_sapi.Voices.Where(v => !modernNames.Contains(v.Name)).OrderBy(v => v.Name));
        }
        Voices = voices;
    }

    /// <summary>Pronunciation settings changed: carry on from the current word with the new rules.</summary>
    public void PronunciationChanged() => RestartIfPlaying();

    public NarrationState State { get; private set; } = NarrationState.Idle;

    /// <summary>Start of the word being read (or where reading will start next).</summary>
    public ReadingPosition Position { get; private set; } = ReadingPosition.Start;

    public event Action? StateChanged;
    /// <summary>The highlight moved: (paragraph index, first word, last word). One word for Windows voices, a sentence for natural voices.</summary>
    public event Action<int, int, int>? HighlightChanged;
    public event Action? ReachedEnd;
    public event Action<string>? Error;

    public VoiceOption? Voice
    {
        get => _voice;
        set
        {
            if (Equals(_voice, value)) return;
            _voice = value;
            RestartIfPlaying();
        }
    }

    public double Rate
    {
        get => _rate;
        set
        {
            if (Math.Abs(_rate - value) < 0.001) return;
            _rate = value;
            RestartIfPlaying();
        }
    }

    public void Load(BookDocument book, ReadingPosition start, Func<Paragraph, bool> isSpoken)
    {
        CancelAll();
        _book = book;
        _isSpoken = isSpoken;
        _plan = new ReadingPlan(book, isSpoken);
        // Start on something that is read aloud (not a header, the contents page, ...).
        Position = _plan.ChunkAt(start) is { } first ? new ReadingPosition(first.ParagraphIndex, first.Start) : start;
        SetState(NarrationState.Idle);
        RaiseWord(Position);
    }

    /// <summary>Reading options changed: rebuild the plan and carry on from the current word.</summary>
    public void SetSpokenFilter(Func<Paragraph, bool> isSpoken)
    {
        _isSpoken = isSpoken;
        if (_book is null) return;
        _plan = new ReadingPlan(_book, isSpoken);
        RestartIfPlaying();
    }

    // ---- transport -------------------------------------------------------------------------

    public void PlayFrom(ReadingPosition pos)
    {
        if (_plan is null || _voice is null) return;
        CancelAll();
        int gen = _generation;

        var chunk = _plan.ChunkAt(pos);
        if (chunk is null)
        {
            SetState(NarrationState.Idle);
            ReachedEnd?.Invoke();
            return;
        }
        SetState(NarrationState.Playing);
        _ = StartChunkAsync(chunk.Value, gen, null);
    }

    public void Pause()
    {
        if (State != NarrationState.Playing) return;
        EngineFor(_voice)?.Pause();
        SetState(NarrationState.Paused);
    }

    public void Resume()
    {
        if (State != NarrationState.Paused) return;
        EngineFor(_voice)?.Resume();
        SetState(NarrationState.Playing);
    }

    /// <summary>Stops reading and goes back to the start of the current paragraph.</summary>
    public void Stop()
    {
        CancelAll();
        Position = new ReadingPosition(Position.ParagraphIndex, 0);
        SetState(NarrationState.Idle);
        RaiseWord(Position);
    }

    /// <summary>Moves the reading position without speaking (stops any paused audio).</summary>
    public void MoveTo(ReadingPosition pos)
    {
        CancelAll();
        Position = Clamp(pos);
        SetState(NarrationState.Idle);
        RaiseWord(Position);
    }

    public void NextParagraph() => JumpToParagraph(Position.ParagraphIndex + 1, +1);
    public void PreviousParagraph() => JumpToParagraph(Position.ParagraphIndex - 1, -1);

    private void JumpToParagraph(int index, int direction)
    {
        if (_book is null || _book.IsEmpty || _plan is null) return;
        index = Math.Clamp(index, 0, _book.Paragraphs.Count - 1);
        // Land on a paragraph that is actually read aloud (skip footers, footnotes, ...).
        index = _plan.FindSpoken(index, direction) ?? _plan.FindSpoken(index, -direction) ?? index;
        var target = new ReadingPosition(index, 0);
        if (State == NarrationState.Playing) PlayFrom(target);
        else MoveTo(target);
    }

    // ---- chunk pipeline --------------------------------------------------------------------

    private async Task StartChunkAsync(SpeechChunk chunk, int gen, Task<IPreparedSpeech>? preparedTask)
    {
        var engine = EngineFor(_voice);
        if (engine is null || _plan is null || _voice is null) return;
        var ct = _cts.Token;

        IPreparedSpeech prepared;
        var input = (preparedTask is not null ? _currentInputForNext : null) ?? SpeechInput.Build(_plan.TextOf(chunk), Pronouncer);
        try
        {
            prepared = await (preparedTask ?? engine.PrepareAsync(input, _voice, _rate, ct));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (gen == _generation) Fail("Could not prepare speech: " + ex.Message);
            return;
        }

        if (gen != _generation) { prepared.Dispose(); return; }

        _currentChunk = chunk;
        Position = new ReadingPosition(chunk.ParagraphIndex, chunk.Start);
        RaiseWord(Position);

        // Prepare the following chunk while this one plays.
        _nextChunk = _plan.After(chunk);
        _nextInput = _nextChunk is { } next ? SpeechInput.Build(_plan.TextOf(next), Pronouncer) : null;
        _nextPrepared = _nextChunk is not null && _nextInput is not null ? engine.PrepareAsync(_nextInput, _voice, _rate, ct) : null;

        engine.Play(prepared, new SpeechCallbacks
        {
            OnWord = spoken => _dispatcher.InvokeAsync(() => OnWord(gen, chunk, input.Spoken.ToOriginal(spoken))),
            OnSentence = i => _dispatcher.InvokeAsync(() => OnSentence(gen, chunk, input, i)),
            OnFinished = () => _dispatcher.InvokeAsync(() => OnChunkFinished(gen)),
            OnError = ex => _dispatcher.InvokeAsync(() => { if (gen == _generation) Fail("Speech failed: " + ex.Message); }),
        });

        if (State == NarrationState.Paused) engine.Pause(); // paused while we were preparing
    }

    private void OnWord(int gen, SpeechChunk chunk, int localIndex)
    {
        if (gen != _generation || _book is null) return;
        var para = _book.Paragraphs[chunk.ParagraphIndex];
        int abs = Math.Clamp(chunk.Start + localIndex, 0, Math.Max(0, para.Text.Length - 1));
        int word = para.WordIndexAt(abs);
        if (word < 0) return;
        Position = new ReadingPosition(chunk.ParagraphIndex, para.Words[word].Start);
        HighlightChanged?.Invoke(chunk.ParagraphIndex, word, word);
    }

    /// <summary>A natural voice started a sentence: highlight all of it.</summary>
    private void OnSentence(int gen, SpeechChunk chunk, SpeechInput input, int index)
    {
        if (gen != _generation || _book is null || index < 0 || index >= input.Sentences.Count) return;
        var para = _book.Paragraphs[chunk.ParagraphIndex];
        var seg = input.Sentences[index];
        int first = para.WordIndexAt(chunk.Start + seg.OriginalStart);
        int last = para.WordIndexAt(Math.Max(chunk.Start + seg.OriginalStart, chunk.Start + seg.OriginalStart + seg.OriginalLength - 1));
        if (first < 0) return;
        Position = new ReadingPosition(chunk.ParagraphIndex, para.Words[first].Start);
        HighlightChanged?.Invoke(chunk.ParagraphIndex, first, Math.Max(first, last));
    }

    private SpeechInput? _currentInputForNext;

    private SpeechChunk? _currentChunk;

    private async void OnChunkFinished(int gen)
    {
        if (gen != _generation) return;
        if (_nextChunk is { } next)
        {
            var prepared = _nextPrepared;
            _currentInputForNext = _nextInput;
            _nextChunk = null;
            _nextPrepared = null;
            _nextInput = null;

            if (PauseAroundHeadings && _book is not null && _currentChunk is { } done && done.ParagraphIndex != next.ParagraphIndex &&
                (_book.Paragraphs[done.ParagraphIndex].Kind == ParagraphKind.Heading || _book.Paragraphs[next.ParagraphIndex].Kind == ParagraphKind.Heading))
            {
                await Task.Delay(HeadingPause);
                if (gen != _generation) { prepared?.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); }, TaskScheduler.Default); return; }
                while (State == NarrationState.Paused && gen == _generation) await Task.Delay(100);
                if (gen != _generation) return;
            }
            _ = StartChunkAsync(next, gen, prepared);
        }
        else
        {
            CancelAll();
            SetState(NarrationState.Idle);
            ReachedEnd?.Invoke();
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private void RestartIfPlaying()
    {
        if (State == NarrationState.Playing) PlayFrom(Position);
        else if (State == NarrationState.Paused) MoveTo(Position); // a paused clip has the old voice/speed baked in
    }

    private ISpeechEngine? EngineFor(VoiceOption? voice) => voice?.Kind switch
    {
        VoiceEngineKind.OneCore => _oneCore,
        VoiceEngineKind.Sapi => _sapi,
        VoiceEngineKind.Piper => _piper,
        _ => null,
    };

    private void CancelAll()
    {
        _generation++;
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        var leftover = _nextPrepared;
        _nextPrepared = null;
        _nextChunk = null;
        _nextInput = null;
        _currentInputForNext = null;
        leftover?.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); }, TaskScheduler.Default);

        _oneCore?.Stop();
        _sapi?.Stop();
        _piper?.Stop();
    }

    private ReadingPosition Clamp(ReadingPosition pos)
    {
        if (_book is null || _book.IsEmpty) return ReadingPosition.Start;
        int p = Math.Clamp(pos.ParagraphIndex, 0, _book.Paragraphs.Count - 1);
        return new ReadingPosition(p, Math.Clamp(pos.CharOffset, 0, _book.Paragraphs[p].Text.Length));
    }

    private void RaiseWord(ReadingPosition pos)
    {
        if (_book is null || _book.IsEmpty) return;
        var para = _book.Paragraphs[pos.ParagraphIndex];
        int word = para.WordIndexAt(pos.CharOffset);
        if (word >= 0) HighlightChanged?.Invoke(pos.ParagraphIndex, word, word);
    }

    private void SetState(NarrationState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke();
    }

    private void Fail(string message)
    {
        CancelAll();
        SetState(NarrationState.Idle);
        Error?.Invoke(message);
    }

    public void Dispose()
    {
        CancelAll();
        _oneCore?.Dispose();
        _sapi?.Dispose();
        _piper?.Dispose();
    }
}
