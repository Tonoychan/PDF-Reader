using Sapi = System.Speech.Synthesis;

namespace PdfReader.App.Speech;

/// <summary>
/// The classic desktop (SAPI 5) voices. Used as a fallback, and for third-party voices
/// that only install as SAPI voices. It speaks directly and reports progress per word.
/// </summary>
public sealed class SapiSpeechEngine : ISpeechEngine
{
    private readonly Sapi.SpeechSynthesizer _synth = new();
    private Sapi.Prompt? _prompt;
    private SpeechCallbacks? _callbacks;

    private sealed class Prepared : IPreparedSpeech
    {
        public required string Text { get; init; }
        public required VoiceOption Voice { get; init; }
        public required double Rate { get; init; }
        public void Dispose() { }
    }

    private SapiSpeechEngine()
    {
        _synth.SetOutputToDefaultAudioDevice();
        _synth.SpeakProgress += OnProgress;
        _synth.SpeakCompleted += OnCompleted;
        Voices = _synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => new VoiceOption("sapi:" + v.VoiceInfo.Name, v.VoiceInfo.Name, v.VoiceInfo.Culture.Name, VoiceEngineKind.Sapi, v.VoiceInfo.Name))
            .ToList();
    }

    public IReadOnlyList<VoiceOption> Voices { get; }

    public static SapiSpeechEngine? TryCreate()
    {
        try
        {
            var engine = new SapiSpeechEngine();
            return engine.Voices.Count > 0 ? engine : null;
        }
        catch
        {
            return null;
        }
    }

    public Task<IPreparedSpeech> PrepareAsync(string text, VoiceOption voice, double rate, CancellationToken ct) =>
        Task.FromResult<IPreparedSpeech>(new Prepared { Text = text, Voice = voice, Rate = rate });

    public void Play(IPreparedSpeech speech, SpeechCallbacks callbacks)
    {
        Stop();
        var p = (Prepared)speech;
        try { _synth.SelectVoice(p.Voice.EngineVoiceId); } catch { }
        _synth.Rate = ToSapiRate(p.Rate);
        _callbacks = callbacks;
        _prompt = _synth.SpeakAsync(p.Text);
    }

    /// <summary>SAPI rate is -10..10 where ±10 ≈ 3× faster/slower.</summary>
    private static int ToSapiRate(double rate) =>
        Math.Clamp((int)Math.Round(10 * Math.Log(Math.Max(rate, 0.1)) / Math.Log(3)), -10, 10);

    private void OnProgress(object? sender, Sapi.SpeakProgressEventArgs e)
    {
        if (_prompt is not null && ReferenceEquals(e.Prompt, _prompt))
            _callbacks?.OnWord(e.CharacterPosition);
    }

    private void OnCompleted(object? sender, Sapi.SpeakCompletedEventArgs e)
    {
        if (_prompt is null || !ReferenceEquals(e.Prompt, _prompt)) return;
        var cb = _callbacks;
        _prompt = null;
        _callbacks = null;
        if (e.Error is not null) cb?.OnError(e.Error);
        else if (!e.Cancelled) cb?.OnFinished();
    }

    public void Pause()
    {
        if (_prompt is not null && _synth.State == Sapi.SynthesizerState.Speaking) _synth.Pause();
    }

    public void Resume()
    {
        if (_synth.State == Sapi.SynthesizerState.Paused) _synth.Resume();
    }

    public void Stop()
    {
        _prompt = null;
        _callbacks = null;
        try
        {
            if (_synth.State == Sapi.SynthesizerState.Paused) _synth.Resume(); // cancelling while paused can hang
            _synth.SpeakAsyncCancelAll();
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        _synth.Dispose();
    }
}
