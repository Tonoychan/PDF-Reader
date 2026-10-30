using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PdfReader.Core;
using global::Windows.Media.Core;
using global::Windows.Media.Playback;

namespace PdfReader.App.Speech;

/// <summary>
/// A running piper.exe for one voice and speed. Each request is one JSON line on stdin naming the WAV
/// file to write; piper writes the files in order. Loading the model takes a second, so the process
/// stays alive between sentences.
/// </summary>
internal sealed class PiperProcess : IDisposable
{
    private readonly Process _process;
    private readonly object _writeLock = new();
    private readonly StringBuilder _errors = new();
    private int _pending;

    public PiperProcess(string exe, string model, double lengthScale, string outDir)
    {
        Model = model;
        LengthScale = lengthScale;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--json-input");
        psi.ArgumentList.Add("--output_dir"); psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add("--length_scale"); psi.ArgumentList.Add(lengthScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--sentence_silence"); psi.ArgumentList.Add("0.15");

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start the voice engine.");
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_errors)
            {
                _errors.AppendLine(e.Data);
                if (_errors.Length > 4000) _errors.Remove(0, _errors.Length - 4000);
            }
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public string Model { get; }
    public double LengthScale { get; }
    public int Pending => Volatile.Read(ref _pending);
    public bool Alive { get { try { return !_process.HasExited; } catch { return false; } } }

    public string LastErrors { get { lock (_errors) return _errors.ToString().Trim(); } }

    /// <summary>Asks piper to say <paramref name="text"/> into <paramref name="wavFile"/>; completes when the file is written.</summary>
    public async Task<string> SynthesizeAsync(string text, string wavFile, CancellationToken ct)
    {
        string line = JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = text, ["output_file"] = wavFile });
        Interlocked.Increment(ref _pending);
        try
        {
            lock (_writeLock)
            {
                _process.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
            }

            // Piper writes the whole file when the sentence is done; wait until it's there and closed.
            var started = DateTime.UtcNow;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (IsComplete(wavFile)) return wavFile;
                if (!Alive)
                    throw new InvalidOperationException("The voice engine stopped." + (LastErrors.Length > 0 ? "\n\n" + Tail(LastErrors) : ""));
                if (DateTime.UtcNow - started > TimeSpan.FromMinutes(2))
                    throw new TimeoutException("The voice engine took too long to answer.");
                await Task.Delay(40, ct);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    private static string Tail(string s) => s.Length <= 600 ? s : "…" + s[^600..];

    private static bool IsComplete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return fs.Length > 44;
        }
        catch (IOException)
        {
            return false; // still being written
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                try { _process.StandardInput.Close(); } catch { }
                if (!_process.WaitForExit(300)) _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        _process.Dispose();
    }
}

/// <summary>
/// Natural-sounding offline voices (Piper). Speaks a sentence at a time: while one sentence plays,
/// the next ones are already being made. Reports which sentence is playing (no word timings).
/// </summary>
public sealed class PiperSpeechEngine : ISpeechEngine
{
    private readonly MediaPlayer _player;
    private readonly string _outDir;
    private PiperProcess? _piper;
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private CancellationTokenSource? _playCts;
    private TaskCompletionSource<bool>? _ended;
    private volatile bool _paused;
    private int _fileCounter;

    private sealed class Prepared : IPreparedSpeech
    {
        public required SpeechInput Input { get; init; }
        public required List<Task<string>> Files { get; init; }
        public required CancellationTokenSource Cts { get; init; }

