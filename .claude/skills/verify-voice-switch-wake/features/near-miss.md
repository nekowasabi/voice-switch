# Near-miss census

Near-miss census reads the sample wake log and prints which near-miss buckets the current matcher still misses. It does not swap `DictationCore.cs`.

## Sub-features

- `gained-lost` counts sample rows the current matcher wakes that an older cut did not, and the reverse.
- `long-d12` lists whole-utterance long-wake misses at distance 1 or 2.
- `short-d1` lists whole-utterance short-wake misses at distance 1.
- `long-d3` lists whole-utterance long-wake misses at distance 3. The sample's only row is the synthetic `法政入力`.

## How to get to it (user POV)

- Run `verify.sh drive near-miss` after the WakeReplay dll exists.
- This is a log report. It does not listen.

## Driving it with verify-wake

Preconditions:

- `verify.sh doctor` prints `doctor: ok`.
- `verify.sh build` has produced `VoiceSwitch.WakeReplay.dll`, or this drive will build it.
- `fixture-replay` is not running.

- **Classify.** Run `VS_RUN_ID=near-miss verify.sh drive near-miss`. Exit code 0.
- **Baseline lines.** Evidence stdout contains these whole fields: `agreed_true=2 agreed_false=7 gained=1 lost=0`, `near_miss_examples_long_wake_d1_2_misses_whole (shown 0, wake.Reading.Length>=10, !hit, whole-utterance):`, `near_miss_examples_short_wake_d1_misses_whole (shown 0, wake.Reading.Length<10, !hit, whole-utterance, d=1):`, `near_miss_examples_long_wake_d3_misses_whole (shown 1, wake.Reading.Length>=10, !hit, whole-utterance, d=3):`, `d3_near_miss_intentional=1 d3_near_miss_ambient=0`, and `text="法政入力" reading="ほうぜいにゅうりょく" wake=おんせいにゅうりょく d=3 intentional=True`.
- **Unchanged Core.** `git status -- dotnet/VoiceSwitch.Windows.Core/DictationCore.cs` shows no modification from this drive.

## Gotchas

- `法政入力` in the sample is a synthetic distance-3 example. The census does not say that live logs contain a pile of long distance-3 misses. H7 measured none overnight.
- `gained=1` is the sample row `縫製入力` at distance 2. Raising that count without a new labeled hit is not a recognition improvement.
- A matcher change that wakes `法政入力` removes the `(shown 1,` distance-3 header. Update this drive's expected lines in the same commit or the drive fails. Match the whole field. `gained=1` is also a prefix of `gained=10` through `gained=19`. Do that only when the corpus guards `安全入力` (`あんぜんにゅうりょく`) and `防衛入力` (`ぼうぜいにゅうりょく`) stay `reject`.
- `--log` exits 0 when the file is readable. The line checks are what make the drive fail. A bare exit code without those lines is not this feature.
- Do not point `--log` at a private voice log. This recipe uses only `tests/fixtures/wake_log_replay_sample.log`.
