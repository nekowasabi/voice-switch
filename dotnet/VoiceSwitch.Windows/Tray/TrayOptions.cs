using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows.Tray;

public sealed record TrayOptions(
    string ConfigPath,
    string? InputWavPath,
    string? RecordOnlyDir,
    bool Paused,
    TrayCommand? IpcCommand)
{
    public TrayInputSource Source => new(InputWavPath, RecordOnlyDir);

    public static TrayOptions Parse(string[] args)
    {
        string? configPath = null;
        string? inputWav = null;
        string? recordOnly = null;
        var paused = false;
        TrayCommand? command = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--config requires a path.");
                    }

                    configPath = args[++i];
                    break;
                case "--input-wav":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--input-wav requires a path.");
                    }

                    inputWav = args[++i];
                    break;
                case "--record-only":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--record-only requires a directory.");
                    }

                    recordOnly = args[++i];
                    break;
                case "--paused":
                    paused = true;
                    break;
                case "--tray-command":
                    if (i + 1 >= args.Length || !TryParseCommand(args[++i], out var parsedCommand))
                    {
                        throw new ArgumentException("--tray-command must be one of: status, start, pause, reload, quit.");
                    }

                    command = parsedCommand;
                    break;
                default:
                    throw new ArgumentException($"unknown argument: {args[i]}");
            }
        }

        if (recordOnly is not null && inputWav is null)
        {
            throw new ArgumentException("--record-only requires --input-wav.");
        }

        if (inputWav is not null && recordOnly is null)
        {
            throw new ArgumentException("--input-wav in tray mode requires --record-only so no external app is launched.");
        }

        var resolvedConfig = Path.GetFullPath(configPath
            ?? Environment.GetEnvironmentVariable("VOICE_SWITCH_CONFIG")
            ?? WindowsPaths.DefaultConfigPath());
        return new TrayOptions(resolvedConfig, inputWav is null ? null : Path.GetFullPath(inputWav), recordOnly is null ? null : Path.GetFullPath(recordOnly), paused, command);
    }

    private static bool TryParseCommand(string value, out TrayCommand command)
    {
        var normalized = value.ToLowerInvariant();
        command = normalized switch
        {
            "status" or "showstatus" => TrayCommand.ShowStatus,
            "start" or "resume" => TrayCommand.Start,
            "pause" => TrayCommand.Pause,
            "reload" => TrayCommand.Reload,
            "quit" => TrayCommand.Quit,
            _ => default
        };
        return normalized is "status" or "showstatus" or "start" or "resume" or "pause" or "reload" or "quit";
    }
}