        public void Dispose()
        {
            try { Cts.Cancel(); } catch { }
            foreach (var t in Files)
                t.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) TryDelete(x.Result); }, TaskScheduler.Default);
        }
    }

    public PiperSpeechEngine()
    {
        _outDir = Path.Combine(Path.GetTempPath(), "PDFReader-piper", Environment.ProcessId.ToString());
        try
        {
            var parent = Path.GetDirectoryName(_outDir)!;
            if (Directory.Exists(parent))
                foreach (var old in Directory.GetDirectories(parent))
                    try { Directory.Delete(old, true); } catch { } // leftovers from earlier runs
            Directory.CreateDirectory(_outDir);
        }
        catch { }

        _player = new MediaPlayer { AutoPlay = false };
        _player.CommandManager.IsEnabled = false;
        _player.MediaEnded += (_, _) => _ended?.TrySetResult(true);
        _player.MediaFailed += (_, e) => _ended?.TrySetException(new InvalidOperationException("Audio playback failed: " + e.ErrorMessage));
        Refresh();
    }

    public IReadOnlyList<VoiceOption> Voices { get; private set; } = Array.Empty<VoiceOption>();

    public bool EngineInstalled => PiperInstall.EngineInstalled;

    /// <summary>Re-reads the installed voices (after a download or removal).</summary>
    public void Refresh() => Voices = PiperInstall.EngineInstalled ? PiperInstall.InstalledVoices() : Array.Empty<VoiceOption>();

    public async Task<IPreparedSpeech> PrepareAsync(SpeechInput input, VoiceOption voice, double rate, CancellationToken ct)
    {
        var piper = await GetProcessAsync(voice.EngineVoiceId, 1.0 / Math.Clamp(rate, 0.5, 3.0), ct);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var files = new List<Task<string>>(input.Sentences.Count);
        for (int i = 0; i < input.Sentences.Count; i++)
        {
            string text = input.SentenceText(i).Trim();
            string file = Path.Combine(_outDir, $"s{Interlocked.Increment(ref _fileCounter)}.wav");
            files.Add(text.Length == 0 ? Task.FromResult("") : piper.SynthesizeAsync(text, file, cts.Token));
        }
        return new Prepared { Input = input, Files = files, Cts = cts };
    }

    /// <summary>One piper process per voice + speed; a new one replaces the old.</summary>
    private async Task<PiperProcess> GetProcessAsync(string model, double lengthScale, CancellationToken ct)
    {
        await _processLock.WaitAsync(ct);
        try
        {
            if (_piper is { Alive: true } p && p.Model == model && Math.Abs(p.LengthScale - lengthScale) < 0.001) return p;
            _piper?.Dispose();
            string exe = PiperInstall.FindExe() ?? throw new InvalidOperationException("The natural voice engine isn't installed. Open Voices… to download it.");
            if (!File.Exists(model)) throw new FileNotFoundException("This voice's files are missing. Download it again from Voices….", model);
            Directory.CreateDirectory(_outDir);
            _piper = new PiperProcess(exe, model, lengthScale, _outDir);
            return _piper;
        }
        finally
        {
            _processLock.Release();
        }
    }

    public void Play(IPreparedSpeech speech, SpeechCallbacks callbacks)
    {
        StopPlayback();
        var prepared = (Prepared)speech;
        var cts = _playCts = new CancellationTokenSource();
        _paused = false;
        _ = PlayLoopAsync(prepared, callbacks, cts.Token);
    }

    private async Task PlayLoopAsync(Prepared prepared, SpeechCallbacks callbacks, CancellationToken ct)
    {
        try
        {
            for (int i = 0; i < prepared.Files.Count; i++)
            {
                string file;
                try { file = await prepared.Files[i].WaitAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                if (ct.IsCancellationRequested) return;
                while (_paused && !ct.IsCancellationRequested) await Task.Delay(50, ct);
                if (file.Length == 0) continue;

                callbacks.OnSentence?.Invoke(i);

                var ended = _ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _player.Source = MediaSource.CreateFromUri(new Uri(file));
                if (!_paused) _player.Play();
                using (ct.Register(() => ended.TrySetCanceled()))
                {
                    try { await ended.Task; }
                    catch (OperationCanceledException) { return; }
                }
                TryDelete(file);
            }
            if (!ct.IsCancellationRequested) callbacks.OnFinished();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) callbacks.OnError(ex);
        }
    }

    public void Pause()
    {
        _paused = true;
        try { _player.Pause(); } catch { }
    }

    public void Resume()
    {
        _paused = false;
        try { if (_player.Source is not null) _player.Play(); } catch { }
    }

    private void StopPlayback()
    {
        _playCts?.Cancel();
        _playCts = null;
        _paused = false;
        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch { }
    }

    /// <summary>Stops reading (a jump, a stop or a new voice).</summary>
    public void Stop()
    {
        StopPlayback();

        // If piper is busy making sentences nobody will hear, start fresh rather than wait for them.
        if (_piper is { Pending: > 3 } busy)
        {
            _piper = null;
            busy.Dispose();
        }
    }

    private static void TryDelete(string file)
    {
        try { if (file.Length > 0 && File.Exists(file)) File.Delete(file); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _piper?.Dispose();
        _player.Dispose();
        try { Directory.Delete(_outDir, true); } catch { }
    }
}
