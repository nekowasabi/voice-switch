namespace VoiceSwitch.Windows.Core;

public sealed record CliOptions(
    bool Help,
    bool SelfTest,
    bool Fire,
    bool DryRun,
    bool Recognizers,
    bool CheckDevice,
    int? ListenSeconds,
    string? InputWavPath,
    bool InputWavFast,
    string? OutputDir,
    string? ConfigPath,
    IReadOnlyList<string> CheckPaths)
{
    public string? EnrollSpeakerOutput { get; init; }
    public IReadOnlyList<string> EnrollSpeakerInputs { get; init; } = [];
    public string? EnrollWordsWord { get; init; }
    public bool EnrollWordsStop { get; init; }
    public IReadOnlyList<string> EnrollWordsInputs { get; init; } = [];
    // Why: one exe serves both the tray app and terminal diagnostics; a bare launch (double-click,
    // shortcut, or only --config) or any tray-only flag means the tray, everything else stays CLI.
    public static bool IsTrayLaunch(string[] args) =>
        args.Length == 0
        || args is ["--config", _]
        || args.Any(arg => arg is "--paused" or "--tray-command" or "--record-only");

    public static CliOptions Parse(string[] args)
    {
        var help = false;
        var selfTest = false;
        var fire = false;
        var dryRun = false;
        var recognizers = false;
        var checkDevice = false;
        int? listenSeconds = null;
        string? inputWavPath = null;
        var inputWavFast = false;
        string? outputDir = null;
        string? configPath = null;
        var checkPaths = new List<string>();
        string? enrollOutput = null;
        var enrollInputs = new List<string>();
        string? enrollWordsWord = null;
        var enrollWordsStop = false;
        var enrollWordsInputs = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--enroll-speaker":
                    if (i + 2 >= args.Length) throw new ArgumentException("--enroll-speaker requires <out.json> <in.wav>...");
                    enrollOutput = args[++i];
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--")) enrollInputs.Add(args[++i]);
                    if (enrollInputs.Count == 0) throw new ArgumentException("--enroll-speaker requires <out.json> <in.wav>...");
                    break;
                case "--enroll-words":
                    while (i + 1 < args.Length && (args[i + 1] == "--stop" || args[i + 1] == "--dry-run"))
                    {
                        if (args[++i] == "--stop") enrollWordsStop = true;
                        else dryRun = true;
                    }

                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("--enroll-words requires <word> <in.wav>... (record 3-5 takes).");
                    }

                    enrollWordsWord = args[++i];
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        enrollWordsInputs.Add(args[++i]);
                    }

                    if (enrollWordsInputs.Count == 0)
                    {
                        throw new ArgumentException("--enroll-words requires <word> <in.wav>... (record 3-5 takes).");
                    }

                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                case "--self-test":
                case "--vad-selftest":
                    selfTest = true;
                    break;
                case "--fire":
                    fire = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--recognizers":
                    recognizers = true;
                    break;
                case "--check-device":
                    checkDevice = true;
                    break;
                case "--listen-seconds":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out var seconds) || seconds < 0)
                    {
                        throw new ArgumentException("--listen-seconds requires a non-negative integer.");
                    }

                    listenSeconds = seconds;
                    break;
                case "--input-wav":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--input-wav requires a path.");
                    }

                    inputWavPath = args[++i];
                    break;
                case "--input-wav-fast":
                    inputWavFast = true;
                    break;
                case "--output-dir":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--output-dir requires a path.");
                    }

                    outputDir = args[++i];
                    break;
                case "--check":
                    // Mac --check a.wav b.wav: every following argument up to the next flag is a file.
                    var first = checkPaths.Count;
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        checkPaths.Add(args[++i]);
                    }

                    if (checkPaths.Count == first)
                    {
                        throw new ArgumentException("--check requires one or more WAV paths.");
                    }

                    break;
                case "--config":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--config requires a path.");
                    }

                    configPath = args[++i];
                    break;
                default:
                    throw new ArgumentException($"unknown argument: {args[i]}");
            }
        }

        if (fire && dryRun)
        {
            throw new ArgumentException("--dry-run cannot be combined with --fire.");
        }

        if (inputWavPath is not null && (fire || recognizers || checkDevice))
        {
            throw new ArgumentException("--input-wav cannot be combined with --fire, --recognizers, or --check-device.");
        }

        if (inputWavFast && inputWavPath is null)
        {
            throw new ArgumentException("--input-wav-fast requires --input-wav.");
        }

        if (outputDir is not null && inputWavPath is null)
        {
            throw new ArgumentException("--output-dir requires --input-wav.");
        }

        if (checkPaths.Count > 0 && (inputWavPath is not null || fire))
        {
            throw new ArgumentException("--check cannot be combined with --input-wav or --fire.");
        }

        if (enrollWordsWord is not null && (checkPaths.Count > 0 || inputWavPath is not null || fire))
        {
            throw new ArgumentException("--enroll-words cannot be combined with --check, --input-wav, or --fire.");
        }

        return new CliOptions(help, selfTest, fire, dryRun, recognizers, checkDevice, listenSeconds, inputWavPath, inputWavFast, outputDir, configPath, checkPaths)
        { EnrollSpeakerOutput = enrollOutput, EnrollSpeakerInputs = enrollInputs, EnrollWordsWord = enrollWordsWord, EnrollWordsStop = enrollWordsStop, EnrollWordsInputs = enrollWordsInputs };
    }
}
