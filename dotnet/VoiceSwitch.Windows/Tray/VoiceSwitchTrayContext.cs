using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceSwitch.Windows.Tray;

public sealed class VoiceSwitchTrayContext : ApplicationContext
{
    private readonly TrayRuntimeSupervisor supervisor;
    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem statusItem = new("Status");
    private readonly ToolStripMenuItem startItem = new("Start");
    private readonly ToolStripMenuItem pauseItem = new("Pause");
    private readonly ToolStripMenuItem reloadItem = new("Reload");
    private readonly ToolStripMenuItem settingsItem = new("Settings");
    private readonly ToolStripMenuItem errorItem = new("Recent error");
    private readonly ToolStripMenuItem quitItem = new("Quit");
    private readonly int uiThreadId;
    private readonly System.Windows.Forms.Timer diagnosticsTimer;
    private TraySnapshot snapshot;
    private TrayDiagnostics cachedDiagnostics = null!;
    private bool disposed;

    public VoiceSwitchTrayContext(TrayRuntimeSupervisor supervisor)
    {
        this.supervisor = supervisor;
        uiThreadId = Environment.CurrentManagedThreadId;
        snapshot = supervisor.Snapshot;
        supervisor.SnapshotChanged += OnSnapshotChanged;

        menu = new ContextMenuStrip();
        foreach (var item in new[] { statusItem, startItem, pauseItem, reloadItem, settingsItem, errorItem, quitItem })
        {
            menu.Items.Add(item);
        }

        statusItem.Enabled = false;
        startItem.Click += async (_, _) => await RunCommandAsync(TrayCommand.Start);
        pauseItem.Click += async (_, _) => await RunCommandAsync(TrayCommand.Pause);
        reloadItem.Click += async (_, _) => await RunCommandAsync(TrayCommand.Reload);
        settingsItem.Click += (_, _) => ShowSettings();
        errorItem.Click += (_, _) => ShowError();
        quitItem.Click += async (_, _) =>
        {
            try
            {
                await ShutdownAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "voice-switch tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Text = "voice-switch tray: paused",
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => ShowSettings();
        _ = menu.Handle;
        ApplySnapshot(snapshot);
        diagnosticsTimer = new System.Windows.Forms.Timer { Interval = 500 };
        diagnosticsTimer.Tick += (_, _) =>
        {
            if (!disposed)
            {
                ApplySnapshot(supervisor.Snapshot);
            }
        };
        diagnosticsTimer.Start();
    }

    public TrayDiagnostics Diagnostics()
    {
        var observed = Volatile.Read(ref cachedDiagnostics);
        return observed with
        {
            Snapshot = supervisor.Snapshot,
            MenuItems = observed.MenuItems.ToArray()
        };
    }

    public async Task ShutdownAsync(CancellationToken cancellation)
    {
        await supervisor.QuitAsync(cancellation);
        await ExitThreadOnUiAsync();
    }

    public Task QuitRuntimeAsync(CancellationToken cancellation) =>
        supervisor.QuitAsync(cancellation);

    public void RequestExitThread()
    {
        if (Environment.CurrentManagedThreadId == uiThreadId)
        {
            ExitThread();
            return;
        }

        try
        {
            menu.BeginInvoke(() => ExitThread());
        }
        catch
        {
        }
    }

    private TrayDiagnostics BuildDiagnostics() =>
        new(
            snapshot,
            menu.Items.OfType<ToolStripMenuItem>()
                .Select(item => new TrayMenuDiagnostic(item.Name ?? "", item.Text ?? "", item.Enabled))
                .ToArray(),
            notifyIcon.Visible,
            ShellNotifyIconRegistration(),
            Environment.ProcessId);

    private void PublishDiagnosticsOnUi() =>
        Volatile.Write(ref cachedDiagnostics, BuildDiagnostics());

    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            supervisor.SnapshotChanged -= OnSnapshotChanged;
            if (disposing)
            {
                diagnosticsTimer.Stop();
                diagnosticsTimer.Dispose();
                notifyIcon.Visible = false;
                PublishDiagnosticsOnUi();
                notifyIcon.Dispose();
                menu.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private void OnSnapshotChanged(TraySnapshot next)
    {
        if (Environment.CurrentManagedThreadId == uiThreadId)
        {
            ApplySnapshot(next);
            return;
        }

        try
        {
            notifyIcon.ContextMenuStrip?.BeginInvoke(() => ApplySnapshot(next));
        }
        catch
        {
        }
    }

    private void ApplySnapshot(TraySnapshot next)
    {
        if (disposed)
        {
            return;
        }

        snapshot = next;
        statusItem.Name = "status";
        startItem.Name = "start";
        pauseItem.Name = "pause";
        reloadItem.Name = "reload";
        settingsItem.Name = "settings";
        errorItem.Name = "error";
        quitItem.Name = "quit";

        var state = next.State.ToString();
        statusItem.Text = $"Status: {state}";
        startItem.Text = next.State is TrayState.Paused or TrayState.Stopped or TrayState.Finished or TrayState.Error ? "Start / Resume" : "Start / Resume";
        startItem.Enabled = next.State is TrayState.Paused or TrayState.Stopped or TrayState.Finished or TrayState.Error;
        pauseItem.Enabled = next.State is TrayState.Starting or TrayState.Listening;
        reloadItem.Enabled = next.State is not TrayState.Quitting and not TrayState.Starting and not TrayState.Pausing and not TrayState.Reloading;
        errorItem.Text = string.IsNullOrWhiteSpace(next.LastError) ? "Recent error: none" : "Recent error: available";
        errorItem.Enabled = !string.IsNullOrWhiteSpace(next.LastError);
        notifyIcon.Text = TruncateTooltip($"voice-switch: {state}");
        PublishDiagnosticsOnUi();
    }

    private async Task RunCommandAsync(TrayCommand command, CancellationToken cancellation = default)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
            switch (command)
            {
                case TrayCommand.Start:
                    await supervisor.StartAsync(cts.Token);
                    break;
                case TrayCommand.Pause:
                    await supervisor.PauseAsync(cts.Token);
                    break;
                case TrayCommand.Reload:
                    await supervisor.ReloadAsync(cts.Token);
                    break;
                case TrayCommand.Quit:
                    await supervisor.QuitAsync(cts.Token);
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "voice-switch tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private Task ExitThreadOnUiAsync()
    {
        if (Environment.CurrentManagedThreadId == uiThreadId)
        {
            ExitThread();
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            menu.BeginInvoke(() =>
            {
                try
                {
                    ExitThread();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            });
        }
        catch (Exception ex)
        {
            done.SetException(ex);
        }

        return done.Task;
    }

    private void ShowSettings()
    {
        var text = $"""
        Config: {snapshot.ConfigPath}
        State: {snapshot.State}
        Synthetic input: {snapshot.SyntheticInput}
        Instance: {snapshot.InstanceKey ?? "-"}
        """;
        MessageBox.Show(text, "voice-switch settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowError()
    {
        if (!string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            MessageBox.Show(snapshot.LastError, "voice-switch recent error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string TruncateTooltip(string value) =>
        value.Length <= 63 ? value : value[..63];

    private string ShellNotifyIconRegistration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "not-verified: non-Windows runtime";
        }

        try
        {
            var window = typeof(NotifyIcon).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(notifyIcon)
                ?? typeof(NotifyIcon).GetField("window", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(notifyIcon);
            var id = typeof(NotifyIcon).GetField("_id", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(notifyIcon)
                ?? typeof(NotifyIcon).GetField("id", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(notifyIcon);
            var handleProperty = window?.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (handleProperty?.GetValue(window) is not IntPtr hwnd || hwnd == IntPtr.Zero || id is null)
            {
                return "not-verified: notify icon handle unavailable";
            }

            var identifier = new NotifyIconIdentifier
            {
                CbSize = Marshal.SizeOf<NotifyIconIdentifier>(),
                HWnd = hwnd,
                UID = Convert.ToUInt32(id)
            };
            var result = Shell_NotifyIconGetRect(ref identifier, out _);
            return result == 0 ? "registered" : $"not-verified: Shell_NotifyIconGetRect={result}";
        }
        catch (Exception ex)
        {
            return "not-verified: " + ex.GetType().Name;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect iconLocation);

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int CbSize;
        public IntPtr HWnd;
        public uint UID;
        public Guid GuidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
