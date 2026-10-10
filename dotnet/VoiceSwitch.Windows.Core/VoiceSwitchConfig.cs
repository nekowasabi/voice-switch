using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

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

/// <summary>Load-time guardrails for timing fields that are easy to mistype 10x. No clamp — warn only.</summary>
public static class DictationTimingGuard
{
    // example.windows.json uses 2400; values above 10s are almost always a typo (e.g. 24000).
    public static bool ShouldWarnHighEndSilence(int? ms) => ms is > 10000;

    // example.windows.json uses 3000; values above 15s are almost always a typo (e.g. 30000).
    public static bool ShouldWarnHighStartTimeout(int? ms) => ms is > 15000;
}

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
    // Mac earlyWakeMs: recognize the still-open utterance before the hangover closes it. Null keeps it off.
    int? EarlyWakeMs = null,
    float? VadRatio = null,
    float? VadMinRMS = null,
    string[]? SkipWhileMicInUseBy = null,
    string[]? StopWords = null,
    string? StopCommand = null,
    DictationConfig? Dictation = null,
    // Parsed for cross-platform config compatibility; Windows does not run macrowhisper (macOS-only CLI hooks).
    System.Text.Json.JsonElement? Macrowhisper = null,
    NoiseReductionOptions? NoiseReduction = null,
    // Twin of Mac `macOS`: Windows-only settings.
    WindowsActions? Windows = null)
{
    // Parallel to WakeWords by index. The Windows runtime fills it from MS-IME; config files never carry it.
    [JsonIgnore]
    public string?[]? WakeReadings { get; init; }

    public WakeWord[] Wakes() => WakeWord.All(WakeWords, WakeReadings);

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

    public static WakeWord[] All(string[] words, string?[]? readings) =>
        words.Select((word, i) => From(word, readings is { } r && i < r.Length ? r[i] : null)).ToArray();
}

public sealed record WindowsActions(WakeAction[]? Actions = null);

/// <summary>A wake phrase whose dictation opens its own URL with the text as `input=` instead of being pasted or routed.</summary>
public sealed record WakeAction(string Name, string[] WakeWords, string Url, string? SuperwhisperMode = null)
{
    [JsonIgnore]
    public string?[]? WakeReadings { get; init; }

    public WakeWord[] Wakes() => WakeWord.All(WakeWords, WakeReadings);

    // Mac WakeAction.openArguments: every `input` item is replaced by one percent-encoded item; other items stay as written.
    public string InputUrl(string input)
    {
        var hash = Url.IndexOf('#');
        var head = hash < 0 ? Url : Url[..hash];
        var fragment = hash < 0 ? "" : Url[hash..];
        var question = head.IndexOf('?');
        var query = question < 0 ? "" : head[(question + 1)..];
        var items = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(item => item.Split('=', 2)[0] != "input")
            .Append("input=" + EncodeUnreserved(input));
        return (question < 0 ? head : head[..question]) + "?" + string.Join('&', items) + fragment;
    }

    private static string EncodeUnreserved(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
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

        var actions = config.Windows?.Actions ?? [];
        for (var i = 0; i < actions.Length; i++)
        {
            var field = $"windows.actions[{i}]";
            if (actions[i] is not { } action || string.IsNullOrWhiteSpace(action.Name))
            {
                throw new InvalidDataException($"{field}.name must not be empty.");
            }

            if (action.WakeWords is not { Length: > 0 } || action.WakeWords.Any(word => string.IsNullOrWhiteSpace(word) || TextMatching.Normalize(word).Length == 0))
            {
                throw new InvalidDataException($"{field}.wakeWords must contain at least one word, each with a word character.");
            }

            if (!IsActionUrl(action.Url))
            {
                throw new InvalidDataException($"{field}.url must be an absolute application URL (for example superwhisper://record); encode spaces as %20, use valid percent escapes, and do not use file/data/javascript URLs.");
            }
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

    // Mac ActionURL. The explicit RFC 3986 character set stands in for Swift URLComponents' strict parse, which .NET Uri
    // lacks; it also keeps the URL free of anything a command line would need to quote.
    private static bool IsActionUrl(string? raw)
    {
        const string allowed = "-._~:/?#[]@!$&'()*+,;=%";
        if (string.IsNullOrEmpty(raw) || !raw.All(c => char.IsAsciiLetterOrDigit(c) || allowed.Contains(c)))
        {
            return false;
        }

        var colon = raw.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        var scheme = raw[..colon];
        var destination = raw[(colon + 1)..];
        if (!Regex.IsMatch(scheme, "^[A-Za-z][A-Za-z0-9+.-]*$")
            || scheme.ToLowerInvariant() is "file" or "data" or "javascript"
            || destination.Length == 0
            || destination == "//")
        {
            return false;
        }

        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '%' && !(i + 2 < raw.Length && char.IsAsciiHexDigit(raw[i + 1]) && char.IsAsciiHexDigit(raw[i + 2])))
            {
                return false;
            }
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out _);
    }
}
