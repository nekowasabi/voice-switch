using System.Text.Json;

namespace VoiceSwitch.Windows.Core;

[Flags]
public enum KeyMods
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Win = 8
}

public readonly record struct KeyChord(int Vk, KeyMods Mods);

public enum DictationCommand
{
    Finish,
    Cancel
}

// Mirrors MacApp.swift Hotkeys: Superwhisper's own finish/cancel shortcuts are borrowed only while a dictation is open.
// The hook thread calls OnKey; the runtime thread calls Begin, End and Take.
public sealed class DictationHotkeys
{
    public static readonly KeyChord DefaultFinish = new(0x20, KeyMods.Control);
    public static readonly KeyChord DefaultCancel = new(0x1B, KeyMods.None);
    // A keyUp lost to a secure desktop or a lock screen must not swallow that key forever.
    private const long SwallowUpExpiryMs = 2000;

    private readonly object sync = new();
    private readonly Dictionary<int, long> swallowUp = new();
    private (KeyChord Finish, KeyChord Cancel)? armed;
    private DictationCommand? pending;

    // Superwhisper preferences.json uses KeyboardEvent.code names: "Control+Space", "Escape", "Control+Shift+KeyM".
    public static KeyChord? Parse(string name)
    {
        var parts = name.Split('+');
        var mods = KeyMods.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "control" or "ctrl": mods |= KeyMods.Control; break;
                case "shift": mods |= KeyMods.Shift; break;
                case "alt": mods |= KeyMods.Alt; break;
                case "meta" or "super" or "win": mods |= KeyMods.Win; break;
                default: return null;
            }
        }

        var key = parts[^1];
        int? vk = key switch
        {
            "Space" => 0x20,
            "Escape" => 0x1B,
            "Enter" => 0x0D,
            "Tab" => 0x09,
            "Backspace" => 0x08,
            ['K', 'e', 'y', var letter] when letter is >= 'A' and <= 'Z' => 0x41 + letter - 'A',
            ['D', 'i', 'g', 'i', 't', var digit] when char.IsAsciiDigit(digit) => 0x30 + digit - '0',
            ['F', .. var number] when int.TryParse(number, out var f) && f is >= 1 and <= 24 => 0x70 + f - 1,
            _ => null
        };
        return vk is int code ? new KeyChord(code, mods) : null;
    }

    public static (KeyChord Finish, KeyChord Cancel) Load(string? preferencesJson)
    {
        var finish = DefaultFinish;
        var cancel = DefaultCancel;
        if (preferencesJson is null)
        {
            return (finish, cancel);
        }

        try
        {
            using var document = JsonDocument.Parse(preferencesJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                finish = Read(document.RootElement, "toggleRecordingShortcut") ?? finish;
                cancel = Read(document.RootElement, "cancelRecordingShortcut") ?? cancel;
            }
        }
        catch (JsonException)
        {
        }

        return (finish, cancel);
    }

    public void Begin((KeyChord Finish, KeyChord Cancel) shortcuts)
    {
        lock (sync)
        {
            armed = shortcuts;
            pending = null;
            swallowUp.Clear();
        }
    }

    public void End()
    {
        lock (sync)
        {
            armed = null;
            pending = null;
        }
    }

    // Returns true when the event must not reach other applications.
    public bool OnKey(int vk, KeyMods mods, bool down, long nowMs)
    {
        lock (sync)
        {
            DropStale(nowMs);
            if (swallowUp.ContainsKey(vk))
            {
                if (!down)
                {
                    swallowUp.Remove(vk);
                }

                return true;
            }

            if (!down || armed is not { } shortcuts)
            {
                return false;
            }

            var chord = new KeyChord(vk, mods);
            if (chord == shortcuts.Finish)
            {
                pending = DictationCommand.Finish;
            }
            else if (chord == shortcuts.Cancel)
            {
                pending = DictationCommand.Cancel;
            }
            else
            {
                return false;
            }

            swallowUp[vk] = nowMs;
            return true;
        }
    }

    public DictationCommand? Take()
    {
        lock (sync)
        {
            var command = pending;
            pending = null;
            return command;
        }
    }

    // True once the hook may be removed: disarmed and no swallowed press still owes its keyUp.
    public bool Releasable(long nowMs)
    {
        lock (sync)
        {
            DropStale(nowMs);
            return armed is null && swallowUp.Count == 0;
        }
    }

    private void DropStale(long nowMs)
    {
        if (armed is not null)
        {
            return;
        }

        foreach (var vk in swallowUp.Where(entry => nowMs - entry.Value > SwallowUpExpiryMs).Select(entry => entry.Key).ToArray())
        {
            swallowUp.Remove(vk);
        }
    }

    private static KeyChord? Read(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? Parse(value.GetString()!) : null;
}
