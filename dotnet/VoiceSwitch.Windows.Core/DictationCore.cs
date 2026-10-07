using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace VoiceSwitch.Windows.Core;

public readonly record struct SampleRange
{
    public SampleRange(long start, long end)
    {
        if (start < 0 || end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "sample range must be half-open and non-negative.");
        }

        Start = start;
        End = end;
    }

    public long Start { get; }
    public long End { get; }
    public long Length => End - Start;
    public bool IsEmpty => Length == 0;
}

public enum RecognitionExtent
{
    PrefixHead,
    ClosedUtterance
}

public enum FinishReason
{
    StandaloneStop,
    Silence,
    MaximumDuration,
    FinishCommand,
    CancelCommand,
    StartTimeout,
    Empty,
    Discontinuity,
    Overflow,
    StaleRecognition,
    RecognitionFailed
}

public enum HandoffStatus
{
    Transcribed,
    NoResult,
    FailedBeforeDispatch,
    DryRunSuppressed,
    RecordedLocally
}

public enum DictationPhase
{
    Idle,
    Waiting,
    Recording,
    Ended
}

public sealed record LexicalRun(string Text, SampleRange Range, string? Reading = null);

public sealed record RecognitionAlternate(string Text, string Reading);

public sealed record RecognitionRequest(long Id, RecognitionExtent Extent, SampleRange Range, ImmutableArray<short> Samples);

public sealed record RecognizedUtterance(
    long Id,
    RecognitionExtent Extent,
    SampleRange Source,
    string Text,
    ImmutableArray<LexicalRun> Lexemes,
    bool HadRejectedSpeech = false,
    double? Confidence = null,
    // What SAPI heard but rejected as too uncertain; diagnostics only, never part of Text or Lexemes.
    string? RejectedText = null,
    // Every accepted result came from the constrained stop-word grammar, none from free dictation.
    bool FromStopGrammar = false,
    // SAPI's runner-up readings of the same audio; diagnostics only.
    ImmutableArray<RecognitionAlternate> Alternates = default);

public sealed record DictationEvent(string Kind, FinishReason? Reason, SampleRange? Range, Guid? SessionId = null)
{
    public static DictationEvent Started(Guid sessionId, SampleRange body) => new("started", null, body, sessionId);
    public static DictationEvent Submitted(Guid sessionId, FinishReason reason, SampleRange body) => new("submitted", reason, body, sessionId);
    public static DictationEvent Cancelled(FinishReason reason) => new("cancelled", reason, null);
    public static DictationEvent Error(FinishReason reason) => new("error", reason, null);
}

public sealed record DictationAudio(Guid SessionId, SampleRange Range, ImmutableArray<short> Samples, FinishReason Reason = FinishReason.Silence, nint Target = 0);

public sealed record HandoffResult(HandoffStatus Status, Guid Id, string? Path, string Message);

public interface IDictationRecognizer
{
    Task<RecognizedUtterance> RecognizeAsync(RecognitionRequest request, CancellationToken cancellation);
}

public interface IDictationHandoff
{
    Task<HandoffResult> SubmitAsync(DictationAudio audio, CancellationToken cancellation);
}

public sealed class SampleStore
{
    private readonly List<short> samples = new();
    private long maxRetainedSamples;
    private long start;
    private long next;

    public SampleStore(long maxRetainedSamples = 1_120_000)
    {
        this.maxRetainedSamples = maxRetainedSamples > 0
            ? maxRetainedSamples
            : throw new ArgumentOutOfRangeException(nameof(maxRetainedSamples));
    }

    public long Start => start;
    public long Next => next;

    // Grow only: a smaller budget could already be exceeded by audio a reload must not drop.
    public void EnsureCapacity(long samples) => maxRetainedSamples = Math.Max(maxRetainedSamples, samples);

    public void Append(long rangeStart, ReadOnlySpan<short> block)
    {
        if (rangeStart != next)
        {
            throw new InvalidOperationException($"PCM discontinuity at {rangeStart}; expected {next}.");
        }

        samples.AddRange(block.ToArray());
        next += block.Length;
        if (samples.Count > maxRetainedSamples)
        {
            throw new InvalidOperationException($"retained PCM budget exceeded: {samples.Count} samples.");
        }
    }

    public ImmutableArray<short> Copy(SampleRange range)
    {
        if (range.Start < start || range.End > next)
        {
            throw new InvalidOperationException($"sample range {range.Start}..{range.End} is outside retained PCM {start}..{next}.");
        }

        return samples.GetRange((int)(range.Start - start), (int)range.Length).ToImmutableArray();
    }

