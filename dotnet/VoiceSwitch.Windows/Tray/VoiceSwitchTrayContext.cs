using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows.Tray;

public sealed class VoiceSwitchTrayContext : ApplicationContext
{
    private readonly TrayRuntimeSupervisor supervisor;
    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip menu;
    // Japanese labels as on Mac; Name stays the stable id that scripts and diagnostics match.
    private readonly ToolStripMenuItem statusItem = new("状態") { Name = "status" };
    private readonly ToolStripMenuItem startItem = new("再開") { Name = "start" };
    private readonly ToolStripMenuItem pauseItem = new("一時停止") { Name = "pause" };
    private readonly ToolStripMenuItem micItem = new("マイク") { Name = "mic" };
    private readonly ToolStripMenuItem openConfigItem = new("設定ファイルを開く") { Name = "open-config" };
    private readonly ToolStripMenuItem reloadItem = new("設定を再読み込み") { Name = "reload" };
    private readonly ToolStripMenuItem openLogItem = new("ログを開く") { Name = "open-log" };
    private readonly ToolStripMenuItem soundItem = new("効果音") { Name = "sound", CheckOnClick = true };
    private readonly ToolStripMenuItem loginItem = new("ログイン時に起動") { Name = "login" };
    private readonly ToolStripMenuItem errorItem = new("直近のエラー") { Name = "error" };
    private readonly ToolStripMenuItem quitItem = new("終了") { Name = "quit" };
    private readonly LoginItem login = new();
    private int deviceTicks;
    private string? lastEffectiveDevice;
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
        menu.Items.AddRange([statusItem, startItem, pauseItem, new ToolStripSeparator(), micItem, openConfigItem, reloadItem, openLogItem, soundItem, loginItem, errorItem, new ToolStripSeparator(), quitItem]);
        // Rebuilt on every open so plugged and unplugged devices show up (Mac menuNeedsUpdate).
        micItem.DropDownItems.Add(new ToolStripMenuItem("-"));
        micItem.DropDownOpening += (_, _) => FillMicMenu();

