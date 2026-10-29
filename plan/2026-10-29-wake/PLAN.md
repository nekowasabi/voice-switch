# PLAN: wake recognition rate slices

Yardstick for every slice: `plan/2026-10-29-wake/SPEC.md` item 6, corpus sha256 `97fbaf6f615971d59eb0b509560c4939027d7b813a24f9eaf46765bacc8da593`. The corpus and the scorer already live in `.claude/skills/verify-voice-switch-wake/corpus/` on this docs commit, so later slices have a fixed file. Do not edit `corpus.json`, the sha constant, or the printed floor numbers (`hits>=15`, `misses<=8`, `hits>=7`, `misses<=16`). A slice may change matcher code and, where a slice says so, the scorer's inputs.

Item 6 names the row ids that are already hits. A slice that moves a row from `miss` to `hit` adds that id to `must_hit` for that rule in the same commit. Do not remove an id. Totals staying above the floor are not enough: a swap of one old hit and one old miss fails, and giving a listed hit back fails. `plan/2026-10-29-wake/SPEC.md` is part of that slice's files when the list changes.

Stack on this commit, one commit per slice, in the order s1 → s6. Branch names below are the slice branches. Do not merge, push, or open PRs. Each slice stays green on SPEC items 1–10. Acceptance beyond that is the row move listed for that slice: configured or surface `miss` → `hit`, and `false_wakes` stays 0. Rows not listed stay put against this base. "Against this base" means the docs commit, not the combination of earlier slices. Stacking can flip more rows; the slice is accepted when its own listed rows move and its guards do not.

None of these slices need the tray HUD, shortcuts, or log redaction. Leave those files alone.

Measured configured misses today (`expect=wake`, `got=no-wake`): `hosei-wa`, `hosei-ne`, `hosei-yo`, `houzei`, `houzei-alt`, `rejected-kana`, `onsei-desu`, `onsei-masu`.

Surface-only misses that the configured rule already hits: `katakana-fold`, `halfwidth-kana`, `hosei-d2`, `hosei-spaced`, `hosei-short-reading`, `yoon-large`, `choon`, `onsen-ni-hairu`. Those eight are the no-reading lexicon gap. `WakeWord.From(text)` keeps the surface as the reading, and off Windows `ImeReadings` returns null.

Already on main, do not redo: H1 timing warnings; H2d distance 2 only for reading length ≥ 10, closed, whole utterance; H4–H8 replay and near-miss measurement; Windows early-wake v3 (early wake plus a prefix gap). A `、` lexeme is not a pause on Windows. `relaxedReadingDistance2` stays opt-in and off. Mac latency v4 (50 ms pause) was reverted. Overnight long whole-utterance distance-3 misses were empty (H7); the fixture's `法政入力` row is synthetic.

## s1 — particle tail

- Branch: `improve/wake-particle-tail`
- Effort: #high
- Goal: a long closed wake (reading length ≥ `MinReadingDistance2`) at distance ≤ 2 may keep a one-kana tail that is exactly `は`, `よ`, `ね`, `な`, or `わ`. Not `の`. Not a content word.
- Why it misses today: `ほうせいにゅうりょくは` is distance 3 against the full string, and the distance-2 prefix is rejected because distance 2 must cover the whole utterance.
- Files: `dotnet/VoiceSwitch.Windows.Core/DictationCore.cs` (`ReadingWake`), `dotnet/VoiceSwitch.Windows.Tests/Program.cs`, `plan/2026-10-29-wake/SPEC.md` (add the three ids below to configured `must_hit` only).
- Acceptance: SPEC items 1–10. Configured rows `hosei-wa`, `hosei-ne`, `hosei-yo` move `miss` → `hit` and are added to configured `must_hit`. Guards that stay `no-wake` on both rules: `hosei-no`, `hosei-ashita`, `hosei-honbun`, `ninshiki-fused`, `ninshiki-split`. Surface `hosei-wa`, `hosei-ne`, `hosei-yo` stay `miss` against this base (the surface reading is still the kanji). Item 8 and item 10 census strings stay; the sample log has no particle-only tail.
- Risk: the particle set is the whole change. A tail that is not in that set must still miss, or `音声認識` comes back.
- Not in this slice: lowering `MinReadingDistance2`, treating `、` as a pause, Swift.

## s2 — short copula

