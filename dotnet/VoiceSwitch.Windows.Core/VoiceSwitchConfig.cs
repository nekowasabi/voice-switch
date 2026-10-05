using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace VoiceSwitch.Windows.Core;

public sealed record DictationConfig(
    string? RecordingsDir = null,
    int? EndSilenceMs = null,
    double? MaxSeconds = null,
    string[]? ExcludeBundleIDs = null,
    int? StartTimeoutMs = null,
    // Windows twin of excludeBundleIDs: executable names (with or without .exe) whose window in front stops a wake.
    string[]? ExcludeProcessNames = null,
    // Superwhisper mode key or name used for voice-switch dictations; meant to be a mode with auto-paste off.
    string? SuperwhisperMode = null);

public enum NoiseReductionMode
{
    Off,
    ConservativeWiener
}

public sealed record NoiseReductionOptions(
    NoiseReductionMode Mode = NoiseReductionMode.Off,
    double MaxAttenuationDb = 6);

public sealed record VoiceSwitchConfig(
    string[] WakeWords,
    string? Locale,
    string Command,
    double? MaxSeconds = null,
    int? HangoverMs = null,
    int? PrerollMs = null,
    int? MinSpeechMs = null,
    float? VadRatio = null,
    float? VadMinRMS = null,
    string[]? SkipWhileMicInUseBy = null,
    string[]? StopWords = null,
    string? StopCommand = null,
    DictationConfig? Dictation = null,
    NoiseReductionOptions? NoiseReduction = null)
{
    // Parallel to WakeWords by index. The Windows runtime fills it from MS-IME; config files never carry it.
    [JsonIgnore]
    public string?[]? WakeReadings { get; init; }

    public WakeWord[] Wakes() =>
        WakeWords.Select((word, i) => WakeWord.From(word, WakeReadings is { } r && i < r.Length ? r[i] : null)).ToArray();

    public string EffectiveLocale => string.IsNullOrWhiteSpace(Locale) ? "ja-JP" : Locale.Replace('_', '-');

    public RecognitionKey RecognitionKey() =>
        new(EffectiveLocale, Signature(WakeWords), Signature(StopWords ?? []));

    public IEnumerable<string> UnsupportedWarnings()
    {
        if (Dictation?.ExcludeBundleIDs is { Length: > 0 })
        {
            yield return "dictation.excludeBundleIDs is macOS bundle-id based and ignored on Windows; list executable names in dictation.excludeProcessNames instead.";
        }

        if ((StopWords ?? []).Length > 0 && string.IsNullOrWhiteSpace(StopCommand))
        {
            yield return "stopCommand is empty; Windows command-mode stop words are disabled. Configure a custom idempotent stop command only if it is safe to run while idle.";
        }

        if (!string.IsNullOrWhiteSpace(StopCommand)
            && string.Equals(StopCommand.Trim(), PlatformDefaults.SuperwhisperToggle, StringComparison.OrdinalIgnoreCase))
        {
            yield return "stopCommand uses the Superwhisper record toggle from older Windows samples; Windows suppresses it because it can start recording while idle. Use a custom idempotent stop command instead.";
        }
    }

    private static string Signature(IEnumerable<string> words) =>
        string.Join('\u001f', words.Select(TextMatching.Normalize).Order());
}

public sealed record WakeWord(string Text, string Reading)
{
    // Without a computed reading the word itself stands in, so a kana wake word still compares with SAPI's lexical forms.
    public static WakeWord From(string word, string? reading = null) =>
        new(TextMatching.Normalize(word), TextMatching.NormalizeReading(string.IsNullOrEmpty(reading) ? word : reading));
}

public sealed record RecognitionKey(string Locale, string WakeWords, string StopWords);

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static VoiceSwitchConfig Load(string path)
    {
        using var stream = File.OpenRead(path);
        VoiceSwitchConfig config;
        try
        {
            config = JsonSerializer.Deserialize<VoiceSwitchConfig>(stream, Options)
                ?? throw new InvalidDataException($"Config is empty: {path}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Config JSON is invalid: {ex.Message}", ex);
        }

        Validate(config);
        return config;
    }

    public static string ToJsonArray(IEnumerable<string> values) =>
        JsonSerializer.Serialize(values.ToArray(), Options);

    private static void Validate(VoiceSwitchConfig config)
    {
        if (config.WakeWords is null || config.WakeWords.Length == 0)
        {
            throw new InvalidDataException("wakeWords must contain at least one word.");
        }

        for (var i = 0; i < config.WakeWords.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(config.WakeWords[i]) || TextMatching.Normalize(config.WakeWords[i]).Length == 0)
            {
                throw new InvalidDataException($"wakeWords[{i}] must contain at least one word character.");
            }
        }

        if (config.StopWords is not null)
        {
            for (var i = 0; i < config.StopWords.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(config.StopWords[i]) || TextMatching.Normalize(config.StopWords[i]).Length == 0)
                {
                    throw new InvalidDataException($"stopWords[{i}] must contain at least one word character.");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(config.Command))
        {
            throw new InvalidDataException("command must not be empty.");
        }

        if (config.NoiseReduction is { MaxAttenuationDb: < 0 or > 6 })
        {
            throw new InvalidDataException("noiseReduction.maxAttenuationDb must be between 0 and 6.");
        }

        if (!string.IsNullOrWhiteSpace(config.Locale))
        {
            var known = CultureInfo.GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures)
                .Any(culture => string.Equals(culture.Name, config.EffectiveLocale, StringComparison.OrdinalIgnoreCase));
            if (!known)
            {
                throw new InvalidDataException($"locale is not valid: {config.Locale}");
            }
        }
    }
}