    public void RetainFrom(long firstRequired)
    {
        if (firstRequired <= start)
        {
            return;
        }

        if (firstRequired > next)
        {
            samples.Clear();
            start = next;
            return;
        }

        samples.RemoveRange(0, (int)(firstRequired - start));
        start = firstRequired;
    }
}

public static class DictationBoundaries
{
    public static WakePrefix? LeadingWake(RecognizedUtterance recognition, IEnumerable<string> wakeWords) =>
        LeadingWake(recognition, wakeWords.Select(word => WakeWord.From(word)).ToArray());

    public static WakePrefix? LeadingWake(RecognizedUtterance recognition, IReadOnlyList<WakeWord> wakes)
    {
        var lexemes = recognition.Lexemes.Where(run => TextMatching.Normalize(run.Text).Length > 0).ToArray();
        if (lexemes.Length == 0)
        {
            return null;
        }

        var normalizedWake = wakes
            .Select(wake => wake.Text)
            .Where(value => value.Length > 0)
            .Distinct()
            .OrderByDescending(value => value.Length)
            .ToArray();

        var index = 0;
        var consumedEnd = lexemes[0].Range.Start;
        var consumedAny = false;
        WakeWord? byReading = null;
        while (index < lexemes.Length)
        {
            var matched = false;
            long? fusedSplit = null;
            foreach (var wake in normalizedWake)
            {
                var built = "";
                var end = index;
                while (end < lexemes.Length && built.Length < wake.Length)
                {
                    built += TextMatching.Normalize(lexemes[end].Text);
                    end++;
                }

                if (built == wake)
                {
                    consumedAny = true;
                    matched = true;
                    consumedEnd = lexemes[end - 1].Range.End;
                    index = end;
                    break;
                }

                // SAPI sometimes fuses the wake word's tail with the first body characters into one word ("音声" + "入力今日").
                // That word's audio is split by character count, so the body starts inside it.
                if (fusedSplit is null && built.Length > wake.Length && built.StartsWith(wake, StringComparison.Ordinal))
                {
                    var last = lexemes[end - 1];
                    var lastLength = TextMatching.Normalize(last.Text).Length;
                    var wakeCharsInLast = wake.Length - (built.Length - lastLength);
                    fusedSplit = last.Range.Start + last.Range.Length * wakeCharsInLast / lastLength;
                }
            }

            if (matched)
            {
                continue;
            }

            if (fusedSplit is long split)
            {
                return new WakePrefix(split, split);
            }

            // A fuzzy second wake must not turn "音声入力 温泉…" into a wait, so it is tried only before anything is consumed.
            if (ReadingWake(lexemes, index, wakes, allowFuzzy: !consumedAny, closed: recognition.Extent == RecognitionExtent.ClosedUtterance) is not { } hit)
            {
                break;
            }

            if (hit.Distance > 0)
            {
                return new WakePrefix(hit.End, null, hit.Wake, hit.Distance);
            }

            if (hit.Next is not int next)
            {
                return new WakePrefix(hit.End, hit.End, hit.Wake);
            }

            consumedAny = true;
            consumedEnd = hit.End;
            index = next;
            byReading = hit.Wake;
        }

        if (!consumedAny)
        {
            return null;
        }

        var bodyStart = index < lexemes.Length ? lexemes[index].Range.Start : (long?)null;
        return new WakePrefix(consumedEnd, bodyStart, byReading);
    }

    // Wake readings of this many kana or fewer (おんせい) match by reading only when they are the whole closed
    // utterance at distance ≤1. As an exact prefix, kana おんせい opened a body on 音声メモ… and 音声認識… in 5 of 54
    // non-wake TTS fixtures; as a 1-distance prefix it would take ordinary speech starting with 温泉 or 安静; distance 2
    // would widen that further (ほうせい). On the 2026-10-04 live log the rule keeps 温水 x5, 温泉 and 温泉に入る and gives
    // up 温泉は, 温泉入浴 x3 and 温泉有力を…. Longer closed wakes may use distance ≤2 (縫製入力 / ほうせい vs おんせい).
    public const int ShortWakeReading = 4;

    // Next is the lexeme after the wake, or null when the wake ends inside a lexeme (End is then a proportional split).
    private readonly record struct ReadingHit(WakeWord Wake, int Distance, long End, int? Next);

