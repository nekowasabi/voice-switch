using System.Runtime.InteropServices;
using System.Windows.Forms;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows.Tray;

public static class TrayHost
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!CliOptions.IsTrayLaunch(args))
        {
            AttachParentConsole();
            return VoiceSwitch.Windows.Program.Run(args);
        }

        try
        {
            var options = TrayOptions.Parse(args);
            if (options.IpcCommand is { } command)
            {
                AttachParentConsole();
                using var identity = TraySingleInstance.Identify(options.ConfigPath);
                return SendCommand(identity.PipeName, command);
            }

            using var instance = TraySingleInstance.Acquire(options.ConfigPath);
            if (!instance.IsOwner)
            {
                AttachParentConsole();
                Console.WriteLine(TrayIpcServer.SendAsync(instance.PipeName, TrayCommand.ShowStatus, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Built on this STA thread before the runtime exists; the runtime reaches them only through the hooks below.
            var hotkeys = new DictationHotkeys();
            using var hud = new DictationHud();
            using var hook = new KeyboardHook(hotkeys, hud);
            var supervisor = new TrayRuntimeSupervisor(
                options.ConfigPath,
                options.Source,
                new ProductionRuntimeFactory(options.ConfigPath, new TrayRuntimeHooks(
                    OnPhase: phase => hud.Post(() =>
                    {
                        hud.Show(phase);
                        hook.PhaseChanged(phase);
                    }),
                    PlayStopSound: TraySettings.PlayStopSound,
                    PlayWakeSound: TraySettings.PlayWakeSound,
                    ForegroundWindow: WindowFocus.Foreground,
                    RestoreFocus: target => hud.Post(() => WindowFocus.RestoreIfSuperwhisperForeground(target)),
                    Hotkeys: hotkeys,
                    MicDevice: () => TraySettings.MicDevice,
                    OnDelivery: state => hud.Post(() => hud.Deliver(state)))),
                instance.Key,
                pauseDuringMeet: PauseDuringMeet(options.ConfigPath),
                // --paused is the user's pause: a call ending must not start the tray.
                userPaused: options.Paused);

            using var context = new VoiceSwitchTrayContext(supervisor);
            using var wakeLabelHotkey = new WakeLabelHotkey(context.ShowHistory, hotkeys.RequestStart, PauseShortcut(options.ConfigPath),
                () => { if (context.TogglePause() is { } paused) hud.Notify(paused); });
            hud.HistoryRequested += context.ShowHistory;
            void HistoryChanged() => hud.Post(hud.HistoryAvailable);
            DictationHistory.Shared.Changed += HistoryChanged;
            var ipc = new TrayIpcServer(instance.PipeName, supervisor, context.Diagnostics, context.QuitRuntimeAsync, context.RequestExitThread);
            try
            {
                if (!options.Paused)
                {
                    _ = supervisor.StartAsync(CancellationToken.None, user: false);
                }

                Application.Run(context);
            }
            finally
            {
                DictationHistory.Shared.Changed -= HistoryChanged;
                ipc.DisposeAsync().AsTask().GetAwaiter().GetResult();
                supervisor.QuitAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            return 0;
        }
        catch (Exception ex)
        {
            AttachParentConsole();
            Log.Fatal(ex.Message);
            return 1;
        }
    }

    // Read once at launch; a reload does not re-register it. A broken config is the supervisor's to report, so it only means no hotkey here.
    private static KeyChord? PauseShortcut(string configPath)
    {
        string? name;
        try { name = ConfigLoader.Load(configPath).PauseShortcut; }
        catch (Exception) { return null; }
        if (name is null) return null;
        var chord = DictationHotkeys.Parse(name);
        if (chord is null) Log.Info($"pause hotkey ignored: cannot parse \"{name}\"");
        return chord;
    }

    // Read once at launch, as on Mac; a broken config means off.
    private static bool PauseDuringMeet(string configPath)
    {
        try { return ConfigLoader.Load(configPath).PauseDuringMeet == true; }
        catch (Exception) { return false; }
    }

    // Why: a WinExe gets no console, so CLI output from cmd/PowerShell would vanish. Only attach when
    // stdout is unset; redirected handles (pipes from WSL, files) already work and must not be replaced.
    private static void AttachParentConsole()
    {
        if (GetStdHandle(StdOutputHandle) == IntPtr.Zero)
        {
            AttachConsole(AttachParentProcess);
        }
    }

    private static int SendCommand(string pipeName, TrayCommand command)
    {
        try
        {
            var response = TrayIpcServer.SendAsync(pipeName, command, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Console.WriteLine(response);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);
}
