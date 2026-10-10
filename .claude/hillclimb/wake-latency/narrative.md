| round | change (one line) | test pass | train pass | test detected | test nonwake clean | wake latency p50 (test, passing) |
|---|---|---|---|---|---|---|
| 0 | baseline (hangover 300) | 0.202 ± 0.077 | 0.231 | 126/216 | 63/96 | none under 300 |
| 1 | earlyWakeMs 300 | 0.375 ± 0.093 | 0.462 | 126/216 | 63/96 | -54 ms |
| 2 | prefix wake words fire after 150 ms silence | 0.548 ± 0.096 | 0.856 | 126/216 | 63/96 | 183 ms |
| **3** | **short wake word needs a pause before one-breath dictation** | **0.654 ± 0.091** | 0.929 | 108/216 | **96/96** | 186 ms |
| 4 | pause threshold 150 → 50 ms (reverted) | 0.654 | 0.933 | 108/216 | 96/96 | 186 ms |

Rows are case × 3 reps. ± is the 95 % binomial interval over 104 test cases. Reps never changed a verdict, so the interval reflects case sampling only. Cost: $0, because everything runs on device.

**Recommended change.** Adopt v3: `earlyWakeMs: 300` plus the code from rounds 2 and 3. On the held-out set, pass goes from 0.202 to 0.654. Every passing wake fires 186 ms after the word ends at the median and 227 ms at worst. Compound-noun false wakes such as 音声認識 drop from 33 rows to 0.

**Versus baseline.** Baseline never met 300 ms, because the hangover plus STT always lands at 337 to 408 ms. Round 1's early probe fixed 音声入力 only. Round 2 let wake words that also start a longer wake word (音声, 音声に入る) fire after 150 ms of silence instead of the hangover. Round 3 required a pause after the ambiguous short wake word 音声 before a one-breath dictation.

**Why trust this.** The audio set is fixed and every verdict reproduced across 3 reps. Test prosodies, continuation sentences and nonwake sentences never appear in train. The analysis only read train verdicts. Round 4 tested the obvious follow-up and showed no effect.

**What it costs.** Round 3 loses detection on fast one-breath "おんせい、…" utterances. STT transcribes them as 音声テスト… with no comma and no timing gap, so they look exactly like a compound noun. That is 2 train cases and 6 test cases going from late to missed. Test detection overall is bounded by Apple STT: 30 test cases are never detected at baseline. High pitch turns 音声に入る into 大勢に入る or 香水に入る, and some おんせい turn into お店 or ボブ声.

**What else was tried.** v4 lowered the pause threshold to 50 ms and recovered nothing, so it was reverted. The train split has no high-pitch prosody, which is why test trails train. That is a split coverage gap, not overfitting: an out-of-set high-pitch probe showed STT misreads, not segmentation errors.
