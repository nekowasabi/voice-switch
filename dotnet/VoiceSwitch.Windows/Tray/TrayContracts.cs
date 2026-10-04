using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows.Tray;

public enum TrayState
{
    Stopped,
    Starting,
    Listening,
    Pausing,
    Stopping,
    Paused,
    Reloading,
    Finished,
    Error,
    Quitting
}

public enum TrayCommand
{
    ShowStatus,
    Start,
    Pause,
    Reload,
    Quit
}

public sealed record TraySnapshot(
    TrayState State,
    string ConfigPath,
    int PendingHandoffs,
    string? LastError,
    bool SyntheticInput,
    long Generation,
    int? LastExitCode = null,
    string? InstanceKey = null,
    int? OwnedChildProcessId = null,
    DateTimeOffset? OwnedChildProcessStartTimeUtc = null);

public sealed record TrayInputSource(string? WavPath, string? RecordOnlyDir)
{
    public bool SyntheticInput => WavPath is not null;
}

public interface ITrayRuntimeFactory
{
    Task<ITrayRuntimeRun> StartAsync(VoiceSwitchConfig config, TrayInputSource source, CancellationToken cancellation);
}

public interface ITrayRuntimeRun : IAsyncDisposable
{
    Task<int> Completion { get; }
    RecognitionProcessIdentity? OwnedChild { get; }
    Task StopAsync(CancellationToken cancellation);
}
