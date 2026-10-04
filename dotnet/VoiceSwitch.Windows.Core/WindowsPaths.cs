namespace VoiceSwitch.Windows.Core;

public static class WindowsPaths
{
    public static string DefaultConfigPath()
    {
        var localConfig = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(localConfig))
        {
            return localConfig;
        }

        var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? "%USERPROFILE%";
        return $@"{home}\.config\voice-switch\config.json";
    }

    public static string DefaultLogPath()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? @"%USERPROFILE%\AppData\Local";
        return $@"{local}\voice-switch\voice-switch.log";
    }

    public static string DefaultHandoffPath()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath();
        return Path.Combine(local, "voice-switch", "dictation-handoffs");
    }

    public static string SuperwhisperRecordingsPath(DictationConfig? dictation) =>
        ExpandPath(dictation?.RecordingsDir ?? @"%LOCALAPPDATA%\com.superwhisper.app\recordings");

    public static string ExpandPath(string path)
    {
        var expanded = path;
        foreach (var key in new[] { "LOCALAPPDATA", "APPDATA", "USERPROFILE", "HOME", "TEMP" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
            {
                expanded = expanded.Replace($"%{key}%", value, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (expanded.StartsWith('~'))
        {
            expanded = Path.Combine(
                Environment.GetEnvironmentVariable("USERPROFILE")
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[1..].TrimStart('\\', '/'));
        }

        return expanded;
    }
}
