using System.Media;
using Microsoft.Win32;

namespace VoiceSwitch.Windows.Tray;

// Per-user switches that config.json does not own, the Windows twin of the Mac UserDefaults keys.
public static class TraySettings
{
    private const string Key = @"Software\voice-switch";
    private const string ConfirmationSoundValue = "ConfirmationSound";
    private const string MicDeviceValue = "MicDevice";

    // The input device chosen in the マイク menu, by WinMM name; null follows the system default (Mac deviceUID).
    public static string? MicDevice
    {
        get => OperatingSystem.IsWindows() ? Registry.GetValue($@"HKEY_CURRENT_USER\{Key}", MicDeviceValue, null) as string : null;
        set
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (value is null)
            {
                key.DeleteValue(MicDeviceValue, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(MicDeviceValue, value, RegistryValueKind.String);
            }
        }
    }

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
    // Wake is one A5; stop falls A5 -> E5, so the two read differently without looking.
    public static bool PlayWakeSound() => Play(WakeChime);

    public static bool PlayStopSound() => Play(StopChime);

    private static bool Play(Lazy<SoundPlayer> chime)
    {
        if (!ConfirmationSound)
        {
            return false;
        }

        chime.Value.Play();
        return true;
    }

    private static readonly Lazy<SoundPlayer> WakeChime = new(() => Synthesize(880.0));

    private static readonly Lazy<SoundPlayer> StopChime = new(() => Synthesize(880.0, 659.25));

    // Soft "pop" notes synthesized in memory: reads as "heard you" without the system error-ish beeps, and needs no file on disk.
    private static SoundPlayer Synthesize(params double[] notes)
    {
        const int rate = 16000;
        const int noteSamples = rate * 120 / 1000;
        var pcm = new short[noteSamples * notes.Length];
        for (var i = 0; i < pcm.Length; i++)
        {
            var n = i / noteSamples;
            var t = i % noteSamples;
            var env = Math.Min(1.0, t / 80.0) * Math.Pow(1.0 - (double)t / noteSamples, 3);
            pcm[i] = (short)(Math.Sin(2 * Math.PI * notes[n] * i / rate) * env * 9000);
        }

        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8.ToArray()); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8.ToArray());
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(pcm.Length * 2);
            foreach (var x in pcm) w.Write(x);
        }

        ms.Position = 0;
        return new SoundPlayer(ms);
    }
}
