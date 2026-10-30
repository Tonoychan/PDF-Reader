using PdfReader.Core;

namespace PdfReader.App.Speech;

public enum VoiceEngineKind { OneCore, Sapi, Piper }

/// <summary>A Windows voice the user can pick.</summary>
public sealed record VoiceOption(string Key, string Name, string Language, VoiceEngineKind Kind, string EngineVoiceId)
{
    public string Display => Kind switch
    {
        VoiceEngineKind.Sapi => $"{Name} ({Language}) · classic",
        VoiceEngineKind.Piper => $"{Name} ({Language}) · natural",
        _ => $"{Name} ({Language})",
    };

    /// <summary>Natural (Piper) voices highlight a whole sentence; Windows voices highlight each word.</summary>
    public bool HighlightsSentences => Kind == VoiceEngineKind.Piper;

    /// <summary>Heading the voice is listed under in the Voice and speed panel.</summary>
    public string Group => Kind == VoiceEngineKind.Piper ? "NATURAL VOICES · HIGHLIGHT WHOLE SENTENCES" : "WINDOWS VOICES · HIGHLIGHT EACH WORD";

    /// <summary>Second part of the voice's line in the panel ("English (US)", plus "classic" for old Windows voices).</summary>
    public string Detail => Kind == VoiceEngineKind.Sapi ? $"{Language} · classic" : Language;
    public override string ToString() => Display;
}

/// <summary>Audio that has been synthesized (or is ready to be spoken) but not played yet.</summary>
public interface IPreparedSpeech : IDisposable { }

/// <summary>Callbacks for one spoken chunk. They may arrive on any thread.</summary>
public sealed class SpeechCallbacks
{
    public required Action<int> OnWord { get; init; }          // character index of the word inside the spoken text
    public Action<int>? OnSentence { get; init; }               // index into SpeechInput.Sentences (sentence-at-a-time engines)
    public required Action OnFinished { get; init; }
    public required Action<Exception> OnError { get; init; }
}

public interface ISpeechEngine : IDisposable
{
    IReadOnlyList<VoiceOption> Voices { get; }

    /// <summary>Turns text into speech ahead of time so playback can start (or follow on) without a gap.</summary>
    Task<IPreparedSpeech> PrepareAsync(SpeechInput input, VoiceOption voice, double rate, CancellationToken ct);

    /// <summary>Starts speaking. Replaces whatever this engine was speaking before.</summary>
    void Play(IPreparedSpeech speech, SpeechCallbacks callbacks);

    void Pause();
    void Resume();
    void Stop();
}
