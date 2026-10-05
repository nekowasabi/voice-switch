using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

// Mirrors PaneRoute.swift routeDictation. tmux lives in WSL, so every tmux call goes through
// `wsl.exe -e tmux ...` with ArgumentList: no shell, no joined command line.
public static class TmuxPaneRouter
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task RouteAsync(string text)
    {
        var panes = await ListPanesAsync();
        if (panes is null)
        {
            return;
        }

        var hits = PaneRoute.MatchingPanes(text, panes);
        var (pick, jevField) = await AskJevAsync(text, panes);
        var decision = PaneRoute.Decide(hits, pick);
        if (decision.Pane is { } chosen && !PaneRoute.IsPaneId(chosen))
        {
            decision = new RouteDecision(null, "pane id rejected");
        }

        Log.Info(PaneRoute.LogLine(hits.Count, jevField, decision));
        if (decision.Pane is { } pane)
        {
            await SendKeysAsync(pane, text);
        }
    }

    // Does not start a tmux server. Failure or no server returns null and sends nothing.
    private static async Task<IReadOnlyList<PaneLabel>?> ListPanesAsync()
    {
        var psi = Wsl("tmux", PaneRoute.ListPanesArguments);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                var detail = (await stderr).Trim();
                Log.Info($"tmux: list-panes exited {process.ExitCode}{(detail.Length == 0 ? "" : ": " + detail)}; nothing sent");
                return null;
            }

            var panes = PaneRoute.ParsePanes(await stdout);
            if (panes is null)
            {
                Log.Info("tmux: list-panes output was not a four-field catalog; nothing sent");
            }

            return panes;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Info($"tmux: list-panes failed to start: {ex.Message}");
            return null;
        }
    }

    // Key from TYPESAFE_API_KEY, else JEV_API_KEY. No key means no request. The key and the dictation are never logged.
    private static async Task<(JevPick? Pick, string Field)> AskJevAsync(string dictation, IReadOnlyList<PaneLabel> panes)
    {
        var key = new[] { "TYPESAFE_API_KEY", "JEV_API_KEY" }
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));
        if (key is null)
        {
            return (null, "off");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, PaneRoute.JevEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(PaneRoute.JevRequestBody(dictation, panes), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Log.Info($"jev: http {(int)response.StatusCode}");
                return (null, "error");
            }

            var pick = PaneRoute.ParseJevPick(await response.Content.ReadAsStringAsync(), panes.Select(pane => pane.Id));
            if (pick is null)
            {
                Log.Info("jev: answer was not a catalog choice");
                return (null, "error");
            }

            return (pick, PaneRoute.JevField(pick));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Info("jev: request failed or timed out");
            return (null, "error");
        }
    }

    private static async Task SendKeysAsync(string pane, string text)
    {
        var psi = Wsl("tmux", ["send-keys", "-t", pane, "-l", "--", text]);
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                Log.Info($"tmux: send-keys exited {process.ExitCode}");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Info($"tmux: send-keys failed to start: {ex.Message}");
        }
    }

    private static ProcessStartInfo Wsl(string command, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(command);
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        return psi;
    }
}
