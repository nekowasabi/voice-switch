# Metrics

- **pass** (binary, headline): wake cases pass when the first wake-ish verdict from `voice-switch --check` fires within 300 ms of the end of the wake word (fire position + measured STT ms − wake end). Nonwake cases pass when no wake-ish verdict fires at all, because a `dictate` verdict opens a dictation.
- **latency_ms** (wake cases only): fire position + STT ms − wake end. Lower is better.
- **Wake end** is measured on the clean rendering of each file: the segment end, followed by a gap, nearest the lone wake word's end. If none lies within 200 ms, the lone end is used (`wake_end_src` in row meta).
- **tags[0]**: wake-lone, wake-comma (one breath with 、), wake-pause (800 ms pause then text), nonwake.

## Set
208 cases. Kyoko only, because every ja_JP voice renders as Kyoko through `say -o`. There are 8 prosodies (rate × [[pbas]]), clean plus Gaussian noise at RMS 0.003, and 500 ms of lead-in.
Train uses prosodies p0, p2, p4, p6, continuation sentences s0-s2, and nonwake n0-n3. Test uses p1, p3, p5, p7, s3-s5, and n4-n7.

## Noise
Baseline at 3 reps: 0 of 208 cases change verdict across reps. Only the STT ms jitters (55-95 ms). One case is about 1 % of a split.

## Known ceiling
Apple STT misreads おんせい as お店 or ボブ声 on some prosodies. No segmentation change can recover those.
