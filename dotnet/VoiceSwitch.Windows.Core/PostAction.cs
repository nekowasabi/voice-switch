using System.Globalization;
using System.Net.Http.Headers;

namespace VoiceSwitch.Windows.Core;

/// <summary>An action with tokenEnv POSTs the dictation to its https url as multipart form fields rendered from its fields table.</summary>
/// <remarks>The bearer token is read from the environment variable tokenEnv names, never from the config file.</remarks>
public static class PostAction
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    // The Pebble Index webhook in transcription-only mode, so a receiver built for it accepts a config without fields.
    public static readonly IReadOnlyDictionary<string, string> DefaultFields = new Dictionary<string, string>
    {
        ["transcription"] = "{text}",
        ["recordedAt"] = "{recordedAtMs}",
        ["client"] = "ring",
    };

    public static bool IsHttps(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Renders the action's fields (or the default table) to name/value pairs sorted by name.</summary>
    /// <remarks>{text} is replaced in one pass, so a dictation containing "{recordedAtMs}" stays literal.</remarks>
    public static IReadOnlyList<(string Name, string Value)> Fields(WakeAction action, string text, long recordedAtMs)
    {
        var ms = recordedAtMs.ToString(CultureInfo.InvariantCulture);
        return (action.Fields ?? DefaultFields)
            .OrderBy(field => field.Key, StringComparer.Ordinal)
            .Select(field => (Name: field.Key, Value: string.Join(text, field.Value.Split("{text}").Select(part => part.Replace("{recordedAtMs}", ms)))))
            .ToList();
    }

    public static async Task SendAsync(WakeAction action, string text, CancellationToken cancellation)
    {
        var token = Environment.GetEnvironmentVariable(action.TokenEnv!)
            ?? throw new InvalidOperationException($"environment variable {action.TokenEnv} is not set");
        using var form = new MultipartFormDataContent();
        foreach (var (name, value) in Fields(action, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
        {
            // .NET writes name=x unquoted, and the Workers formData() parser throws on that (Cloudflare 1101),
            // so each name carries its own quotes.
            form.Add(new StringContent(value), $"\"{name}\"");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, action.Url) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Client.SendAsync(request, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // The status alone cannot tell a bad field from a receiver fault; the head of the reply can.
            var reply = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {reply[..Math.Min(reply.Length, 300)]}");
        }
    }
}