- Branch: `improve/wake-short-copula`
- Effort: #high
- Goal: a short wake (reading length ≤ `ShortWakeReading`) may match when the reading distance is 0 on a prefix whose length equals the wake, and the unmatched tail is exactly `です` or `ます`. Not distance ≤ 1. Not a one-kana tail (those already match at size+1 for a non-short rule, and short wakes stay whole-utterance except for this tail).
- Files: `dotnet/VoiceSwitch.Windows.Core/DictationCore.cs` (`ReadingWake`), `dotnet/VoiceSwitch.Windows.Tests/Program.cs`, `plan/2026-10-29-wake/SPEC.md` (configured `must_hit` only).
- Acceptance: SPEC items 1–10. Configured rows `onsei-desu` (text `音声です`, reading `おんせいです`) and `onsei-masu` (text `音声ます`, reading `おんせいます`) move `miss` → `hit` and are added to configured `must_hit`. Surface stays `miss` against this base, because the surface reading of `音声` is the kanji, not `おんせい`. Guards that stay `no-wake`: `onsen-desu`, `onsen-wa`, `onsei-memo`, `ninshiki-fused`, `ninshiki-split`. The rows are one lexeme. Adjacent lexemes `音声` + `です` with no gap stay a non-wake; do not undo the prefix-gap rule.
- Risk: distance ≤ 1 on a short wake would accept `おんせんです` / `温泉は`. The tail has to be exact and the wake span has to be distance 0.
- Not in this slice: kana string `おんせいです` as a new row. That string already hits by fused split, because no longer wake text starts with `おんせい`.

## s3 — exact alternate inside a near miss

- Branch: `improve/wake-exact-alternate`
- Effort: #high
- Goal: if `LeadingWake` misses, and one alternate's reading equals a wake reading of length ≥ 10, and the primary whole-utterance reading is raw distance ≤ 3 from that same wake, accept wake-only (`BodyStart` null). Not on `EarlyWake` probes. Not for short wakes. An alternate alone, with a far primary, does not wake.
- Files: `dotnet/VoiceSwitch.Windows.Core/DictationCore.cs` (`LeadingWake`), `dotnet/VoiceSwitch.Windows.Tests/Program.cs`, `plan/2026-10-29-wake/SPEC.md` (configured `must_hit` only).
- Acceptance: SPEC items 1–10. Configured row `houzei-alt` moves `miss` → `hit` and is added to configured `must_hit`. `houzei` (no alternate) stays `miss`. `zatsudan-alt` stays `no-wake`. Surface `houzei-alt` stays `miss` against this base. Item 8 stays, because sample id 2 already hits on the primary and id 11 has no exact-wake alternate.
- Risk: n-best inside the distance-3 band is trusted. A phrase such as `ぼうぜいにゅうりょく` with an exact alternate would also wake. Do not add a `no-wake` label on that shape unless the rule grows a further guard.
- Not in this slice: enabling distance 3 with no alternate.

## s4 — rejected hypothesis, distance 0 on the reading

- Branch: `improve/wake-rejected-reading`
- Effort: #high
- Goal: `RejectedWake` still requires a closed utterance, empty accepted text, and confidence ≥ `RejectedWakeMinConfidence`. It also accepts when the normalized rejected text equals a wake reading at distance 0. It does not accept distance ≥ 1. It still never cuts a body.
- Files: `dotnet/VoiceSwitch.Windows.Core/DictationCore.cs` (`RejectedWake`), `.claude/skills/verify-voice-switch-wake/corpus/Program.cs`, `dotnet/VoiceSwitch.Windows.Tests/Program.cs`, `plan/2026-10-29-wake/SPEC.md` (configured `must_hit` only).
- Scorer change in this same commit: the rejected channel passes the wake readings (`WakeWord.Reading`), not only the surface strings. Today both rules call `RejectedWake` with the surface texts, so a matcher-only change cannot move `rejected-kana`. Do not change `corpus.json`.
- Acceptance: SPEC items 1–10, including the same corpus sha and the same floor lines. Configured row `rejected-kana` (`rejectedText` `おんせいにゅうりょく`, confidence 0.35) moves `miss` → `hit` and is added to configured `must_hit`. These stay `no-wake`: `rejected-low` (confidence 0.05), `rejected-longer`, `rejected-prefix`, `rejected-kana-hosei` (`ほうせいにゅうりょく`, distance ≠ 0). Surface `rejected-kana` stays `miss` against this base, because the surface reading is still the kanji until s5.
- Risk: a distance-1 rejected reading would open waits on ordinary near-homophones. Distance 0 only.

## s5 — built-in reading when IME did not supply one

