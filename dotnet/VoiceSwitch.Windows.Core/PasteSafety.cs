namespace VoiceSwitch.Windows.Core;

public readonly record struct PasteSafety(bool TargetFocused, bool PlainTextField, bool CompositionClear,
    bool ClipboardRestorable, bool InputUnchanged)
{
    public bool AllowsPaste => TargetFocused && PlainTextField && CompositionClear && ClipboardRestorable && InputUnchanged;
}
