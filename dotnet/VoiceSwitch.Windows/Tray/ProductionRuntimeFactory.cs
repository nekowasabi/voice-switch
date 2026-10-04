using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows.Tray;

public sealed class ProductionRuntimeFactory(string configPath) : ITrayRuntimeFactory
{
    public Task<ITrayRuntimeRun> StartAsync(VoiceSwitchConfig config, TrayInputSource source, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ValidateSource(config, source);
        cancellation.ThrowIfCancellationRequested();

        if (config.Dictation is null)
        {
            var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var resident = new ResidentRuntime(configPath, config, null, SpeechPowerShell.Start, CommandRunner.Run, _ => { }, false, commandCancellation.Token);
            var commandCompletion = Task.Factory.StartNew(resident.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return Task.FromResult<ITrayRuntimeRun>(new ProductionRuntimeRun(commandCompletion, commandCancellation, null));
        }

        IPcmCapture capture = source.WavPath is null
            ? WinMmCapture.Open()
            : new WavPcmCapture(source.WavPath, paced: true);
        IDictationHandoff handoff = source.RecordOnlyDir is null
            ? new RegisteredSuperwhisperHandoff(WindowsPaths.DefaultHandoffPath(), WindowsPaths.SuperwhisperRecordingsPath(config.Dictation))
            : new LocalRecordingHandoff(source.RecordOnlyDir, source.WavPath);

        var observer = new TrayRuntimeObserver();
        var recognizer = new SpeechPowerShellDictationRecognizer(config, observer.RecordSapiTiming);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var runtime = new WindowsDictationRuntime(config, capture, recognizer, handoff, dryRun: false, observer);
        var completion = Task.Run(() => runtime.RunAsync(linked.Token), CancellationToken.None);
        return Task.FromResult<ITrayRuntimeRun>(new ProductionRuntimeRun(completion, linked, observer));
    }

    private static void ValidateSource(VoiceSwitchConfig config, TrayInputSource source)
    {
        if (source.SyntheticInput && config.Dictation is null)
        {
            throw new ArgumentException("--input-wav requires a config with dictation.");
        }

        if (source.WavPath is not null && source.RecordOnlyDir is null)
        {
            throw new ArgumentException("--input-wav in tray mode requires --record-only so no external app is launched.");
        }

        if (source.WavPath is null && source.RecordOnlyDir is not null)
        {
            throw new ArgumentException("--record-only requires --input-wav.");
        }
    }

    private sealed class ProductionRuntimeRun : ITrayRuntimeRun
    {
        private readonly CancellationTokenSource cancellation;
        private readonly TrayRuntimeObserver? observer;
        private bool disposed;

        public ProductionRuntimeRun(Task<int> completion, CancellationTokenSource cancellation, TrayRuntimeObserver? observer)
        {
            Completion = completion;
            this.cancellation = cancellation;
            this.observer = observer;
        }

        public Task<int> Completion { get; }
        public RecognitionProcessIdentity? OwnedChild => observer?.OwnedChild;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var completed = await Task.WhenAny(Completion, Task.Delay(TimeSpan.FromSeconds(15), linked.Token)).ConfigureAwait(false);
            if (completed != Completion)
            {
                throw new TimeoutException("runtime did not stop within 15 seconds after cancellation.");
            }

            _ = await Completion.ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            cancellation.Cancel();
            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }

    private sealed class TrayRuntimeObserver : IDictationRuntimeObserver
    {
        private readonly object sync = new();
        private RecognitionProcessIdentity? liveChild;

        public RecognitionProcessIdentity? OwnedChild
        {
            get
            {
                lock (sync)
                {
                    return liveChild;
                }
            }
        }

        public void RecordSapiTiming(RecognitionDiagnostic diagnostic)
        {
            if (diagnostic.ChildProcess is not { } identity)
            {
                return;
            }

            lock (sync)
            {
                if (diagnostic.Running)
                {
                    liveChild = identity;
                    return;
                }

                if (liveChild == identity)
                {
                    liveChild = null;
                }
            }
        }

        public void RecognitionQueued(RecognitionRequest request, int pending, long retainedStart, long retainedEnd) { }
        public void RecognitionCompleted(RecognitionRequest request, RecognizedUtterance? recognition, Exception? error) { }
        public void HandoffSubmitted(DictationAudio audio, HandoffResult result) { }
        public void RetentionObserved(long retainedStart, long retainedEnd, int pending) { }
        public void NoiseProcessorCompleted(NoiseProcessorStatus status) { }
    }
}
