using PdfReader.Core;
using global::Windows.Foundation.Collections;
using global::Windows.Media.Core;
using global::Windows.Media.Playback;
using WinSpeech = global::Windows.Media.SpeechSynthesis;

namespace PdfReader.App.Speech;

/// <summary>
/// The modern Windows voices (the ones Narrator and Settings › Speech use).
/// Text is synthesized to an audio stream with word-boundary cues, then played with MediaPlayer;
/// each cue tells us which word is being spoken.
/// </summary>
public sealed class OneCoreSpeechEngine : ISpeechEngine
{
    private readonly WinSpeech.SpeechSynthesizer _synth = new();
    private readonly SemaphoreSlim _synthLock = new(1, 1);
    private readonly MediaPlayer _player;
    private Session? _current;

    private sealed class Prepared : IPreparedSpeech
    {
        public Prepared(WinSpeech.SpeechSynthesisStream stream) => Stream = stream;
        public WinSpeech.SpeechSynthesisStream Stream { get; }
        public void Dispose() => Stream.Dispose();
    }

    private sealed class Session
    {
        public required Prepared Speech { get; init; }
        public required MediaSource Source { get; init; }
        public required MediaPlaybackItem Item { get; init; }
        public required SpeechCallbacks Callbacks { get; init; }
        public HashSet<TimedMetadataTrack> Hooked { get; } = new();

        public void Dispose()
        {
            try { Source.Dispose(); } catch { }
            try { Speech.Dispose(); } catch { }
        }
    }

    private OneCoreSpeechEngine()
    {
        Voices = WinSpeech.SpeechSynthesizer.AllVoices
            .Select(v => new VoiceOption("onecore:" + v.Id, v.DisplayName, v.Language, VoiceEngineKind.OneCore, v.Id))
            .ToList();

        var def = WinSpeech.SpeechSynthesizer.DefaultVoice;
        DefaultVoiceKey = def is null ? null : "onecore:" + def.Id;

        _player = new MediaPlayer { AutoPlay = false };
        _player.CommandManager.IsEnabled = false; // keep it out of the Windows media overlay
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
    }

    /// <summary>The voice selected in Windows Settings › Time &amp; language › Speech.</summary>
    public string? DefaultVoiceKey { get; }

    public IReadOnlyList<VoiceOption> Voices { get; }

    public static OneCoreSpeechEngine? TryCreate()
    {
        try
        {
            var engine = new OneCoreSpeechEngine();
            return engine.Voices.Count > 0 ? engine : null;
        }
        catch
        {
            return null; // e.g. Windows "N" editions without the Media Feature Pack
        }
    }

    public async Task<IPreparedSpeech> PrepareAsync(SpeechInput input, VoiceOption voice, double rate, CancellationToken ct)
    {
        string text = input.Text;
        await _synthLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = WinSpeech.SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voice.EngineVoiceId);
            if (info is not null) _synth.Voice = info;
            _synth.Options.IncludeWordBoundaryMetadata = true;
            _synth.Options.IncludeSentenceBoundaryMetadata = false;
            _synth.Options.SpeakingRate = Math.Clamp(rate, 0.5, 6.0);
            var stream = await _synth.SynthesizeTextToStreamAsync(text).AsTask(ct).ConfigureAwait(false);
            return new Prepared(stream);
        }
        finally
        {
            _synthLock.Release();
        }
    }

    public void Play(IPreparedSpeech speech, SpeechCallbacks callbacks)
    {
        Stop();
        var prepared = (Prepared)speech;
        var source = MediaSource.CreateFromStream(prepared.Stream, prepared.Stream.ContentType);
        var item = new MediaPlaybackItem(source);
        var session = new Session { Speech = prepared, Source = source, Item = item, Callbacks = callbacks };

        item.TimedMetadataTracksChanged += (sender, args) =>
        {
            if (args.CollectionChange == CollectionChange.ItemInserted) Hook(session, (int)args.Index);
            else if (args.CollectionChange == CollectionChange.Reset)
                for (int i = 0; i < sender.TimedMetadataTracks.Count; i++) Hook(session, i);
        };
        for (int i = 0; i < item.TimedMetadataTracks.Count; i++) Hook(session, i);

        _current = session;
        _player.Source = item;
        _player.Play();
    }

    private void Hook(Session session, int index)
    {
        var track = session.Item.TimedMetadataTracks[index];
        if (track.Id.Contains("Sentence", StringComparison.OrdinalIgnoreCase)) return;
        if (!session.Hooked.Add(track)) return;

        track.CueEntered += (t, args) =>
        {
            if (!ReferenceEquals(_current, session)) return;
            if (args.Cue is SpeechCue cue && cue.StartPositionInInput is int start)
                session.Callbacks.OnWord(start);
        };
        session.Item.TimedMetadataTracks.SetPresentationMode((uint)index, TimedMetadataTrackPresentationMode.ApplicationPresented);
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        var session = _current;
        if (session is null) return;
        // Guard against a late "ended" from the previous item arriving just after a new one started.
        try
        {
            var ps = sender.PlaybackSession;
            if (ps.NaturalDuration > TimeSpan.FromSeconds(1) && ps.Position < ps.NaturalDuration - TimeSpan.FromSeconds(1)) return;
        }
        catch { }
        _current = null;
        session.Callbacks.OnFinished();
        session.Dispose();
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var session = _current;
        if (session is null) return;
        _current = null;
        session.Callbacks.OnError(new InvalidOperationException("Audio playback failed: " + args.ErrorMessage, args.ExtendedErrorCode));
        session.Dispose();
    }

    public void Pause()
    {
        if (_current is not null) _player.Pause();
    }

    public void Resume()
    {
        if (_current is not null) _player.Play();
    }

    public void Stop()
    {
        var session = _current;
        _current = null;
        if (session is null) return;
        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch { }
        session.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _player.Dispose();
        _synth.Dispose();
    }
}
