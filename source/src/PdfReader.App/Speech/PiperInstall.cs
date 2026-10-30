using System.IO;
using System.IO.Compression;
using System.Net.Http;
using PdfReader.Core;

namespace PdfReader.App.Speech;

/// <summary>Where Piper and its voices live on this PC, and how they get there.</summary>
public static class PiperInstall
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDF Reader", "piper");

    public static string VoicesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDF Reader", "voices");

    public static string CatalogFile => Path.Combine(VoicesFolder, "catalog.json");

    /// <summary>piper.exe (the zip unpacks into a "piper" folder).</summary>
    public static string? FindExe()
    {
        foreach (var candidate in new[] { Path.Combine(Root, "piper", "piper.exe"), Path.Combine(Root, "piper.exe") })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    public static bool EngineInstalled => FindExe() is not null;

    public static string ModelPath(string key) => Path.Combine(VoicesFolder, key + ".onnx");

    public static bool IsInstalled(string key) =>
        File.Exists(ModelPath(key)) && File.Exists(ModelPath(key) + ".json");

    /// <summary>Installed voices as (key, model path, display name, language).</summary>
    public static List<VoiceOption> InstalledVoices()
    {
        var list = new List<VoiceOption>();
        try
        {
            if (!Directory.Exists(VoicesFolder)) return list;
            foreach (var model in Directory.EnumerateFiles(VoicesFolder, "*.onnx"))
            {
                string config = model + ".json";
                if (!File.Exists(config)) continue;
                string key = Path.GetFileNameWithoutExtension(model);
                var (_, lang, dataset) = SafeConfig(config);
                // key looks like en_US-lessac-medium
                var parts = key.Split('-');
                string name = parts.Length >= 2 ? parts[1] : (dataset.Length > 0 ? dataset : key);
                name = char.ToUpperInvariant(name[0]) + name[1..].Replace('_', ' ');
                string quality = parts.Length >= 3 ? parts[2] : "";
                string language = (lang.Length > 0 ? lang : parts[0]).Replace('_', '-');
                if (quality is "high" or "low" or "x_low") name += $" {quality.Replace('_', '-')}";
                list.Add(new VoiceOption("piper:" + key, name, language, VoiceEngineKind.Piper, model));
            }
        }
        catch { }
        return list.OrderBy(v => v.Language).ThenBy(v => v.Name).ToList();
    }

    private static (int, string, string) SafeConfig(string path)
    {
        try { return PiperCatalog.ReadConfig(File.ReadAllText(path)); }
        catch { return (22050, "", ""); }
    }

    public static int SampleRateOf(string modelPath) => SafeConfig(modelPath + ".json").Item1;

    // ---- downloading -------------------------------------------------------------------------------

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    static PiperInstall()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("PDFReader/1.0");
    }

    /// <summary>The voices catalogue (downloaded once a day, otherwise read from disk).</summary>
    public static async Task<List<PiperVoiceInfo>> LoadCatalogAsync(bool refresh, CancellationToken ct)
    {
        string? json = null;
        bool fresh = File.Exists(CatalogFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CatalogFile) < TimeSpan.FromDays(1);
        if (!refresh && fresh) json = await File.ReadAllTextAsync(CatalogFile, ct);
        if (json is null)
        {
            try
            {
                json = await Http.GetStringAsync(PiperCatalog.CatalogUrl, ct);
                Directory.CreateDirectory(VoicesFolder);
                await File.WriteAllTextAsync(CatalogFile, json, ct);
            }
            catch when (File.Exists(CatalogFile))
            {
                json = await File.ReadAllTextAsync(CatalogFile, ct); // offline: use the last copy
            }
        }
        return PiperCatalog.Parse(json);
    }

    /// <summary>Downloads and unpacks the Piper engine (about 21 MB) if it isn't there yet.</summary>
    public static async Task EnsureEngineAsync(IProgress<(string Step, double Fraction)>? progress, CancellationToken ct)
    {
        if (EngineInstalled) return;
        Directory.CreateDirectory(Root);
        string zip = Path.Combine(Root, "piper_windows_amd64.zip");
        await DownloadAsync(PiperCatalog.EngineUrl, zip, "Downloading the voice engine", progress, ct);
        progress?.Report(("Unpacking the voice engine", 1));
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, Root, overwriteFiles: true), ct);
        File.Delete(zip);
        if (!EngineInstalled) throw new InvalidOperationException("The voice engine download didn't contain piper.exe.");
    }

    /// <summary>Downloads one voice (the engine too, the first time).</summary>
    public static async Task InstallVoiceAsync(PiperVoiceInfo voice, IProgress<(string Step, double Fraction)>? progress, CancellationToken ct)
    {
        await EnsureEngineAsync(progress, ct);
        Directory.CreateDirectory(VoicesFolder);
        string model = ModelPath(voice.Key);
        await DownloadAsync(PiperCatalog.BaseUrl + voice.ConfigFile, model + ".json", "Downloading voice settings", progress, ct);
        await DownloadAsync(PiperCatalog.BaseUrl + voice.ModelFile, model, $"Downloading {voice.DisplayName}", progress, ct);
    }

    public static void RemoveVoice(string key)
    {
        foreach (var f in new[] { ModelPath(key), ModelPath(key) + ".json" })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    private static async Task DownloadAsync(string url, string target, string step, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        string tmp = target + ".part";
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(tmp);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            var lastReport = DateTime.MinValue;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(150))
                {
                    lastReport = DateTime.UtcNow;
                    progress?.Report((step, total is > 0 ? (double)done / total.Value : 0));
                }
            }
        }
        File.Move(tmp, target, overwrite: true);
        progress?.Report((step, 1));
    }
}
