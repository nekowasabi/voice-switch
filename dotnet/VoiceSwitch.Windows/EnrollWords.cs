// --enroll-words: record the wake/stop word a few times, run each clip through the
// production SAPI recognizer, and append the mis-transcriptions as aliases.
//
// Record 3-5 takes of 16 kHz mono PCM16 WAV (practice recordings stay where the
// user put them; they are never copied into the repo). Added aliases land in the
// existing wakeWords (or stopWords with --stop) array, so the resident matcher,
// the dictation wake prefix, and the dedicated stop-word engine pick them up at
// the next config reload without a restart.
using System.Collections.Immutable;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

public static class EnrollWords
{
    public static async Task<int> RunAsync(string configPath, VoiceSwitchConfig config, string word, bool stop, IReadOnlyList<string> wavs, bool dryRun)
    {
        var transcripts = new List<(string Path, string Text)>();
        await using var recognizer = new SpeechPowerShellDictationRecognizer(config);
        long id = 0;
        foreach (var wav in wavs)
        {
            ImmutableArray<short> samples;
            try
            {
                samples = Pcm16Wav.DecodeStrict(new FileInfo(wav), 16000 * 600);
            }
            catch (Exception ex)
            {
                Log.Fatal($"enroll-words: cannot read {wav}: {ex.Message}");
                return 1;
            }

            RecognizedUtterance heard;
            try
            {
                heard = await recognizer.RecognizeAsync(
                    new RecognitionRequest(++id, RecognitionExtent.ClosedUtterance, new SampleRange(0, samples.Length), samples),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Fatal($"enroll-words: transcription needs the Windows SAPI recognizer (System.Speech); {ex.Message}");
                return 1;
            }

            transcripts.Add((wav, heard.Text));
        }

        var existing = (stop ? config.StopWords ?? [] : config.WakeWords).Concat([word]).ToArray();
        var journalTexts = WordEnrollment.ReadJournalTexts(config.Dictation?.JournalDir);
        var results = WordEnrollment.Candidates(transcripts.Select(entry => entry.Text), existing, journalTexts, stop);
        for (var i = 0; i < transcripts.Count; i++)
        {
            Console.WriteLine($"{transcripts[i].Path}\ttranscript=\"{transcripts[i].Text}\"\tnormalized=\"{results[i].Normalized}\"\t{results[i].Label}");
        }

        var aliases = results.Where(result => result.Verdict == WordEnrollmentVerdict.Alias).Select(result => result.Normalized).ToList();
        if (aliases.Count == 0)
        {
            Console.WriteLine($"enroll-words: no new aliases for \"{word}\" (journal entries checked: {journalTexts.Count})");
            return 0;
        }

        string[] added;
        try
        {
            var effective = stop ? (config.StopWords ?? []) : config.WakeWords;
            added = WordEnrollment.AppendAliasesToConfig(configPath, stop ? "stopWords" : "wakeWords", aliases, dryRun, effectiveExisting: effective);
        }
        catch (Exception ex)
        {
            Log.Fatal($"enroll-words: cannot update {configPath}: {ex.Message}");
            return 1;
        }

        if (added.Length == 0)
        {
            Console.WriteLine($"enroll-words: no new aliases for \"{word}\" (candidates already configured)");
        }
        else if (dryRun)
        {
            Console.WriteLine($"enroll-words: dry-run, would append to {(stop ? "stopWords" : "wakeWords")}: [{string.Join(", ", added)}] (journal entries checked: {journalTexts.Count})");
        }
        else
        {
            Console.WriteLine($"enroll-words: appended to {(stop ? "stopWords" : "wakeWords")}: [{string.Join(", ", added)}] (backup: {configPath}.bak)");
        }

        return 0;
    }
}
