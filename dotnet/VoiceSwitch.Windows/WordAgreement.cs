using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

// Characters two Japanese recognizers of speech.cpp (github.com/nyosegawa/speech.cpp) both wrote for a recording. Silero
// still hears a voiced cough as speech; each recognizer invents its own word for it, while spoken words come out the same
// from both. macOS pairs Apple Speech with Whisper instead (MacApp.swift agreedWordCount).
public static class WordAgreement
{
    private static readonly SpeechWorker[] Workers = [new("parakeet-tdt_ctc-0.6b-ja"), new("reazonspeech-v2")];

    // Loading the models, and fetching 1.3 GB of them on the first run, takes far longer than a recording; pay it at launch.
    public static void Warm()
    {
        foreach (var worker in Workers) worker.Start();
    }

    // Throws while a worker is not ready or fails; the caller hands the recording off as macOS does.
    public static async Task<int> CountAsync(ImmutableArray<short> samples)
    {
        var texts = await Task.WhenAll(Workers.Select(worker => worker.TranscribeAsync(samples)));
        return NoWakePolicy.AgreedCharacters(texts[0], texts[1]);
    }

    private sealed class SpeechWorker(string model)
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        // A worker that will not start (not installed, no Vulkan) is retried at most this often, not on every check.
        private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
        private readonly object sync = new();
        private readonly SemaphoreSlim gate = new(1, 1);
        private Task<Process>? starting;
        private DateTime failedAt = DateTime.MinValue;
        private long nextId;

        public void Start()
        {
            lock (sync)
            {
                if (starting is { IsFaulted: false } && !(starting.IsCompletedSuccessfully && starting.Result.HasExited)) return;
                if (starting is not null && DateTime.UtcNow - failedAt < RetryAfter) return;
                starting = Task.Run(StartAsync);
            }
        }

        public async Task<string> TranscribeAsync(ImmutableArray<short> samples)
        {
            Start();
            Task<Process>? current;
            lock (sync) current = starting;
            if (current is not { IsCompletedSuccessfully: true } || current.Result.HasExited)
                throw new InvalidOperationException($"speech worker {model} is not ready");
            var process = current.Result;
            await gate.WaitAsync();
            try
            {
                return await ExchangeAsync(process, samples).WaitAsync(RequestTimeout);
            }
            catch
            {
                // A worker that timed out or broke the protocol may still be busy with the request; start a fresh one later.
                try { process.Kill(); } catch (InvalidOperationException) { }
                lock (sync) failedAt = DateTime.MinValue;
                throw;
            }
            finally { gate.Release(); }
        }

        private async Task<string> ExchangeAsync(Process process, ImmutableArray<short> samples)
        {
            var id = Interlocked.Increment(ref nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var pcm = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
            // One second per chunk keeps each JSON line small whatever the recording's length.
            const int chunkBytes = 32_000;
            for (var seq = 0; seq * chunkBytes < pcm.Length; seq++)
            {
                var length = Math.Min(chunkBytes, pcm.Length - seq * chunkBytes);
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "chunk", id, seq, pcm = Convert.ToBase64String(pcm, seq * chunkBytes, length) }));
            }
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "transcribe", id, sample_rate = 16000 }));
            await process.StandardInput.FlushAsync();
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync() ?? throw new InvalidOperationException($"speech worker {model} exited");
                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                if (!root.TryGetProperty("id", out var answered) || answered.GetString() != id)
                {
                    if (root.GetProperty("type").GetString() == "error") throw new InvalidOperationException($"speech worker {model}: {root.GetProperty("error").GetProperty("message").GetString()}");
                    continue;
                }
                switch (root.GetProperty("type").GetString())
                {
                    case "end": return root.GetProperty("text").GetString() ?? "";
                    case "progress": continue;
                    default: throw new InvalidOperationException($"speech worker {model}: {line}");
                }
            }
        }

        private async Task<Process> StartAsync()
        {
            try
            {
                var psi = new ProcessStartInfo(Executable() ?? throw new FileNotFoundException("speech.cpp is not installed (speech.exe not found)"))
                {
                    ArgumentList = { "worker", model },
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var process = ChildProcessJob.Start(psi);
                // Drained from the start: a worker blocked on a full stderr pipe (model download progress) would never answer.
                var stderr = process.StandardError.ReadToEndAsync();
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync();
                    if (line is null)
                    {
                        await process.WaitForExitAsync();
                        throw new InvalidOperationException($"exited {process.ExitCode}: {(await stderr).Trim()}");
                    }
                    using var message = JsonDocument.Parse(line);
                    var type = message.RootElement.GetProperty("type").GetString();
                    if (type == "ready")
                    {
                        Log.Info($"word agreement: {model} ready pid={process.Id}");
                        return process;
                    }
                    if (type == "fatal") throw new InvalidOperationException(message.RootElement.GetProperty("error").GetProperty("message").GetString());
                }
            }
            catch (Exception ex)
            {
                lock (sync) failedAt = DateTime.UtcNow;
                Log.Info($"word agreement: {model} unavailable, automatic recordings skip word agreement: {ex.Message}");
                throw;
            }
        }

        // The installer's junction first: a PATH entry it added is not seen by a process started before the install.
        private static string? Executable()
        {
            var installed = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Programs", "speech.cpp", "current", "speech.exe");
            if (File.Exists(installed)) return installed;
            return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(dir => Path.Combine(dir, "speech.exe")).FirstOrDefault(File.Exists);
        }
    }
}
