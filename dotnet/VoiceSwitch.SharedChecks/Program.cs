using VoiceSwitch.Windows.Core;

var failed = 0;
void Check(string name, bool ok)
{
    Console.WriteLine(ok ? "PASS " + name : "FAIL " + name);
    if (!ok) failed++;
}

var lookup = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["TOKEN"] = "abcdefghij",
    ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
};
string? Get(string name) => lookup.TryGetValue(name, out var value) ? value : null;

string Redact(string text) => SecretLog.Redact(text, Get, lookup);

var missingDefault = Redact("curl ${API_TOKEN:-s3cret-value}");
Check("missing env default is not logged",
    missingDefault == "curl $API_TOKEN" && !missingDefault.Contains("s3cret-value", StringComparison.Ordinal));

var missingPercent = Redact("echo %PASSWORD%");
Check("missing percent env is the name",
    missingPercent == "echo $PASSWORD" && !missingPercent.Contains("hunter2", StringComparison.Ordinal));

var setSecret = Redact("bearer abcdefghij");
Check("set secret value is the name", setSecret == "bearer $TOKEN");

var ordinary = Redact("heard: 今日は晴れ");
Check("ordinary log is unchanged", ordinary == "heard: 今日は晴れ");

var path = Redact(@"path %LOCALAPPDATA%\voice-switch");
Check("set non-secret percent stays", path == @"path %LOCALAPPDATA%\voice-switch");

var root = RepoRoot();
var swiftLog = File.ReadAllText(Path.Combine(root, "Sources/voice-switch/Config.swift"));
var windowsLog = File.ReadAllText(Path.Combine(root, "dotnet/VoiceSwitch.Windows/Program.cs"));
Check("Mac log redacts", swiftLog.Contains("SecretLog.redact", StringComparison.Ordinal));
Check("Windows log redacts", windowsLog.Contains("SecretLog.Redact", StringComparison.Ordinal));

var name = "VOICE_SWITCH_TEST_SMTP_" + Guid.NewGuid().ToString("N");
Environment.SetEnvironmentVariable(name, null);
try
{
    MailAction.SendAsync(new SmtpSettings("127.0.0.1", "me@example.com", PasswordEnv: name), "a@b.example", "s", "body", CancellationToken.None)
        .GetAwaiter().GetResult();
    Check("missing smtp env throws", false);
}
catch (InvalidOperationException ex)
{
    Check("missing smtp env names the variable only",
        ex.Message.Contains(name, StringComparison.Ordinal) && !ex.Message.Contains("s3cret-value", StringComparison.Ordinal));
}

Check("prompt url resolves to grok-bot", ActionIcons.Resolve("progress", "grokbot://app/v1/prompt?id=1") == "grok-bot");
Check("grok bot name resolves to grok-bot", ActionIcons.Resolve("Grok Bot", "mailto:a@b.example") == "grok-bot");
Check("other action has no builtin icon", ActionIcons.Resolve("progress", "alter://action/1") is null);
Check("configured icon overrides the table", ActionIcons.Resolve("progress", "alter://action/1", "other-bot") == "other-bot");
Check("unsafe icon stem is ignored", ActionIcons.Resolve("Grok Bot", "mailto:a@b.example", "../x") == "grok-bot");
var macHud = File.ReadAllText(Path.Combine(root, "Sources/voice-switch/MacApp.swift"));
var winHud = File.ReadAllText(Path.Combine(root, "dotnet/VoiceSwitch.Windows/Tray/DictationHud.cs"));
Check("Mac HUD draws a named action icon", macHud.Contains("iconName", StringComparison.Ordinal) && macHud.Contains("hudIcon", StringComparison.Ordinal) && !macHud.Contains("case .grok", StringComparison.Ordinal));
Check("Windows HUD draws a named action icon", winHud.Contains("IconName", StringComparison.Ordinal) && !winHud.Contains("HudGlyph.Grok", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(root, "dotnet/VoiceSwitch.Windows/DictationRuntime.cs")).Contains("HudIcon", StringComparison.Ordinal));
Check("icon file exists", new FileInfo(Path.Combine(root, "Sources/voice-switch/Resources/grok-bot.png")).Length > 32);

var hotkeys = new DictationHotkeys();
hotkeys.RequestAction("grokbot");
Check("action hotkey is a start", hotkeys.Take() == DictationCommand.Start);
Check("action hotkey keeps the action name", hotkeys.TakeAction() == "grokbot");
hotkeys.RequestStart();
Check("manual start has no action", hotkeys.Take() == DictationCommand.Start && hotkeys.TakeAction() is null);
Check("shortcut chord parses", DictationHotkeys.Parse("Control+Shift+F6") == new KeyChord(0x75, KeyMods.Control | KeyMods.Shift));
var macConfig = File.ReadAllText(Path.Combine(root, "Sources/voice-switch/Config.swift"));
var winConfig = File.ReadAllText(Path.Combine(root, "dotnet/VoiceSwitch.Windows.Core/VoiceSwitchConfig.cs"));
Check("Mac action shortcut field", macConfig.Contains("let shortcut: String?", StringComparison.Ordinal) && macConfig.Contains("registerActions", StringComparison.Ordinal) == false);
Check("Mac registers action shortcuts", File.ReadAllText(Path.Combine(root, "Sources/voice-switch/MacWakeUI.swift")).Contains("registerActions", StringComparison.Ordinal)
    && File.ReadAllText(Path.Combine(root, "Sources/voice-switch/MacApp.swift")).Contains("registerActions", StringComparison.Ordinal)
    && File.ReadAllText(Path.Combine(root, "Sources/voice-switch/MacApp.swift")).Contains("action-hotkey", StringComparison.Ordinal));
Check("Windows action shortcut field", winConfig.Contains("string? Shortcut", StringComparison.Ordinal)
    && File.ReadAllText(Path.Combine(root, "dotnet/VoiceSwitch.Windows/DictationRuntime.cs")).Contains("action-hotkey", StringComparison.Ordinal)
    && File.ReadAllText(Path.Combine(root, "dotnet/VoiceSwitch.Windows/Tray/WakeLabelHotkey.cs")).Contains("action hotkey:", StringComparison.Ordinal));

return failed == 0 ? 0 : 1;

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Makefile")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
}
