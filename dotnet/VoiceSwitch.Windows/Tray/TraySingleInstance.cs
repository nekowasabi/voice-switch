using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace VoiceSwitch.Windows.Tray;

public sealed class TraySingleInstance : IDisposable
{
    private readonly FileStream? lockHandle;

    private TraySingleInstance(string key, string pipeName, string lockPath, FileStream? lockHandle)
    {
        Key = key;
        PipeName = pipeName;
        LockPath = lockPath;
        this.lockHandle = lockHandle;
    }

    public string Key { get; }
    public string PipeName { get; }
    public string LockPath { get; }
    public bool IsOwner => lockHandle is not null;

    public static TraySingleInstance Identify(string configPath)
    {
        var canonical = Path.GetFullPath(configPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var sid = CurrentSid();
        var key = Sha256Hex($"{sid}\n{canonical}")[..32];
        var lockPath = Path.Combine(LocalAppData(), "voice-switch", "tray", key + ".lock");
        return new TraySingleInstance(key, "voice-switch-tray-" + key, lockPath, null);
    }

    public static TraySingleInstance Acquire(string configPath)
    {
        using var identity = Identify(configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(identity.LockPath)!);
        FileStream? handle = null;
        try
        {
            handle = new FileStream(identity.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            handle.SetLength(0);
            using var writer = new StreamWriter(handle, Encoding.UTF8, leaveOpen: true);
            writer.WriteLine(Environment.ProcessId);
            writer.Flush();
            handle.Flush();
            handle.Position = 0;
        }
        catch (IOException)
        {
            handle?.Dispose();
            handle = null;
        }

        return new TraySingleInstance(identity.Key, identity.PipeName, identity.LockPath, handle);
    }

    public void Dispose() => lockHandle?.Dispose();

    private static string CurrentSid()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        }

        return Environment.UserName;
    }

    private static string LocalAppData() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } value
            ? value
            : Path.GetTempPath();

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
