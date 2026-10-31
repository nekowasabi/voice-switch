using System.Diagnostics;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

// The only production choice between the Superwhisper and Haas body paths.
public static class DictationHandoffs
{
    public static IDictationHandoff Create(
        VoiceSwitchConfig config,
        string root,
        string recordingsDir,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        Func<string, Task<RouteResult>>? onTranscribed = null,
        Func<WakeAction, string, CancellationToken, Task>? post = null,
        Func<string, nint, bool>? paste = null,
        Action<nint>? restoreFocus = null,
        Func<TimeSpan, Task>? delay = null,
        bool dryRun = false,
        bool wrapLocalHistory = false)
    {
        if ((config.Dictation?.Transcriber ?? "superwhisper") == "haas")
        {
            var haas = config.Dictation!.Haas!;
            return new HaasRunnerHandoff(root, haas.Command!, haas.TimeoutSeconds ?? 120, haas.Vocabulary,
                startProcess, onTranscribed, post, paste);
        }
        var superwhisper = new RegisteredSuperwhisperHandoff(root, recordingsDir, startProcess, dryRun,
            restoreFocus, delay, onTranscribed, config.Dictation?.SuperwhisperMode, post: post);
        return wrapLocalHistory ? LocalHistoryHandoff.Wrap(superwhisper, config, paste) : superwhisper;
    }
}
