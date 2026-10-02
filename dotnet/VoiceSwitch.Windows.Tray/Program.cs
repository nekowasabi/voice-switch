using System.Text.Json;
using System.Windows.Forms;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows.Tray;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var options = TrayOptions.Parse(args);
            if (options.IpcCommand is { } command)
            {
                using var identity = TraySingleInstance.Identify(options.ConfigPath);
                return SendCommand(identity.PipeName, command);
            }

            using var instance = TraySingleInstance.Acquire(options.ConfigPath);
            if (!instance.IsOwner)
            {
                Console.WriteLine(TrayIpcServer.SendAsync(instance.PipeName, TrayCommand.ShowStatus, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                return 0;
            }

            var supervisor = new TrayRuntimeSupervisor(
                options.ConfigPath,
                options.Source,
                new ProductionRuntimeFactory(),
                CountPendingHandoffs,
                instance.Key);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var context = new VoiceSwitchTrayContext(supervisor);
            var ipc = new TrayIpcServer(instance.PipeName, supervisor, context.Diagnostics, context.QuitRuntimeAsync, context.RequestExitThread);
            try
            {
                if (options.Start)
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
            Console.Error.WriteLine(ex.Message);
            return 1;
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

    private static int CountPendingHandoffs()
    {
        var root = WindowsPaths.DefaultHandoffPath();
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.json").Count()
            : 0;
    }
}
