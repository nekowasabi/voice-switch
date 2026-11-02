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
