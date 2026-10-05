using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

// Mirrors PaneRoute.swift routeDictation. tmux lives in WSL, so every tmux call goes through
// `wsl.exe -e <tmux> -S <socket> ...` with ArgumentList: no shell, no joined command line.
public static class TmuxPaneRouter
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private sealed record TmuxServer(string Binary, string Socket);

    // Why: `wsl.exe -e` skips the login shell. A TMUX_TMPDIR set there (e.g. /run/user/1000) is invisible, and
    // PATH may resolve an older /usr/bin/tmux whose protocol the running server rejects ("server exited
    // unexpectedly"). So take the binary of a running `tmux: server` from /proc and probe both socket roots,
    // as focusbm's WslTmuxService does. The script is fixed text; the dictation never passes through a shell.
    // ponytail: first live server wins; pick by session name if several servers ever run at once.
    private const string FindServerScript =
        "for p in /proc/[0-9]*; do [ \"$(cat \"$p/comm\" 2>/dev/null)\" = \"tmux: server\" ] || continue; " +
        "b=$(readlink \"$p/exe\") || continue; " +
        "for s in /run/user/*/tmux-*/* /tmp/tmux-*/*; do [ -S \"$s\" ] && \"$b\" -S \"$s\" list-sessions >/dev/null 2>&1 " +
        "&& { printf '%s\\n%s' \"$b\" \"$s\"; exit 0; }; done; done; exit 1";

    // Sent when send-keys ran and exited 0. SkippedNoBody when a pane was chosen but had no extractable body
    // (must not fall through to paste). NotRouted when nothing took the dictation.
    public static async Task<RouteDisposition> RouteAsync(string text)
    {
        var server = await FindServerAsync();
        if (server is null)
        {
            Log.Info("tmux: no running server reachable from wsl.exe; nothing sent");
            return RouteDisposition.NotRouted;
        }

        var panes = await ListPanesAsync(server);
        if (panes is null)
        {
            return RouteDisposition.NotRouted;
        }

        var hits = PaneRoute.MatchingPanes(text, panes);
        var (pick, jevField) = await AskJevAsync(text, panes);
        var decision = PaneRoute.Decide(hits, pick);
        if (decision.Pane is { } chosen && !PaneRoute.IsPaneId(chosen))
        {
            decision = new RouteDecision(null, "pane id rejected");
        }

        // Matching and Jev saw the full dictation; only the quoted body is typed. No body, no send and no paste.
        var body = PaneRoute.ExtractSendBody(text);
        decision = PaneRoute.RequireSendBody(decision, body);
        Log.Info(PaneRoute.LogLine(hits.Count, jevField, decision));
        var sent = decision.Pane is { } pane && body is not null && await SendKeysAsync(server, pane, body);
        return PaneRoute.Disposition(decision, sent);
    }

    private static async Task<TmuxServer?> FindServerAsync()
    {
        var psi = Wsl("sh", ["-c", FindServerScript]);
        psi.RedirectStandardOutput = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var lines = stdout.Split('\n');
            return process.ExitCode == 0 && lines.Length == 2 && lines.All(line => line.StartsWith('/'))
                ? new TmuxServer(lines[0], lines[1])
                : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Info($"tmux: server probe failed to start: {ex.Message}");
            return null;
        }
    }

    // Does not start a tmux server. Failure or no server returns null and sends nothing.
    private static async Task<IReadOnlyList<PaneLabel>?> ListPanesAsync(TmuxServer server)
    {
        var psi = Wsl(server.Binary, ["-S", server.Socket, .. PaneRoute.ListPanesArguments]);
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

    private static async Task<bool> SendKeysAsync(TmuxServer server, string pane, string body)
    {
        var psi = Wsl(server.Binary, ["-S", server.Socket, "send-keys", "-t", pane, "-l", "--", body]);
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                Log.Info($"tmux: send-keys exited {process.ExitCode}");
                return false;
            }

            Log.Info($"dictation delivered: pane {pane}");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Info($"tmux: send-keys failed to start: {ex.Message}");
            return false;
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
