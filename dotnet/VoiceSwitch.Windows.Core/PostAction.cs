using System.Net.Http.Headers;

namespace VoiceSwitch.Windows.Core;

/// <summary>An action with tokenEnv POSTs the dictation to its https url as multipart form field inputParam (default transcription).</summary>
/// <remarks>The bearer token is read from the environment variable tokenEnv names, never from the config file.</remarks>
public static class PostAction
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static bool IsHttps(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static async Task SendAsync(WakeAction action, string text, CancellationToken cancellation)
    {
        var token = Environment.GetEnvironmentVariable(action.TokenEnv!)
            ?? throw new InvalidOperationException($"environment variable {action.TokenEnv} is not set");
        // Same fields as the Pebble Index webhook in transcription-only mode, so a receiver built for it accepts this.
        // .NET writes name=transcription unquoted, and the Workers formData() parser throws on that (Cloudflare 1101),
        // so each name carries its own quotes.
        using var form = new MultipartFormDataContent
        {
            { new StringContent(text), $"\"{action.InputParam ?? "transcription"}\"" },
            { new StringContent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)), "\"recordedAt\"" },
            { new StringContent("ring"), "\"client\"" }
        };
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