        statusItem.Enabled = false;
        soundItem.Checked = TraySettings.ConfirmationSound;
        soundItem.CheckedChanged += (_, _) => TraySettings.ConfirmationSound = soundItem.Checked;
        loginItem.Checked = login.IsEnabled(LoginCommand);
        // Read again on every open: the Run entry can change outside voice-switch.
        menu.Opening += (_, _) => loginItem.Checked = login.IsEnabled(LoginCommand);
        startItem.Click += async (_, _) => await RunCommandAsync(TrayCommand.Start);
        pauseItem.Click += async (_, _) => await RunCommandAsync(TrayCommand.Pause);
        reloadItem.Click += async (_, _) => await RunCommandAsync(TrayCommand.Reload);
        openConfigItem.Click += (_, _) => Open(snapshot.ConfigPath);
        openLogItem.Click += (_, _) => Open(WindowsPaths.DefaultLogPath());
        loginItem.Click += (_, _) => ToggleLogin();
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
            Icon = TrayIcons.Stopped,
            ContextMenuStrip = menu,
            Text = "voice-switch: 停止中",
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
                if (++deviceTicks % 4 == 0)
                {
                    FollowDevice();
                }
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
                .Select(item => new TrayMenuDiagnostic(item.Name ?? "", item.Text ?? "", item.Enabled, item.Checked))
                .ToArray(),
            notifyIcon.Visible,
            ShellNotifyIconRegistration(),
            Environment.ProcessId,
            notifyIcon.Icon == TrayIcons.Listening ? "mic" : "mic-slash");

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
        var state = StateLabel(next.State);
        statusItem.Text = $"状態: {state}";
        startItem.Enabled = next.State is TrayState.Paused or TrayState.Stopped or TrayState.Finished or TrayState.Error;
        pauseItem.Enabled = next.State is TrayState.Starting or TrayState.Listening;
        reloadItem.Enabled = next.State is not TrayState.Quitting and not TrayState.Starting and not TrayState.Pausing and not TrayState.Reloading;
        errorItem.Text = string.IsNullOrWhiteSpace(next.LastError) ? "直近のエラー: なし" : "直近のエラーを表示";
        errorItem.Enabled = !string.IsNullOrWhiteSpace(next.LastError);
        var icon = next.State == TrayState.Listening ? TrayIcons.Listening : TrayIcons.Stopped;
        if (notifyIcon.Icon != icon)
        {
            notifyIcon.Icon = icon;
        }

        notifyIcon.Text = TruncateTooltip($"voice-switch: {state}");
        PublishDiagnosticsOnUi();
    }

    private async Task RunCommandAsync(TrayCommand command, CancellationToken cancellation = default, bool quiet = false)
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
        catch (Exception ex) when (!quiet)
        {
            MessageBox.Show(ex.Message, "voice-switch tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            Log.Info($"tray: {command} failed: {ex.Message}");
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

    private void FillMicMenu()
    {
        var pinned = TraySettings.MicDevice;
        micItem.DropDownItems.Clear();
        foreach (var (title, name) in new (string, string?)[] { ("システムのデフォルト", null) }.Concat(WinMmCapture.InputDevices().Select(d => (d, (string?)d))))
        {
            var item = new ToolStripMenuItem(title) { Checked = name == pinned };
            item.Click += async (_, _) =>
            {
                TraySettings.MicDevice = name;
                Log.Info($"tray: microphone {name ?? "system default"}");
                await RunCommandAsync(TrayCommand.Reload);
            };
            micItem.DropDownItems.Add(item);
        }
    }

    // WinMM never tells an open capture that its device went away or that the default changed, so every 2 s the
    // device a fresh open would pick is compared with the one in use; a difference restarts the run onto it.
    private void FollowDevice()
    {
        var effective = WinMmCapture.EffectiveDevice(TraySettings.MicDevice);
        var changed = effective != lastEffectiveDevice;
        lastEffectiveDevice = effective;
        if (snapshot.State == TrayState.Listening && WinMmCapture.CurrentDevice is { } current && current != effective)
        {
            Log.Info($"tray: microphone changed from {current} to {effective ?? "none"}; restarting");
            _ = RunCommandAsync(TrayCommand.Reload, quiet: true);
        }
        else if (snapshot.State == TrayState.Error && changed && effective is not null)
        {
            Log.Info($"tray: microphone {effective} is available; starting");
            _ = RunCommandAsync(TrayCommand.Start, quiet: true);
        }
    }

    private static string StateLabel(TrayState state) => state switch
    {
        TrayState.Listening => "待ち受け中",
        TrayState.Paused or TrayState.Stopped or TrayState.Finished => "停止中",
        TrayState.Starting => "開始中",
        TrayState.Pausing or TrayState.Stopping => "停止しています",
        TrayState.Reloading => "再読み込み中",
        TrayState.Error => "エラー",
        TrayState.Quitting => "終了しています",
        _ => state.ToString()
    };

    private string LoginCommand => LoginItem.CommandFor(Environment.ProcessPath ?? Application.ExecutablePath, snapshot.ConfigPath);

    private void ToggleLogin()
    {
        try
        {
            var enable = !login.IsEnabled(LoginCommand);
            login.Set(LoginCommand, enable);
            loginItem.Checked = login.IsEnabled(LoginCommand);
            Log.Info($"tray: launch at sign-in {(enable ? "on" : "off")}: {LoginCommand}");
            if (enable && LoginCommand.StartsWith("\"\\\\", StringComparison.Ordinal))
            {
                MessageBox.Show("voice-switch.exe がネットワーク上（WSL など）にあるため、サインイン時にはまだ開けない場合があります。ローカルのフォルダに置くと確実です。",
                    "voice-switch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show($"ログイン項目の変更に失敗しました: {ex.Message}", "voice-switch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        PublishDiagnosticsOnUi();
    }

    // Mac NSWorkspace.open: the file opens in whatever app Windows associates with it.
    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show($"開けませんでした: {path}\n{ex.Message}", "voice-switch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
