namespace VoiceSwitch.Windows.Core;

public sealed record CliOptions(
    bool Help,
    bool SelfTest,
    bool Fire,
    bool DryRun,
    bool Recognizers,
    bool CheckDevice,
    int? ListenSeconds,
    Guid? CompleteHandoff,
    string? InputWavPath,
    bool InputWavFast,
    string? OutputDir,
    string? ConfigPath)
{
    public static CliOptions Parse(string[] args)
    {
        var help = false;
        var selfTest = false;
        var fire = false;
        var dryRun = false;
        var recognizers = false;
        var checkDevice = false;
        int? listenSeconds = null;
        Guid? completeHandoff = null;
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
                case "--complete-handoff":
                    if (i + 1 >= args.Length || !Guid.TryParse(args[++i], out var handoffId))
                    {
                        throw new ArgumentException("--complete-handoff requires a handoff id.");
                    }

                    completeHandoff = handoffId;
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

        if (dryRun && completeHandoff is not null)
        {
            throw new ArgumentException("--dry-run cannot be combined with --complete-handoff.");
        }

        if (inputWavPath is not null && (fire || recognizers || checkDevice || completeHandoff is not null))
        {
            throw new ArgumentException("--input-wav cannot be combined with --fire, --recognizers, --check-device, or --complete-handoff.");
        }

        if (inputWavFast && inputWavPath is null)
        {
            throw new ArgumentException("--input-wav-fast requires --input-wav.");
        }

        if (outputDir is not null && inputWavPath is null)
        {
            throw new ArgumentException("--output-dir requires --input-wav.");
        }

        return new CliOptions(help, selfTest, fire, dryRun, recognizers, checkDevice, listenSeconds, completeHandoff, inputWavPath, inputWavFast, outputDir, configPath);
    }
}