    // SAPI writes a slurred 音声 as 温泉 or 温水 (distance 1) or 縫製 (distance 2 on the long wake). Text equality misses
    // those while the kana differ. Longest wake reading first; the utterance prefix may be one kana shorter or longer
    // than the wake (size±1 only). Short wakes stay at distance ≤1 and whole-closed only; long closed wakes allow ≤2.
    private static ReadingHit? ReadingWake(LexicalRun[] lexemes, int index, IReadOnlyList<WakeWord> wakes, bool allowFuzzy, bool closed)
    {
        var readings = lexemes.Skip(index)
            .Select(run => TextMatching.NormalizeReading(string.IsNullOrEmpty(run.Reading) ? run.Text : run.Reading))
            .ToArray();
        var all = string.Concat(readings);
        foreach (var wake in wakes.Where(wake => wake.Reading.Length > 0).DistinctBy(wake => wake.Reading).OrderByDescending(wake => wake.Reading.Length))
        {
            var size = wake.Reading.Length;
            (int Distance, int Length)? best = null;
            // Long closed wakes may accept distance 2 (縫製入力); short stays ≤1 to avoid 温泉/温水 FP widening.
            var maxDistance = (size > ShortWakeReading && closed) ? 2 : 1;
            foreach (var length in new[] { size, size - 1, size + 1 })
            {
                if (length <= 0 || length > all.Length)
                {
                    continue;
                }

                var distance = TextMatching.EditDistance(all[..length], wake.Reading);
                if (distance > maxDistance || (distance >= 1 && !allowFuzzy) || (size <= ShortWakeReading && !(closed && length == all.Length)))
                {
                    continue;
                }

                if (best is null || distance < best.Value.Distance)
                {
                    best = (distance, length);
                }
            }

            if (best is not { } match)
            {
                continue;
            }

            var offset = 0;
            for (var k = 0; k < readings.Length; k++)
            {
                var end = offset + readings[k].Length;
                if (match.Length <= end)
                {
                    var run = lexemes[index + k];
                    return match.Length == end
                        ? new ReadingHit(wake, match.Distance, run.Range.End, index + k + 1)
                        : new ReadingHit(wake, match.Distance, run.Range.Start + run.Range.Length * (match.Length - offset) / readings[k].Length, null);
                }

                offset = end;
            }
        }

        return null;
    }

    // ponytail: fixed floor with no field data behind it yet; tune from the conf= on "via=rejected" log lines.
    public const double RejectedWakeMinConfidence = 0.2;

    // SAPI rejected the whole utterance, but its best guess is exactly a wake word. Wake-only by construction:
    // a rejected result has no word timings, so it can open the wait for the body but never cut one.
    public static WakePrefix? RejectedWake(RecognizedUtterance recognition, IEnumerable<string> wakeWords)
    {
        if (recognition.Extent != RecognitionExtent.ClosedUtterance
            || TextMatching.Normalize(recognition.Text).Length > 0
            || recognition.RejectedText is not { } rejected
            || !(recognition.Confidence >= RejectedWakeMinConfidence)
            || !wakeWords.Select(TextMatching.Normalize).Contains(TextMatching.Normalize(rejected)))
        {
            return null;
        }

        return new WakePrefix(recognition.Source.End, null);
    }

    public static bool IsStandaloneStop(RecognizedUtterance recognition, IEnumerable<string> stopWords)
    {
        return StandaloneStopRange(recognition, stopWords) is not null;
    }

    // ponytail: floor set from TTS fixtures. With both grammars loaded, real stop words scored 0.57-0.73 and no body
    // phrase was ever answered by the stop grammar; alone, the stop grammar force-matched "ストップしないで" at 0.24.
    // Tune from the "grammar=stop conf=" log lines once real-voice data exists.
    public const double StopGrammarMinConfidence = 0.4;

    public static SampleRange? StandaloneStopRange(RecognizedUtterance recognition, IEnumerable<string> stopWords)
    {
        if (recognition.Extent != RecognitionExtent.ClosedUtterance || recognition.HadRejectedSpeech)
        {
            return null;
        }

        // The stop grammar answers with a stop word verbatim even when it force-matched other speech, so only its
        // confidence separates a stop from a short body phrase. Free dictation text keeps the plain equality match.
        if (recognition.FromStopGrammar && !(recognition.Confidence >= StopGrammarMinConfidence))
        {
            return null;
        }

        var normalized = TextMatching.Normalize(recognition.Text);
        if (normalized.Length == 0 || !stopWords.Select(TextMatching.Normalize).Contains(normalized))
        {
            return null;
        }

        var lexemes = recognition.Lexemes.Where(run => TextMatching.Normalize(run.Text).Length > 0).ToArray();
        if (lexemes.Length == 0)
        {
            return recognition.Source;
        }

        return new SampleRange(lexemes[0].Range.Start, lexemes[^1].Range.End);
    }
}

