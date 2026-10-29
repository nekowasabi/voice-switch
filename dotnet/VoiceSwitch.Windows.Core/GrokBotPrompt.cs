using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceSwitch.Windows.Core;

/// <summary>Grok Bot prompt-link limits (documented, not live-tested; staff forum 2026-10-16).</summary>
public static class GrokBotPrompt
{
    public static bool IsPrompt(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, "grokbot", StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, "app", StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.AbsolutePath, "/v1/prompt", StringComparison.Ordinal);
    }

    public static (string Fitted, bool Truncated) Fit(string text)
    {
        var collapsed = Regex.Replace(text, "[\r\n\t]+", " ").Trim();
        var fitted = new StringBuilder();
        var encodedLength = 0;
        foreach (var rune in collapsed.EnumerateRunes())
        {
            var chunk = EncodeRune(rune);
            if (encodedLength + chunk.Length > 1000)
            {
                return (fitted.ToString(), true);
            }

            fitted.Append(rune.ToString());
            encodedLength += chunk.Length;
        }

        return (fitted.ToString(), false);
    }

    private static string EncodeRune(Rune rune)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(rune.ToString()))
        {
            if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
