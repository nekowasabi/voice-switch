namespace VoiceSwitch.Windows.Core;

public sealed record ModeRestore(string Previous, string Temporary)
{
    public bool Valid => !string.IsNullOrWhiteSpace(Previous) && !string.IsNullOrWhiteSpace(Temporary) && Previous != Temporary;
    public ModeRecovery Decide(string? active) => !Valid || active is null ? ModeRecovery.Unknown
        : active == Temporary ? ModeRecovery.Restore : ModeRecovery.Clear;
}

public enum ModeRecovery { Unknown, Restore, Clear }