// ByReading is set only when the reading pass matched; Distance is that match's edit distance.
public sealed record WakePrefix(long WakeEnd, long? BodyStart, WakeWord? ByReading = null, int Distance = 0);

public sealed class DictationSession
{
    private readonly VoiceSwitchConfig config;
    private readonly WakeWord[] wakes;
    private readonly List<DictationEvent> events = new();
    private Guid sessionId;
    private long? bodyStart;
    private long lastSpeechEnd;
    // Where the end-silence window counts from: never before lastSpeechEnd, and at body start the clock when the body
    // was applied. Recognition completes 0.8-2 s after the audio, and Mac counts silence from the moment the dictation opens.
    private long silenceFrom;
    private long maxEnd;
    private long startTimeoutEnd;
    private long wakeEnd;
    private long wakeSourceEnd;
    private bool awaitingBody;
    private bool terminal;

    public DictationSession(VoiceSwitchConfig config)
    {
        this.config = config;
        wakes = config.Wakes();
    }

    public IReadOnlyList<DictationEvent> Events => events;
    public SampleRange? PendingBody => bodyStart is long start && !terminal ? new SampleRange(start, lastSpeechEnd) : null;
    public bool IsActive => bodyStart is not null && !terminal;
    public bool IsAwaitingBody => awaitingBody && !terminal;
    public bool IsTerminal => terminal;
    public long? RequiredAudioStart => bodyStart;
    public long? AwaitingWakeEnd => awaitingBody && !terminal ? wakeEnd : null;
    public long? AwaitingWakeSourceEnd => awaitingBody && !terminal ? wakeSourceEnd : null;
    public long? SilenceDeadline => bodyStart is not null && !terminal ? silenceFrom + MsToSamples(config.Dictation?.EndSilenceMs ?? 1200) : null;

    // now: the capture clock (sample position) when this recognition is applied; it trails the audio by the recognizer's latency.
    public DictationAudio? Apply(RecognizedUtterance recognition, Func<SampleRange, ImmutableArray<short>> copyAudio, long now = 0)
    {
        if (terminal)
        {
            events.Add(DictationEvent.Error(FinishReason.StaleRecognition));
            return null;
        }

        if (awaitingBody)
        {
            if (DictationBoundaries.StandaloneStopRange(recognition, config.StopWords ?? []) is not null)
            {
                Cancel(FinishReason.Empty);
                return null;
            }

            var wake = DictationBoundaries.LeadingWake(recognition, wakes) ?? DictationBoundaries.RejectedWake(recognition, config.WakeWords);
            if (wake is { BodyStart: null })
            {
                wakeEnd = Math.Max(wakeEnd, wake.WakeEnd);
                wakeSourceEnd = Math.Max(wakeSourceEnd, recognition.Source.End);
                startTimeoutEnd = Math.Max(wakeEnd, now) + MsToSamples(config.Dictation?.StartTimeoutMs ?? 3000);
                return null;
            }

            var start = wake?.BodyStart ?? recognition.Source.Start;
            StartBody(start, recognition.Source.End, now);
            return null;
        }

        if (bodyStart is null)
        {
            var wake = DictationBoundaries.LeadingWake(recognition, wakes) ?? DictationBoundaries.RejectedWake(recognition, config.WakeWords);
            if (wake is null)
            {
                return null;
            }

            sessionId = Guid.NewGuid();
            maxEnd = (wake.BodyStart ?? wake.WakeEnd) + SecondsToSamples(config.Dictation?.MaxSeconds ?? config.MaxSeconds ?? 60);
            if (wake.BodyStart is long start)
            {
                StartBody(start, recognition.Source.End, now);
            }
            else
            {
                wakeEnd = wake.WakeEnd;
                wakeSourceEnd = recognition.Source.End;
                startTimeoutEnd = Math.Max(wake.WakeEnd, now) + MsToSamples(config.Dictation?.StartTimeoutMs ?? 3000);
                awaitingBody = true;
            }

            return null;
        }

        if (recognition.Source.Start >= maxEnd)
        {
            return Finish(new SampleRange(bodyStart.Value, Math.Min(lastSpeechEnd, maxEnd)), FinishReason.MaximumDuration, copyAudio);
        }

        if (DictationBoundaries.StandaloneStopRange(recognition, config.StopWords ?? []) is { } stop)
        {
            return Finish(new SampleRange(bodyStart.Value, Math.Max(bodyStart.Value, stop.Start)), FinishReason.StandaloneStop, copyAudio);
        }

        lastSpeechEnd = Math.Min(Math.Max(lastSpeechEnd, recognition.Source.End), maxEnd);
        silenceFrom = Math.Max(silenceFrom, lastSpeechEnd);
        if (lastSpeechEnd >= maxEnd)
        {
            return Finish(new SampleRange(bodyStart.Value, maxEnd), FinishReason.MaximumDuration, copyAudio);
        }

        return null;
    }

