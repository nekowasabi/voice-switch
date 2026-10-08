using Microsoft.Win32;

namespace VoiceSwitch.Windows.Tray;

// Mac SMAppService.mainApp: a per-user Run entry that starts the tray at sign-in.
public sealed class LoginItem(string valueName = "voice-switch")
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string CommandFor(string exePath, string configPath) => $"\"{exePath}\" --config \"{configPath}\"";

    // On only when the entry starts this exe with this config; an entry left by an older copy reads as off.
    public bool IsEnabled(string command)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(valueName) as string, command, StringComparison.OrdinalIgnoreCase);
    }

    public void Set(string command, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(valueName, command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
