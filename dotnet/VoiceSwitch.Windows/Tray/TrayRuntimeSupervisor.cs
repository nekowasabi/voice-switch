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
    private StatusView published = null!;

    public TrayRuntimeSupervisor(
        string configPath,
        TrayInputSource source,
        ITrayRuntimeFactory factory,
        string? instanceKey = null)
    {
        this.configPath = Path.GetFullPath(configPath);
        this.source = source;
        this.factory = factory;
        this.instanceKey = instanceKey;
        snapshot = NewSnapshot(TrayState.Paused, null, null);
        published = new StatusView(snapshot, null);
    }

    public event Action<TraySnapshot>? SnapshotChanged;
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

    public async Task StartAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
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
        finally
        {
            gate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
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
        finally
        {
            gate.Release();
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
