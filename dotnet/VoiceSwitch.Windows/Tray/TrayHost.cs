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
                    PlayWakeSound: TraySettings.PlayWakeSound,
                    ForegroundWindow: WindowFocus.Foreground,
                    RestoreFocus: target => hud.Post(() => WindowFocus.RestoreIfSuperwhisperForeground(target)),
                    Hotkeys: hotkeys)),
                instance.Key);

            using var context = new VoiceSwitchTrayContext(supervisor);
            var ipc = new TrayIpcServer(instance.PipeName, supervisor, context.Diagnostics, context.QuitRuntimeAsync, context.RequestExitThread);
            try
            {
                if (!options.Paused)
                {
                    _ = supervisor.StartAsync(CancellationToken.None);
                }

                Application.Run(context);
            }
            finally
            {
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
