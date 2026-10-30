using System.Text.Json;

namespace PdfReader.Core;

/// <summary>A downloadable Piper voice from the official catalogue (voices.json).</summary>
public sealed record PiperVoiceInfo(
    string Key,              // e.g. en_US-lessac-medium
    string Name,             // lessac
    string LanguageCode,     // en_US
    string LanguageName,     // English (United States)
    string Quality,          // x_low, low, medium, high
    int Speakers,
    string ModelFile,        // relative path of the .onnx on the server
    string ConfigFile,       // relative path of the .onnx.json
    long SizeBytes)
{
    public string DisplayName => char.ToUpperInvariant(Name[0]) + Name[1..].Replace('_', ' ');
    public string SizeLabel => SizeBytes <= 0 ? "" : $"{SizeBytes / 1_000_000.0:0} MB";
    public bool IsEnglish => LanguageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads the Piper voices catalogue and knows where the files live.</summary>
public static class PiperCatalog
{
    public const string BaseUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/";
    public const string CatalogUrl = BaseUrl + "voices.json";
    public const string EngineUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";

    /// <summary>Good-sounding English voices shown first.</summary>
    public static readonly string[] Recommended =
    {
        "en_US-lessac-medium", "en_US-amy-medium", "en_US-ryan-high", "en_GB-alan-medium", "en_GB-jenny_dioco-medium", "en_US-hfc_female-medium",
    };

    public static List<PiperVoiceInfo> Parse(string json)
    {
        var list = new List<PiperVoiceInfo>();
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            try
            {
                var v = prop.Value;
                string key = v.TryGetProperty("key", out var k) ? k.GetString() ?? prop.Name : prop.Name;
                string name = v.GetProperty("name").GetString() ?? key;
                var lang = v.GetProperty("language");
                string code = lang.GetProperty("code").GetString() ?? "";
                string langName = lang.TryGetProperty("name_english", out var ne) ? ne.GetString() ?? code : code;
                if (lang.TryGetProperty("country_english", out var ce) && ce.GetString() is { Length: > 0 } country)
                    langName += $" ({country})";
                string quality = v.TryGetProperty("quality", out var q) ? q.GetString() ?? "" : "";
                int speakers = v.TryGetProperty("num_speakers", out var ns) && ns.TryGetInt32(out int n) ? n : 1;

                string? model = null, config = null;
                long size = 0;
                foreach (var f in v.GetProperty("files").EnumerateObject())
                {
                    if (f.Name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                    {
                        model = f.Name;
                        if (f.Value.TryGetProperty("size_bytes", out var sb) && sb.TryGetInt64(out long s)) size = s;
                    }
                    else if (f.Name.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase)) config = f.Name;
                }
                if (model is null || config is null) continue;
                list.Add(new PiperVoiceInfo(key, name, code, langName, quality, speakers, model, config, size));
            }
            catch
            {
                // skip malformed entries
            }
        }
        return list
            .OrderBy(v => Array.IndexOf(Recommended, v.Key) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(v => v.LanguageName)
            .ThenBy(v => v.Name)
            .ThenBy(v => QualityRank(v.Quality))
            .ToList();
    }

    public static int QualityRank(string q) => q switch { "high" => 0, "medium" => 1, "low" => 2, "x_low" => 3, _ => 4 };

    /// <summary>Sample rate and language from a voice's .onnx.json config.</summary>
    public static (int SampleRate, string Language, string Name) ReadConfig(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int rate = root.TryGetProperty("audio", out var a) && a.TryGetProperty("sample_rate", out var sr) && sr.TryGetInt32(out int r) ? r : 22050;
        string lang = root.TryGetProperty("language", out var l) && l.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
        string name = root.TryGetProperty("dataset", out var d) ? d.GetString() ?? "" : "";
        return (rate, lang, name);
    }
}
