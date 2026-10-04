using System.Collections.Immutable;

namespace VoiceSwitch.Windows;

public sealed record PcmFrame(long Start, ImmutableArray<short> Samples);
