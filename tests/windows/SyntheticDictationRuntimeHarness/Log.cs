namespace VoiceSwitch.Windows;

public static class Log
{
    public static void Info(string message) =>
        Console.WriteLine($"{DateTimeOffset.Now:O} {message}");

    public static void Fatal(string message) =>
        Console.Error.WriteLine($"{DateTimeOffset.Now:O} fatal: {message}");
}
