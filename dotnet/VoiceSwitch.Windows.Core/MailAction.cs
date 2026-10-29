using System.Net;
using System.Net.Mail;
using System.Text;

namespace VoiceSwitch.Windows.Core;

/// <summary>SMTP account an action mails through. Windows only; the Mac decodes the key and ignores it.</summary>
/// <remarks>The password is read from the environment variable named by PasswordEnv, never from the config file.
/// SmtpClient speaks STARTTLS, so the port is a submission port such as 587, not implicit-TLS 465.</remarks>
public sealed record SmtpSettings(string Host, string User, int Port = 587, string? From = null, string PasswordEnv = "VOICE_SWITCH_SMTP_PASSWORD");

/// <summary>An action whose url is mailto:&lt;to&gt;?subject=&lt;subject&gt; and that has smtp mails the dictation as the body.</summary>
public static class MailAction
{
    public static bool IsMailto(string url) => url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    public static (string To, string Subject) Parse(string url)
    {
        var rest = url["mailto:".Length..];
        var question = rest.IndexOf('?');
        var to = Uri.UnescapeDataString(question < 0 ? rest : rest[..question]);
        var subject = (question < 0 ? "" : rest[(question + 1)..]).Split('&')
            .Select(item => item.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0].Equals("subject", StringComparison.OrdinalIgnoreCase))
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .FirstOrDefault() ?? "";
        return (to, subject);
    }

    public static async Task SendAsync(SmtpSettings smtp, string to, string subject, string body, CancellationToken cancellation)
    {
        var password = Environment.GetEnvironmentVariable(smtp.PasswordEnv);
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException($"environment variable {smtp.PasswordEnv} is not set");
        }
        using var message = new MailMessage(smtp.From ?? smtp.User, to, subject, body)
        {
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8
        };
        using var client = new SmtpClient(smtp.Host, smtp.Port)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(smtp.User, password)
        };
        await client.SendMailAsync(message, cancellation).ConfigureAwait(false);
    }
}