- Branch: `improve/wake-reading-fallback`
- Effort: #high
- Goal: `WakeWord.From(word, reading)` keeps a non-empty explicit reading. When `reading` is null or empty, fill a static Core table for the built-in example vocabulary, otherwise keep today's fallback (the surface word). The table is `音声入力` → `おんせいにゅうりょく`, `音声` → `おんせい`, `音声入る` → `おんせいはいる`, `音声に入る` → `おんせいにはいる`, `音声に入るよ` → `おんせいにはいるよ`, `音声によって` → `おんせいによって`, `インプット` → `いんぷっと`.
- Files: a new file under `dotnet/VoiceSwitch.Windows.Core/` (the SDK compiles it; do not edit the test csproj for a Core file), `VoiceSwitchConfig.cs` (`WakeWord.From` only), `dotnet/VoiceSwitch.Windows.Tests/Program.cs` (`ConfigWakeWordsCarryReadings`), `plan/2026-10-29-wake/SPEC.md` (surface `must_hit` only). Unknown words, such as `明日`, still stand in as themselves. On Windows a non-null IME reading still wins, because the runtime passes it in. Do not change `RecognitionKey`. Do not put the table only in `DictationRuntime`; the corpus surface rule calls `WakeWord.From(text)` and would not see a runtime-only table.
- Acceptance: SPEC items 1–10. Against this base, surface rows move `miss` → `hit` and are added to surface `must_hit`: `katakana-fold`, `halfwidth-kana`, `hosei-d2`, `hosei-spaced`, `hosei-short-reading`, `yoon-large`, `choon`, `onsen-ni-hairu`. Configured totals for those eight stay hits (they already match). Guards that stay `no-wake` on the surface rule: `ninshiki-fused`, `ninshiki-split`, `ninpu`, `inku`, `onsen-wa`, `anzen` (reading `あんぜんにゅうりょく`), `bouzei` (reading `ぼうぜいにゅうりょく`). Surface `hosei-wa`, `hosei-ne`, `hosei-yo` stay `miss` against this base; they need s1 as well.
- Risk: a table that maps anything outside that vocabulary can false-wake the surface rule. Keep the table closed. This slice does not help a Windows machine whose IME already returned those readings. It is the gap when the reading argument is empty.

## s6 — long distance-3, only with a guard that holds

- Branch: `improve/wake-long-d3-suffix`
- Effort: #xhigh
- Goal: whole-utterance `法政入力` (reading `ほうぜいにゅうりょく`, raw distance 3 from `おんせいにゅうりょく`) becomes a configured hit, and `安全入力` (`あんぜんにゅうりょく`) and `防衛入力` (`ぼうぜいにゅうりょく`) stay `no-wake`.
- Files: `dotnet/VoiceSwitch.Windows.Core/DictationCore.cs`, `dotnet/VoiceSwitch.Windows.Tests/Program.cs`, and, only if the rule lands, `dotnet/VoiceSwitch.WakeReplay/` self-test expects, `.claude/skills/verify-voice-switch-wake/verify.sh` near-miss strings, and SPEC item 10's string list. Same commit. `corpus.json` stays.
- Acceptance: SPEC items 1–9 stay green, and item 6 gains configured `houzei` in `must_hit`. Item 10's whole-field strings change only as the census changes (`(shown 1,` on the distance-3 header goes away, and `gained=1 lost=0` changes with the new gained count). Configured `houzei` moves `miss` → `hit`. `anzen` and `bouzei` stay `no-wake`. `あんぜいにゅうりょく` is already a configured hit at distance 2; it is not a guard. Do not lower `MinReadingDistance2`. Do not turn on raw distance 3. Do not loosen `ShortWakeReading`.
- Risk: the shared suffix `にゅうりょく` also fits `ぼうぜいにゅうりょく`. Dropping one dakuten from the cost makes both `法政` and a true distance-2 neighbor hit. H7's overnight whole-utterance long distance-3 miss list was empty, so this row is synthetic. Abandon the slice if `anzen` and `bouzei` cannot both stay `no-wake`. Do not ship a special case whose only effect is the fixture string `法政`.

## Dropped

- Comma-as-pause. `おんせいは` already hits. Treating `、` as a pause reopens `音声認識`.
- Loosening `ShortWakeReading`, lowering `MinReadingDistance2`, or defaulting `relaxedReadingDistance2`. Those were rejected with named ambient false wakes.
- Raw distance 3 with no further guard (see s6).
- Dakuten cost 0.
- VAD or segmenter changes. The corpus is already text.
- A Swift port of `ReadingWake`. Parity does not require it, and Mac cannot be built here.
- Redoing merged H1–H8 or the Windows v3 early-wake port.
