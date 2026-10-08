using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceSwitch.Windows.Tray;

public sealed class TrayIpcServer : IAsyncDisposable
{
    private const int MaxMessageBytes = 4096;
    private static readonly TimeSpan ClientReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClientWriteTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly string pipeName;
    private readonly TrayRuntimeSupervisor supervisor;
    private readonly Func<TrayDiagnostics>? diagnostics;
    private readonly Func<CancellationToken, Task>? shutdown;
    private readonly Action? requestExit;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task loop;

    public TrayIpcServer(
        string pipeName,
        TrayRuntimeSupervisor supervisor,
        Func<TrayDiagnostics>? diagnostics = null,
        Func<CancellationToken, Task>? shutdown = null,
        Action? requestExit = null)
    {
        this.pipeName = pipeName;
        this.supervisor = supervisor;
        this.diagnostics = diagnostics;
        this.shutdown = shutdown;
        this.requestExit = requestExit;
        loop = Task.Run(RunAsync);
    }

    public static async Task<string> SendAsync(string pipeName, TrayCommand command, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(command.ToString()).ConfigureAwait(false);
            var response = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            return response ?? "";
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"tray command '{command}' timed out after {timeout.TotalSeconds:0.#} seconds waiting for tray host '{pipeName}'. Start voice-switch.exe first, or retry after it finishes starting.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task RunAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            await using var pipe = CreateServer();
            try
            {
                await pipe.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
                await HandleClientAsync(pipe, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch
            {
            }
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        var options = PipeOptions.Asynchronous;
        if (OperatingSystem.IsWindows())
        {
            options |= PipeOptions.CurrentUserOnly;
        }

        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, options, MaxMessageBytes, MaxMessageBytes);
    }

    private async Task HandleClientAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var clientTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        clientTimeout.CancelAfter(ClientReadTimeout);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var line = await ReadBoundedLineAsync(reader, clientTimeout.Token).ConfigureAwait(false);
        if (!TryParseCommand(line, out var command))
        {
            await WriteLineWithDeadlineAsync(pipe, """{"ok":false,"error":"unsupported command"}""", cancellationToken).ConfigureAwait(false);
            return;
        }

        var exitAfterResponse = false;
        try
        {
            switch (command)
            {
                case TrayCommand.Start:
                    await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case TrayCommand.Pause:
                    await supervisor.PauseAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case TrayCommand.Reload:
                    await supervisor.ReloadAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case TrayCommand.Quit:
                    if (shutdown is null)
                    {
                        await supervisor.QuitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await shutdown(cancellationToken).ConfigureAwait(false);
                    }

                    exitAfterResponse = requestExit is not null;
                    break;
            }
        }
        catch
        {
            exitAfterResponse = false;
        }

        var body = diagnostics is null
            ? new TrayDiagnostics(supervisor.Snapshot, [], false, "not-checked", Environment.ProcessId)
            : diagnostics();
        await WriteLineWithDeadlineAsync(pipe, JsonSerializer.Serialize(body, JsonOptions).ReplaceLineEndings(" "), cancellationToken).ConfigureAwait(false);
        if (exitAfterResponse)
        {
            this.cancellation.Cancel();
            requestExit?.Invoke();
        }
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[MaxMessageBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var next = await reader.ReadAsync(buffer.AsMemory(total, 1), cancellationToken).ConfigureAwait(false);
            if (next == 0 || buffer[total] == '\n')
            {
                return new string(buffer, 0, total).Trim();
            }

            total += next;
        }

        throw new InvalidDataException("IPC message too large.");
    }

    private static async Task WriteLineWithDeadlineAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ClientWriteTimeout);
        var bytes = Encoding.UTF8.GetBytes(value + Environment.NewLine);
        await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
    }

    private static bool TryParseCommand(string value, out TrayCommand command)
    {
        var normalized = value.Trim();
        command = normalized switch
        {
            nameof(TrayCommand.ShowStatus) => TrayCommand.ShowStatus,
            nameof(TrayCommand.Start) => TrayCommand.Start,
            nameof(TrayCommand.Pause) => TrayCommand.Pause,
            nameof(TrayCommand.Reload) => TrayCommand.Reload,
            nameof(TrayCommand.Quit) => TrayCommand.Quit,
            _ => default
        };
        return normalized is nameof(TrayCommand.ShowStatus)
            or nameof(TrayCommand.Start)
            or nameof(TrayCommand.Pause)
            or nameof(TrayCommand.Reload)
            or nameof(TrayCommand.Quit);
    }
}

public sealed record TrayDiagnostics(
    TraySnapshot Snapshot,
    TrayMenuDiagnostic[] MenuItems,
    bool NotifyIconVisible,
    string ShellRegistration,
    int ProcessId,
    string Icon = "");

public sealed record TrayMenuDiagnostic(string Name, string Text, bool Enabled, bool Checked = false);