    public DictationAudio? AdvanceTo(long sampleEnd, Func<SampleRange, ImmutableArray<short>> copyAudio)
    {
        if (terminal)
        {
            return null;
        }

        if (awaitingBody && sampleEnd >= startTimeoutEnd)
        {
            Cancel(FinishReason.StartTimeout);
            return null;
        }

        if (bodyStart is long start && sampleEnd >= maxEnd)
        {
            var end = Math.Min(maxEnd, Math.Max(start, lastSpeechEnd));
            return Finish(new SampleRange(start, end), FinishReason.MaximumDuration, copyAudio);
        }

        return null;
    }

    public DictationAudio? AdvanceSpeechTo(long speechEnd, Func<SampleRange, ImmutableArray<short>> copyAudio)
    {
        if (terminal || bodyStart is null)
        {
            return null;
        }

        lastSpeechEnd = Math.Min(Math.Max(lastSpeechEnd, speechEnd), maxEnd);
        silenceFrom = Math.Max(silenceFrom, lastSpeechEnd);
        return lastSpeechEnd >= maxEnd
            ? Finish(new SampleRange(bodyStart.Value, maxEnd), FinishReason.MaximumDuration, copyAudio)
            : null;
    }

    public DictationAudio? FinishSilenceAt(long sampleEnd, Func<SampleRange, ImmutableArray<short>> copyAudio)
    {
        return SilenceDeadline is long deadline && sampleEnd >= deadline
            ? Finish(FinishReason.Silence, copyAudio)
            : null;
    }

    // Finish pressed after a lone wake word once speech followed it, before the recognizer turned that speech into the body.
    public DictationAudio? FinishHeard(long start, long end, Func<SampleRange, ImmutableArray<short>> copyAudio)
    {
        if (!IsAwaitingBody)
        {
            return null;
        }

        var stop = Math.Min(end, maxEnd);
        return Finish(new SampleRange(start, Math.Max(start, stop)), FinishReason.FinishCommand, copyAudio);
    }

    public DictationAudio? Finish(FinishReason reason, Func<SampleRange, ImmutableArray<short>> copyAudio)
    {
        if (terminal)
        {
            return null;
        }

        if (bodyStart is not long start || lastSpeechEnd <= start)
        {
            terminal = true;
            events.Add(DictationEvent.Cancelled(awaitingBody && reason == FinishReason.StartTimeout ? FinishReason.StartTimeout : FinishReason.Empty));
            return null;
        }

        return Finish(new SampleRange(start, lastSpeechEnd), reason, copyAudio);
    }

    public void Cancel(FinishReason reason = FinishReason.CancelCommand)
    {
        terminal = true;
        events.Add(DictationEvent.Cancelled(reason));
    }

    private DictationAudio? Finish(SampleRange range, FinishReason reason, Func<SampleRange, ImmutableArray<short>> copyAudio)
    {
        terminal = true;
        if (range.IsEmpty)
        {
            events.Add(DictationEvent.Cancelled(FinishReason.Empty));
            return null;
        }

        var audio = new DictationAudio(sessionId, range, copyAudio(range), reason);
        events.Add(DictationEvent.Submitted(sessionId, reason, range));
        return audio;
    }

    private static long SecondsToSamples(double seconds) =>
        checked((long)Math.Round(seconds * Segmenter.Rate, MidpointRounding.AwayFromZero));

