using VoiceSwitch.Windows;
using VoiceSwitch.Windows.Core;

internal static class NoWakeTests
{
    public static bool Telemetry()
    {
        var report = new WakeTelemetrySummary();
        report.Observe("dictation recognition: complete id=1 rule=strict inputIdleSeconds=0.250");
        report.Observe("dictation recognition: complete id=2 rule=auto_b inputIdleSeconds=3.000");
        report.Observe("dictation recognition: complete id=3 rule=followup inputIdleSeconds=-1");
        report.Observe("dictation recognition: complete id=4");
        report.Observe("wake label: MISS id=2 rule=auto_b inputIdleSeconds=3.000");
        return report.Rules["strict"] == 1 && report.Contexts["strict:after_input"] == 1
            && report.Contexts["auto_b:idle"] == 1 && report.Contexts["followup:unknown"] == 1
            && report.MissingFields == 1 && report.MissLabels == 1
            && new WakeOrigin(WakeRule.RelaxedD2, double.NaN).Fields == "rule=relaxed_d2 inputIdleSeconds=-1"
            && InputActivity.ElapsedSeconds(250, uint.MaxValue - 749) == 1;
    }
}
