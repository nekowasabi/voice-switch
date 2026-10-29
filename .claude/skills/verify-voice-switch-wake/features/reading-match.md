# Reading match

Reading match scores a frozen list of recognized phrases. Each phrase is labeled wake or no-wake. The scorer calls the production wake APIs twice: once with the configured kana readings, and once with the surface word only (what `WakeWord.From` does when no reading was stored).

## Sub-features

- `configured-readings` wakes when the kana reading is the one in `corpus.json`.
- `surface-only` wakes when the reading falls back to the surface word.
- `early-probe` asks `EarlyWake` and requires tail silence.
- `rejected-hypothesis` asks `RejectedWake` on an empty accepted text.
- `guard-rows` are phrases that must stay quiet.

## How to get to it (user POV)

- Run `verify.sh drive reading-match` after `verify.sh build`.
- There is no microphone control and no tray menu for this score.

## Driving it with verify-wake

Preconditions:

- `verify.sh doctor` prints `doctor: ok`.
- `corpus/corpus.json` is the file whose sha256 is `97fbaf6f615971d59eb0b509560c4939027d7b813a24f9eaf46765bacc8da593`.
- `VS_RUN_ID` is set when the evidence folder must be named.

- **Build.** Prepare the scorer. Run `verify.sh build`. Both build logs contain `0 Error(s)` and `wake-corpus.dll` exists under the scratch `build` directory.
- **Score.** Run `VS_RUN_ID=reading-match verify.sh drive reading-match`. Exit code 0. Stdout in the evidence folder contains `corpus_sha256=97fbaf6f615971d59eb0b509560c4939027d7b813a24f9eaf46765bacc8da593`, `rows=47`, `UNVERIFIED real-audio mac windows-hardware`, `floor configured hits>=15 misses<=8 false_wakes=0`, and `floor surface hits>=7 misses<=16 false_wakes=0`.
- **Totals.** The same stdout contains `rule=configured hits=<n> misses=<n> false_wakes=0 rejects=<n> rows=47` and the same shape for `rule=surface`. On the unchanged matcher, configured is hits=15 misses=8 and surface is hits=7 misses=16. A later change may raise hits. It may not add a `false_wake` or drop hits below those floors.
- **Rows.** Each phrase prints `row <rule> <id> expect=<wake|no-wake> got=<wake|no-wake> <hit|miss|false_wake|reject>`. Read the row lines for the phrase you changed. The totals alone do not say which phrase moved.

## Gotchas

- Hits are recognized-text matches, not heard wakes. Do not report a real-world recognition-rate change from this drive.
- The configured rule passes explicit readings into `WakeWord.From`. The surface rule does not. A matcher change and a lexicon change move different rows.
- `RejectedWake` is called with the wake surface strings, not the readings. A kana rejected hypothesis stays a miss on both rules until the matcher and this scorer agree on readings.
- Editing a label, or replacing `corpus.json`, changes the sha256 and fails the drive even if the totals still look calm.
- The floor check is `hits` at least 15 (configured) or 7 (surface), `misses` at most 8 or 16, and `false_wakes` exactly 0. Equality of the hit count is the wrong gate: a real improvement raises hits.
- `anzen` uses the dictionary reading `あんぜんにゅうりょく`. The string `あんぜいにゅうりょく` is already inside the shipped distance-2 gate and is not a guard.