    private static long MsToSamples(int ms) =>
        checked((long)Math.Round(ms * Segmenter.Rate / 1000.0, MidpointRounding.AwayFromZero));

    private void StartBody(long start, long end, long now)
    {
        bodyStart = start;
        lastSpeechEnd = end;
        silenceFrom = Math.Max(end, now);
        maxEnd = start + SecondsToSamples(config.Dictation?.MaxSeconds ?? config.MaxSeconds ?? 60);
        awaitingBody = false;
        events.Add(DictationEvent.Started(sessionId, new SampleRange(start, end)));
    }
}

public static class Pcm16Wav
{
    public const int Rate = 16000;
    public const short Channels = 1;
    public const short BitsPerSample = 16;
    public const short BlockAlign = 2;
    public const int ByteRate = Rate * BlockAlign;

    public static byte[] Encode(ReadOnlySpan<short> samples)
    {
        var dataLength = checked(samples.Length * 2);
        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(Channels);
        writer.Write(16000);
        writer.Write(ByteRate);
        writer.Write(BlockAlign);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static ImmutableArray<short> DecodeStrict(FileInfo file, int maxSamples)
    {
        if (maxSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSamples));
        }

        if (!file.Exists)
        {
            throw new FileNotFoundException("input WAV was not found.", file.FullName);
        }

        var maxBytes = checked(44L + maxSamples * 2L + 4096L);
        if (file.Length < 44 || file.Length > maxBytes)
        {
            throw new InvalidDataException($"input WAV length is outside the supported bounded PCM range: {file.Length} bytes.");
        }

        using var reader = new BinaryReader(file.OpenRead(), Encoding.ASCII);
        if (FourCc(reader) != "RIFF")
        {
            throw new InvalidDataException("input WAV must start with RIFF.");
        }

        var riffSize = ReadUInt32(reader, "RIFF size");
        if (riffSize + 8UL != (ulong)file.Length)
        {
            throw new InvalidDataException("input WAV RIFF size does not match file length.");
        }

        if (FourCc(reader) != "WAVE")
        {
            throw new InvalidDataException("input WAV must be WAVE.");
        }

        short? channels = null;
        int? rate = null;
        short? bits = null;
        short? blockAlign = null;
        int? byteRate = null;
        byte[]? data = null;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var chunk = FourCc(reader);
            var length = ReadUInt32(reader, $"{chunk} length");
            if (length > int.MaxValue || reader.BaseStream.Position + (long)length > reader.BaseStream.Length)
            {
                throw new InvalidDataException($"input WAV chunk {chunk} length is invalid.");
            }

            if (chunk == "fmt ")
            {
                if (length < 16)
                {
                    throw new InvalidDataException("input WAV fmt chunk is too short.");
                }

                var formatTag = reader.ReadInt16();
                channels = reader.ReadInt16();
                rate = reader.ReadInt32();
                byteRate = reader.ReadInt32();
                blockAlign = reader.ReadInt16();
                bits = reader.ReadInt16();
                reader.BaseStream.Position += (long)length - 16;
                if (formatTag != 1)
                {
                    throw new InvalidDataException("input WAV must be PCM.");
                }
            }
            else if (chunk == "data")
            {
                if (data is not null)
                {
                    throw new InvalidDataException("input WAV must contain exactly one data chunk.");
                }

                if ((length & 1) == 1 || length / 2 > (ulong)maxSamples)
                {
                    throw new InvalidDataException("input WAV data chunk is not bounded PCM16 data.");
                }

                data = reader.ReadBytes((int)length);
            }
            else
            {
                reader.BaseStream.Position += (long)length;
            }

            if ((length & 1) == 1)
            {
                reader.BaseStream.Position++;
            }
        }

        if (channels != Channels || rate != Rate || bits != BitsPerSample || blockAlign != BlockAlign || byteRate != ByteRate || data is null)
        {
            throw new InvalidDataException("input WAV must be PCM16 mono 16 kHz.");
        }

        var samples = new short[data.Length / 2];
        Buffer.BlockCopy(data, 0, samples, 0, data.Length);
        return samples.ToImmutableArray();
    }

    public static string Sha256Hex(ReadOnlySpan<short> samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FourCc(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
        {
            throw new EndOfStreamException("input WAV ended before a chunk id.");
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private static uint ReadUInt32(BinaryReader reader, string name)
    {
        try
        {
            return reader.ReadUInt32();
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException($"input WAV ended before {name}.", ex);
        }
    }
}
