using System.Media;
using Microsoft.Win32;

namespace VoiceSwitch.Windows.Tray;

// Per-user switches that config.json does not own, the Windows twin of the Mac UserDefaults keys.
public static class TraySettings
{
    private const string Key = @"Software\voice-switch";
    private const string ConfirmationSoundValue = "ConfirmationSound";

    public static bool ConfirmationSound
    {
        get => OperatingSystem.IsWindows() && Registry.GetValue($@"HKEY_CURRENT_USER\{Key}", ConfirmationSoundValue, 0) is int value && value != 0;
        set
        {
            if (OperatingSystem.IsWindows())
            {
                Registry.SetValue($@"HKEY_CURRENT_USER\{Key}", ConfirmationSoundValue, value ? 1 : 0, RegistryValueKind.DWord);
            }
        }
    }

    // Any thread. True when a sound was played, so the runtime can ignore the microphone while it rings.
    public static bool PlayWakeSound()
    {
        if (!ConfirmationSound)
        {
            return false;
        }

        SystemSounds.Asterisk.Play();
        return true;
    }
}
