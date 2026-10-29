namespace PdfReader.App.Speech;

public enum VoiceEngineKind { OneCore, Sapi }

/// <summary>A Windows voice the user can pick.</summary>
public sealed record VoiceOption(string Key, string Name, string Language, VoiceEngineKind Kind, string EngineVoiceId)
{
    public string Display => Kind == VoiceEngineKind.Sapi ? $"{Name} ({Language}) · classic" : $"{Name} ({Language})";
    public override string ToString() => Display;
}

/// <summary>Audio that has been synthesized (or is ready to be spoken) but not played yet.</summary>
public interface IPreparedSpeech : IDisposable { }

/// <summary>Callbacks for one spoken chunk. They may arrive on any thread.</summary>
public sealed class SpeechCallbacks
{
    public required Action<int> OnWord { get; init; }          // character index of the word inside the chunk text
    public required Action OnFinished { get; init; }
    public required Action<Exception> OnError { get; init; }
}

public interface ISpeechEngine : IDisposable
{
    IReadOnlyList<VoiceOption> Voices { get; }

    /// <summary>Turns text into speech ahead of time so playback can start (or follow on) without a gap.</summary>
    Task<IPreparedSpeech> PrepareAsync(string text, VoiceOption voice, double rate, CancellationToken ct);

    /// <summary>Starts speaking. Replaces whatever this engine was speaking before.</summary>
    void Play(IPreparedSpeech speech, SpeechCallbacks callbacks);

    void Pause();
    void Resume();
    void Stop();
}
