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
    string? ConfigPath)
{
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

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
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

        return new CliOptions(help, selfTest, fire, dryRun, recognizers, checkDevice, listenSeconds, inputWavPath, inputWavFast, outputDir, configPath);
    }
}
