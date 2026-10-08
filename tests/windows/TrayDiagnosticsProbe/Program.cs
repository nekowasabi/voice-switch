using VoiceSwitch.Windows.Core;
using VoiceSwitch.Windows.Tray;

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("SKIP requires Windows");
    return 0;
}

var temp = Path.Combine(Path.GetTempPath(), "voice-switch-tray-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var ready = new TaskCompletionSource<VoiceSwitchTrayContext>(TaskCreationOptions.RunContinuationsAsynchronously);
var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseDispose = new ManualResetEventSlim(false);
var releaseExit = new ManualResetEventSlim(false);
Exception? staError = null;

try
{
    var configPath = Path.Combine(temp, "config.json");
    File.WriteAllText(configPath, """
    {"wakeWords":["音声入力"],"command":"wake","dictation":{"startTimeoutMs":3000,"endSilenceMs":1200,"maxSeconds":60}}
    """);
    var supervisor = new TrayRuntimeSupervisor(configPath, new TrayInputSource("in.wav", Path.Combine(temp, "out")), new ProbeFactory());
    var sta = new Thread(() =>
    {
        try
        {
            using var context = new VoiceSwitchTrayContext(supervisor);
            ready.SetResult(context);
            releaseDispose.Wait();
            context.Dispose();
            disposed.SetResult();
            releaseExit.Wait();
        }
        catch (Exception ex)
        {
            staError = ex;
            ready.TrySetException(ex);
            disposed.TrySetException(ex);
        }
    });
    sta.SetApartmentState(ApartmentState.STA);
    sta.Start();

    var context = await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var before = await Task.Run(context.Diagnostics).WaitAsync(TimeSpan.FromMilliseconds(500));
    if (before.Snapshot.State != TrayState.Paused || before.MenuItems.Length == 0 || !before.NotifyIconVisible)
    {
        Console.Error.WriteLine("initial diagnostics did not describe the paused tray context");
        return 1;
    }

    releaseDispose.Set();
    await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var after = await Task.Run(context.Diagnostics).WaitAsync(TimeSpan.FromMilliseconds(500));
    if (after.NotifyIconVisible)
    {
        Console.Error.WriteLine("disposed diagnostics still reported a visible notify icon");
        return 1;
    }

    releaseExit.Set();
    if (!sta.Join(TimeSpan.FromSeconds(2)))
    {
        Console.Error.WriteLine("STA worker did not exit");
        return 1;
    }

    if (staError is not null)
    {
        Console.Error.WriteLine(staError);
        return 1;
    }

    Console.WriteLine("PASS tray diagnostics do not require UI message pumping");
    return 0;
}
finally
{
    releaseDispose.Set();
    releaseExit.Set();
    Directory.Delete(temp, recursive: true);
}

sealed class ProbeFactory : ITrayRuntimeFactory
{
    public Task<ITrayRuntimeRun> StartAsync(VoiceSwitchConfig config, TrayInputSource source, CancellationToken cancellation) =>
        throw new NotSupportedException("probe does not start a runtime");
}
