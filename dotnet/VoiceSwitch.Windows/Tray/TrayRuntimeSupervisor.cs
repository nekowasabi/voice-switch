using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows.Tray;

public sealed class TrayRuntimeSupervisor : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string configPath;
    private readonly TrayInputSource source;
    private readonly ITrayRuntimeFactory factory;
    private readonly string? instanceKey;
    private VoiceSwitchConfig? config;
    private ITrayRuntimeRun? run;
    private CancellationTokenSource? runCancellation;
    private TraySnapshot snapshot;
    private long generation;
    private bool quitRequested;
    private Task? completionObserver;
    private string? loggedStop;
    private StatusView published = null!;
    // Mac meetBrowsers. ponytail: any capture by these counts as a call, not only Meet.
    private static readonly string[] MeetBrowsers = ["chrome", "msedge"];
    private readonly Func<IReadOnlyCollection<string>, string?> micInUseBy;
    // Model: .claude/skills/verify-meet-pause-windows (MeetPauseWindows.tla). Written only under gate.
    private volatile bool meetHeld;
    private volatile bool meetOverride;
    private bool userPaused;

    public TrayRuntimeSupervisor(
        string configPath,
        TrayInputSource source,
        ITrayRuntimeFactory factory,
        string? instanceKey = null,
        bool pauseDuringMeet = false,
        bool userPaused = false,
        Func<IReadOnlyCollection<string>, string?>? micInUseBy = null)
    {
        this.configPath = Path.GetFullPath(configPath);
        this.source = source;
        this.factory = factory;
        this.instanceKey = instanceKey;
        PauseDuringMeet = pauseDuringMeet;
        this.userPaused = userPaused;
        this.micInUseBy = micInUseBy ?? MicInUse.By;
        snapshot = NewSnapshot(TrayState.Paused, null, null);
        published = new StatusView(snapshot, null);
    }

    public event Action<TraySnapshot>? SnapshotChanged;
    public bool PauseDuringMeet { get; }
    // FollowMeet's cue to queue a Resume once the call is gone; the Resume itself decides under the gate.
    public bool MeetResumeOwed => meetHeld || meetOverride;
    public string? MeetBrowser() => micInUseBy(MeetBrowsers);
    public TraySnapshot Snapshot
    {
        get
        {
            while (true)
            {
                var view = Volatile.Read(ref published);
                var child = view.Run?.OwnedChild;
                if (!ReferenceEquals(view, Volatile.Read(ref published)))
                {
                    continue;
                }

                return view.State with
                {
                    OwnedChildProcessId = child?.ProcessId,
                    OwnedChildProcessStartTimeUtc = child?.StartTimeUtc
                };
            }
        }
    }

    // user: 開始, the pause hotkey, IPC. The launch start and the device retry are not, and never override a hold or a user pause.
    public async Task StartAsync(CancellationToken cancellation, bool user = true)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (user)
            {
                userPaused = false;
                meetHeld = false;
                // A start during a call wins until the call ends.
                meetOverride = PauseDuringMeet && MeetBrowser() is not null;
            }
            else if (meetHeld || userPaused)
            {
                return;
            }

            await StartRunAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    // Under gate.
    private async Task StartRunAsync(CancellationToken cancellation)
    {
        try
        {
            if (snapshot.State is TrayState.Starting or TrayState.Listening || quitRequested)
            {
                return;
            }

            if (run is not null)
            {
                SetSnapshot(TrayState.Error, "runtime is still stopping; wait for cleanup to finish before starting again.", null);
                return;
            }

            SetSnapshot(TrayState.Starting, null, null);
            // Read again: the dictation runtime reloads the file on its own, so the copy cached at the last start can be stale.
            VoiceSwitchConfig nextConfig;
            try
            {
                nextConfig = ConfigLoader.Load(configPath);
            }
            catch (Exception ex) when (config is not null)
            {
                Log.Info($"config reload failed, keeping previous: {ex.Message}");
                nextConfig = config;
            }

            ValidateTrayConfig(nextConfig, source);

            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var started = await factory.StartAsync(nextConfig, source, runCancellation.Token).ConfigureAwait(false);
            config = nextConfig;
            run = started;
            var startedGeneration = ++generation;
            SetSnapshot(TrayState.Listening, null, null);
            completionObserver = ObserveCompletionAsync(started, startedGeneration);
        }
        catch (Exception ex)
        {
            await StopCurrentRunAsync(CancellationToken.None).ConfigureAwait(false);
            SetSnapshot(TrayState.Error, Sanitize(ex), null);
        }
    }

    // User only: the pause outlives a call and blocks the device retry.
    public async Task PauseAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            userPaused = true;
            meetHeld = false;
            await PauseRunAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    // FollowMeet sees a call. Holds from any state, Error and Finished too, or the device retry would listen through the call.
    public async Task HoldForMeetAsync(string browser, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!PauseDuringMeet || meetOverride || userPaused || quitRequested)
            {
                return;
            }

            if (!meetHeld)
            {
                Log.Info($"tray: meet hold ({browser})");
            }

            // Before the stop: a stop that times out still leaves the device retry refused.
            meetHeld = true;
            await PauseRunAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    // FollowMeet saw the call end. Read again here: a Resume queued at the end of one call can run during the next.
    public async Task ResumeAfterMeetAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!PauseDuringMeet || quitRequested || MeetBrowser() is not null)
            {
                return;
            }

            meetOverride = false;
            if (!meetHeld)
            {
                return;
            }

            meetHeld = false;
            Log.Info("tray: meet resume");
            await StartRunAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    // Under gate.
    private async Task PauseRunAsync(CancellationToken cancellation)
    {
        try
        {
            if (snapshot.State is TrayState.Paused or TrayState.Stopped or TrayState.Quitting)
            {
                return;
            }

            SetSnapshot(TrayState.Pausing, snapshot.LastError, null);
            await StopCurrentRunAsync(cancellation).ConfigureAwait(false);
            SetSnapshot(TrayState.Paused, snapshot.LastError, 0);
        }
        catch (Exception ex)
        {
            SetSnapshot(TrayState.Error, Sanitize(ex), null);
        }
    }

    public async Task ReloadAsync(CancellationToken cancellation)
    {
        VoiceSwitchConfig nextConfig;
        try
        {
            nextConfig = ConfigLoader.Load(configPath);
            ValidateTrayConfig(nextConfig, source);
        }
        catch (Exception ex)
        {
            await UpdateErrorWithoutTransitionAsync(Sanitize(ex), cancellation).ConfigureAwait(false);
            return;
        }

        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (quitRequested)
            {
                return;
            }

            var wasRunning = snapshot.State is TrayState.Listening or TrayState.Starting;
            SetSnapshot(TrayState.Reloading, null, null);
            if (wasRunning)
            {
                await StopCurrentRunAsync(cancellation).ConfigureAwait(false);
            }

            config = nextConfig;
            if (!wasRunning)
            {
                SetSnapshot(TrayState.Paused, null, 0);
                return;
            }

            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var started = await factory.StartAsync(nextConfig, source, runCancellation.Token).ConfigureAwait(false);
            run = started;
            var startedGeneration = ++generation;
            SetSnapshot(TrayState.Listening, null, null);
            completionObserver = ObserveCompletionAsync(started, startedGeneration);
        }
        catch (Exception ex)
        {
            SetSnapshot(TrayState.Error, Sanitize(ex), null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task QuitAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (quitRequested)
            {
                return;
            }

            quitRequested = true;
            SetSnapshot(TrayState.Quitting, snapshot.LastError, null);
            await StopCurrentRunAsync(cancellation).ConfigureAwait(false);
            SetSnapshot(TrayState.Quitting, snapshot.LastError, 0);
        }
        catch (Exception ex)
        {
            quitRequested = false;
            SetSnapshot(TrayState.Error, "cleanup failed: " + Sanitize(ex), null);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await QuitAsync(CancellationToken.None).ConfigureAwait(false);
        if (completionObserver is not null)
        {
            await completionObserver.ConfigureAwait(false);
        }

        gate.Dispose();
    }

    private async Task ObserveCompletionAsync(ITrayRuntimeRun observedRun, long observedGeneration)
    {
        int code;
        string? error = null;
        try
        {
            code = await observedRun.Completion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            code = 1;
            error = Sanitize(ex);
        }

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(run, observedRun) || generation != observedGeneration || quitRequested)
            {
                return;
            }

            await StopCurrentRunAsync(CancellationToken.None).ConfigureAwait(false);
            SetSnapshot(code == 0 ? TrayState.Finished : TrayState.Error, code == 0 ? null : error ?? $"runtime exited {code}", code);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StopCurrentRunAsync(CancellationToken cancellation)
    {
        var current = run;
        if (current is null)
        {
            return;
        }

        SetSnapshot(TrayState.Stopping, snapshot.LastError, null);
        runCancellation?.Cancel();
        if (!current.Completion.IsCompleted)
        {
            try
            {
                await current.StopAsync(cancellation).ConfigureAwait(false);
            }
            catch when (current.Completion.IsCompleted)
            {
            }
        }

        if (!current.Completion.IsCompleted)
        {
            throw new TimeoutException("runtime did not confirm termination; ownership is retained for cleanup retry.");
        }

        await current.DisposeAsync().ConfigureAwait(false);
        if (ReferenceEquals(run, current))
        {
            run = null;
            runCancellation?.Dispose();
            runCancellation = null;
        }
    }

    private async Task UpdateErrorWithoutTransitionAsync(string message, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            SetSnapshot(snapshot.State, message, snapshot.LastExitCode);
        }
        finally
        {
            gate.Release();
        }
    }

    private void SetSnapshot(TrayState state, string? error, int? exitCode)
    {
        // The listener stops in these states, so without a line the log just goes quiet; a repeated retry failure logs once.
        var stopped = state is TrayState.Error or TrayState.Finished
            ? $"tray: {state}{(exitCode is null ? "" : $" exit={exitCode}")}{(error is null ? "" : $": {error}")}"
            : state == TrayState.Listening ? null : loggedStop;
        if (stopped is not null && stopped != loggedStop)
        {
            Log.Info(stopped);
        }

        loggedStop = stopped;

        snapshot = NewSnapshot(state, error, exitCode);
        Volatile.Write(ref published, new StatusView(snapshot, run));
        SnapshotChanged?.Invoke(Snapshot);
    }

    private TraySnapshot NewSnapshot(TrayState state, string? error, int? exitCode) =>
        new(
            state,
            configPath,
            error,
            source.SyntheticInput,
            generation,
            exitCode,
            instanceKey);

    // Not only on the device's return: a USB mic is listed a moment before it can be opened, so the start made on
    // its return can fail, and nothing else would try again until the device flickered.
    public static bool ShouldRetryStart(TrayState state, bool changed, string? effective, int ticks) =>
        state == TrayState.Error && effective is not null && (changed || ticks % 20 == 0);

    private static void ValidateTrayConfig(VoiceSwitchConfig config, TrayInputSource source)
    {
        if (source.SyntheticInput && config.Dictation is null)
        {
            throw new ArgumentException("--input-wav requires a config with dictation.");
        }
    }

    private static string Sanitize(Exception ex) =>
        ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];

    private sealed record StatusView(TraySnapshot State, ITrayRuntimeRun? Run);
}
