using VoiceSwitch.Windows.Core;
using VoiceSwitch.Windows.Tray;

// The tray's Meet pause (verify-meet-pause-windows). The mic owner is a variable the tests flip to stand for a call.
internal static class TrayMeetTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static void Start(TrayRuntimeSupervisor supervisor, StartOrigin origin) =>
        supervisor.StartAsync(None, origin).GetAwaiter().GetResult();

    private static void Hold(TrayRuntimeSupervisor supervisor) =>
        supervisor.HoldForMeetAsync(None).GetAwaiter().GetResult();

    private static void Resume(TrayRuntimeSupervisor supervisor) =>
        supervisor.ResumeAfterMeetAsync(None).GetAwaiter().GetResult();

    private static void Pause(TrayRuntimeSupervisor supervisor) =>
        supervisor.PauseAsync(None).GetAwaiter().GetResult();

    private static TestRuntime Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"voice-switch-tray-meet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            wakeWords = new[] { "音声入力" },
            locale = "ja_JP",
            command = "wake",
            stopWords = new[] { "入力ストップ" },
            stopCommand = "stop",
            dictation = new { startTimeoutMs = 3000, endSilenceMs = 1200, maxSeconds = 60 }
        }));
        return new TestRuntime(dir, configPath);
    }

    public static bool HoldFromErrorBlocksRetry()
    {
        using var temp = Temp();
        string? mic = null;
        var factory = new FakeTrayRuntimeFactory { ThrowOnStart = true };
        var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource(null, null), factory, pauseDuringMeet: true, micInUseBy: _ => mic);
        Start(supervisor, StartOrigin.Launch);
        var error = supervisor.Snapshot.State == TrayState.Error;
        mic = "chrome";
        Hold(supervisor);
        var held = supervisor.Snapshot.State == TrayState.Paused && supervisor.MeetResumeOwed;
        factory.ThrowOnStart = false;
        Start(supervisor, StartOrigin.DeviceRetry);
        var refused = supervisor.Snapshot.State == TrayState.Paused && factory.Started.Count == 0;
        mic = null;
        Resume(supervisor);
        return error && held && refused
            && supervisor.Snapshot.State == TrayState.Listening
            && !supervisor.MeetResumeOwed;
    }

    public static bool UserPauseSurvivesCallEnd()
    {
        using var temp = Temp();
        string? mic = null;
        var factory = new FakeTrayRuntimeFactory();
        var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource(null, null), factory, pauseDuringMeet: true, micInUseBy: _ => mic);
        Start(supervisor, StartOrigin.Launch);
        mic = "chrome";
        Hold(supervisor);
        var held = supervisor.Snapshot.State == TrayState.Paused && supervisor.MeetResumeOwed;
        Pause(supervisor);
        var dropped = !supervisor.MeetResumeOwed;
        mic = null;
        Resume(supervisor);
        Start(supervisor, StartOrigin.DeviceRetry);
        return held && dropped
            && supervisor.Snapshot.State == TrayState.Paused
            && factory.Started.Count == 1;
    }

    public static bool StaleResumeInNextCallStaysHeld()
    {
        using var temp = Temp();
        string? mic = null;
        var factory = new FakeTrayRuntimeFactory();
        var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource(null, null), factory, pauseDuringMeet: true, micInUseBy: _ => mic);
        Start(supervisor, StartOrigin.Launch);
        mic = "chrome";
        Hold(supervisor);
        // Call A ends (the poll queues a Resume), and call B starts before that Resume gets the gate.
        mic = null;
        var owed = supervisor.MeetResumeOwed;
        mic = "msedge";
        Resume(supervisor);
        var stillHeld = supervisor.Snapshot.State == TrayState.Paused && supervisor.MeetResumeOwed && factory.Started.Count == 1;
        mic = null;
        Resume(supervisor);
        return owed && stillHeld
            && supervisor.Snapshot.State == TrayState.Listening
            && factory.Started.Count == 2;
    }

    public static bool OffBehavesAsToday()
    {
        using var temp = Temp();
        var queries = 0;
        var factory = new FakeTrayRuntimeFactory();
        var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource(null, null), factory, micInUseBy: _ => { queries++; return "chrome"; });
        Start(supervisor, StartOrigin.Launch);
        Start(supervisor, StartOrigin.User);
        Hold(supervisor);
        var listening = supervisor.Snapshot.State == TrayState.Listening && !supervisor.MeetResumeOwed;
        Pause(supervisor);
        Resume(supervisor);
        var paused = supervisor.Snapshot.State == TrayState.Paused;
        Start(supervisor, StartOrigin.User);
        var path = Path.Combine(temp.Dir, "meet.json");
        File.WriteAllText(path, """{"wakeWords":["音声入力"],"command":"x"}""");
        var absent = ConfigLoader.Load(path).PauseDuringMeet;
        File.WriteAllText(path, """{"wakeWords":["音声入力"],"command":"x","pauseDuringMeet":true}""");
        var on = ConfigLoader.Load(path).PauseDuringMeet;
        return listening && paused
            && supervisor.Snapshot.State == TrayState.Listening
            && !supervisor.PauseDuringMeet
            && factory.Started.Count == 2
            && queries == 0
            && absent is null
            && on == true;
    }

    // The finding in verify-meet-pause-windows, with no Meet involved: the device retry undid a pause that landed in Error.
    public static bool UserPauseAfterErrorSurvivesRetry()
    {
        using var temp = Temp();
        var factory = new FakeTrayRuntimeFactory { ThrowOnStart = true };
        var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource(null, null), factory);
        Start(supervisor, StartOrigin.Launch);
        var error = supervisor.Snapshot.State == TrayState.Error;
        Pause(supervisor);
        factory.ThrowOnStart = false;
        Start(supervisor, StartOrigin.DeviceRetry);
        var kept = supervisor.Snapshot.State == TrayState.Paused && factory.Started.Count == 0;
        Start(supervisor, StartOrigin.User);
        return error && kept
            && supervisor.Snapshot.State == TrayState.Listening
            && factory.Started.Count == 1;
    }

    // Launching during a call used to listen until the first FollowMeet tick; a device retry from Error did the same.
    public static bool NonUserStartInCallHolds()
    {
        using var temp = Temp();
        string? mic = "chrome";
        var factory = new FakeTrayRuntimeFactory();
        var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource(null, null), factory, pauseDuringMeet: true, micInUseBy: _ => mic);
        Start(supervisor, StartOrigin.Launch);
        var launchHeld = supervisor.Snapshot.State == TrayState.Paused && supervisor.MeetResumeOwed && factory.Started.Count == 0;
        mic = null;
        Resume(supervisor);
        var resumed = supervisor.Snapshot.State == TrayState.Listening && factory.Started.Count == 1;

        factory.FaultCompletionOnStart = true;
        Pause(supervisor);
        Start(supervisor, StartOrigin.User);
        var errored = SpinWait.SpinUntil(() => supervisor.Snapshot.State == TrayState.Error, TimeSpan.FromSeconds(2));
        factory.FaultCompletionOnStart = false;
        mic = "msedge";
        Start(supervisor, StartOrigin.DeviceRetry);
        var retryHeld = supervisor.Snapshot.State == TrayState.Paused && supervisor.MeetResumeOwed && factory.Started.Count == 2;
        return launchHeld && resumed && errored && retryHeld;
    }

    public static bool StaleHoldAfterCallKeepsRun()
    {
        using var temp = Temp();
        string? mic = null;
        var factory = new FakeTrayRuntimeFactory();
        var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource(null, null), factory, pauseDuringMeet: true, micInUseBy: _ => mic);
        Start(supervisor, StartOrigin.Launch);
        Hold(supervisor);
        return supervisor.Snapshot.State == TrayState.Listening
            && !supervisor.MeetResumeOwed
            && factory.Started.Count == 1
            && factory.Started[0].StopCount == 0;
    }

    // IPC can send Start while listening; that is not a resume during the call.
    public static bool UserStartWhileListeningKeepsHold()
    {
        using var temp = Temp();
        string? mic = null;
        var factory = new FakeTrayRuntimeFactory();
        var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource(null, null), factory, pauseDuringMeet: true, micInUseBy: _ => mic);
        Start(supervisor, StartOrigin.Launch);
        mic = "chrome";
        Start(supervisor, StartOrigin.User);
        var noOverride = !supervisor.MeetResumeOwed;
        Hold(supervisor);
        return noOverride
            && supervisor.Snapshot.State == TrayState.Paused
            && supervisor.MeetResumeOwed
            && factory.Started.Count == 1;
    }
}
