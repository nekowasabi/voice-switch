# Automatic interjection filtering

`dictation.noWakeWord.filterInterjections` is a Boolean setting. Its default is `false`. Omitting the setting preserves existing transcript retention.

```json
{
  "dictation": {
    "noWakeWord": {
      "filterInterjections": true
    }
  }
}
```

The fragment adds filtering to an existing configuration. It does not enable automatic recording. The existing `followup`, `autoIdle`, and `relaxedReadingDistance2` settings control those recording paths.

## Matching rule

With filtering enabled, automatic transcripts equal to `あっ` or `ああ` are discarded before they enter dictation history. The comparison removes surrounding Unicode whitespace and trailing sentence punctuation from this set: `。.!！?？`.

The comparison preserves internal spaces and punctuation. It also preserves leading punctuation, commas, and quotes. `はい`, `うん`, `何`, `あ、あ`, `あ あ`, `「あっ」`, `アア`, and longer speech remain eligible for history. Retained text is stored unchanged, including its original whitespace and punctuation.

The filter applies to `auto_b`, `followup`, and `relaxed_d2` recordings. Strict wake-word and manual recordings are exempt. A spoken strict wake word keeps its existing priority over an automatic recording.

## Recording and delivery behavior

Each recording keeps the setting captured when its dictation session starts. A configuration reload does not change the policy for a pending transcript. The next session uses the reloaded setting.

Both Superwhisper results and local transcription fallback use the same history admission rule. Discarding an interjection completes processing without a second transcription attempt, a history notification, a paste, or a routed message. History clearing, cancellation, and duplicate protection keep their existing behavior.

This setting does not change voice activity detection or `minSpeechMs`.

## Accuracy limits

A genuine spoken `あっ` or `ああ` is indistinguishable from a cough that produces the same transcript. Enabling this option also discards those genuine automatic utterances. A cough transcribed as other text can still enter history.

This is an opt-in transcript filter. Its effectiveness and false-suppression rate on a representative real-audio corpus are unverified. Synthetic policy tests exercise the matching and delivery rules; they do not measure cough-detection accuracy.

## Verification

Run the portable policy checks with .NET 8 and Swift installed:

```sh
python3 tests/cough-filter/run_policy_tests.py
```

Both languages read `tests/parity/fixtures/cough_policy.json`. The C# regression also exercises the real configuration loader, dictation session, and local handoff. Its injected transcription delegate supplies deterministic test text. An unused SAPI adapter throws if the portable test accidentally invokes it.

The config-to-local regression uses only APIs that existed before this setting. It can run against a separate baseline source tree:

```sh
python3 tests/cough-filter/run_policy_tests.py --regression-source /path/to/baseline
```

That baseline is expected to fail the history-admission assertion. The ordinary Windows test suite also covers fake Superwhisper delivery, mode restoration, automatic-to-strict promotion, clear, cancellation, and duplicate safeguards. Portable checks do not verify live macOS frameworks, Windows SAPI, or Superwhisper.
