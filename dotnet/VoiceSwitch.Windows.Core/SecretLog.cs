using System.Collections;
using System.Text.RegularExpressions;

namespace VoiceSwitch.Windows.Core;

// When an environment variable is missing, a shell default (`${NAME:-secret}`) or the expanded
// value of a secret-named variable must not reach the log. The variable name is the safe form.
public static class SecretLog
{
    private static readonly Regex Brace = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)((?::?[+-][^}]*)?)\}", RegexOptions.Compiled);
    private static readonly Regex Percent = new(@"%([A-Za-z_][A-Za-z0-9_]*)%", RegexOptions.Compiled);
    private static readonly Regex SecretName = new(@"password|passwd|secret|token|api_key|apikey|credential", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Redact(string text) =>
        Redact(text, Environment.GetEnvironmentVariable, Pairs());

    public static string Redact(string text, Func<string, string?> lookup, IEnumerable<KeyValuePair<string, string>> variables)
    {
        text = Brace.Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            var hasDefault = match.Groups[2].Length > 0;
            return hasDefault || string.IsNullOrEmpty(lookup(name)) ? "$" + name : match.Value;
        });
        text = Percent.Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            return string.IsNullOrEmpty(lookup(name)) ? "$" + name : match.Value;
        });
        foreach (var pair in variables
            .Where(pair => pair.Value.Length >= 8 && SecretName.IsMatch(pair.Key))
            .OrderByDescending(pair => pair.Value.Length)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text = text.Replace(pair.Value, "$" + pair.Key, StringComparison.Ordinal);
        }

        return text;
    }

    private static IEnumerable<KeyValuePair<string, string>> Pairs()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                yield return new KeyValuePair<string, string>(key, value);
            }
        }
    }
}
