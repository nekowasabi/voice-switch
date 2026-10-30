---
name: verify-wake-allowances
description: Bounded-exhaustive check (real Core code, every input class) that the Windows wake matcher accepts exactly what main accepted plus the five relaxations of the 2026-10-29 wake stack (particle tail, short copula, exact alternate, rejected reading at distance 0, built-in reading table) and nothing else. Use when a change touches WakeAllowances / ReadingRuleAllows / ReadingWake / MatchAlternate / RejectedReadingMatches / RejectedWake / ShortWakeReading / MinReadingDistance2 / RejectedWakeMinConfidence in dotnet/VoiceSwitch.Windows.Core/DictationCore.cs, WakeWord.From in VoiceSwitchConfig.cs, or WakeReadingFallback.cs, and before merging any wake-relaxation PR.
---

# Verify the wake relaxations add exactly what they claim

The stack #61–#66 (PLAN `plan/2026-10-29-wake/PLAN.md`, slices s1–s5) loosens when ordinary speech counts as a wake word on Windows. The corpus in `../verify-voice-switch-wake` checks 47 hand-picked rows. This check runs the real `VoiceSwitch.Windows.Core` code on every input class of the rule table and on bounded sets of generated utterances, and compares it with main's rule plus the five documented relaxations.

C# has no verifier that runs on this code, so this is not a proof: it is the real code on generated inputs. For P1 the inputs cover every class the predicate can tell apart, so P1 holds for all inputs; P2–P4 hold up to the bounds below.

## Properties

| Name | Plain sentence | Harm if broken |
|---|---|---|
| `P0-thresholds` | `ShortWakeReading` = 4, `MinReadingDistance2` = 10, `RejectedWakeMinConfidence` = 0.2. | Every rule below silently widens; PLAN rejected these loosenings with named ambient false wakes. |
| `P1-reading-rule` | `ReadingRuleAllows` accepts exactly main's guard (2c28b7d) OR s1 (wake reading ≥ 10, closed, distance 2, tail exactly one of は よ ね な わ) OR s2 (wake reading ≤ 4, closed, distance 0, prefix length = wake, one lexeme, tail exactly です or ます). | `音声認識…`, `温泉です`, `法政入力の…` start dictation or an action on ordinary speech; or a wake that used to open stops opening. |
| `P2-rejected-reading` | `RejectedWake` wakes only when the rejected text equals a wake's surface or its reading (distance 0), only for a closed utterance with empty text and confidence ≥ 0.2, and never cuts a body. | A near-homophone SAPI rejected (`おんせ`, `ほんせい`) opens the wait for a body. |
| `P3-alternate` | Alternates never change a result the primary already gives, and add a wake only when the utterance is closed, an alternate's reading equals a wake reading of length ≥ 10, the primary reading is within edit distance 3 of it, and the result is wake-only (`BodyStart` null). Every such shape does wake. | A probe or a prefix head wakes on an n-best guess; a far primary wakes because one alternate said the wake word. |
| `P4-reading-fallback` | `WakeWord.From(word)` with a null or empty reading uses the built-in table only for its seven words; other words keep their own normalized surface; an explicit reading always wins. | A table entry such as `音声認識` makes ordinary speech a wake on the surface-only path. |

## Status

All five pass at the sync commit:

```
PASS P0-thresholds ShortWakeReading=4 MinReadingDistance2=10 RejectedWakeMinConfidence=0.2
PASS P1-reading-rule cases=21660 gained_s1=180 gained_s2=8
PASS P2-rejected-reading cases=224064 wakes=16
PASS P3-alternate cases=178455 gained=2208
PASS P4-reading-fallback words=15
wake-allowances: ok
```

Each property was seen failing on a scratch break of the real code, then the break was reverted and the run was green again:

| Break in the code | Failing property and first line |
|---|---|
| add `"の"` to the s1 `Tail` in `WakeAllowances` | `P1` `size=10 length=9 distance=2 closed=True lexemes=1 tail="の" actual=True expected=False` |
| s2 row `OneLexeme: false` | `P1` `size=1 length=1 distance=0 closed=True lexemes=2 tail="です" actual=True expected=False` |
| `RejectedReading` row `MaxDistance` 0 → 1 | `P2` `rejected="せ" got=wake expected=none` |
| drop the `ClosedUtterance` test in `MatchAlternate` | `P3` `extent=PrefixHead primary="ほほせいにゅうりょく" ... alternate-only wake outside s3` |
| add `["音声認識"] = "おんせいにんしき"` to `WakeReadingFallback` | `P4` `word="音声認識" reading=null got="おんせいにんしき" expected="音声認識"` |

## Run

```sh
sh .claude/skills/verify-wake-allowances/check.sh
```

Uses `dotnet` from `PATH`, else `$HOME/.dotnet/dotnet` (or set `DOTNET=`). .NET 8 SDK (8.0.424 at the sync commit). Core must have been restored once (`dotnet restore dotnet/VoiceSwitch.Windows.Core/VoiceSwitch.Windows.Core.csproj`, needs network the first time). About 10 s with the build. Exit 0 and last line `wake-allowances: ok` is a pass. Each failing property prints up to 5 `FAIL <property> <case>` lines and the last line counts all failures.

## Coverage record

Sync commit: the commit whose subject is `fix(wake): RejectedWake を WakeWord の一覧ひとつで受ける` (find it with `git log -1 --format=%h --grep 'RejectedWake を WakeWord'`). When you move the sync point, replace this line with the new commit's subject. Move it only after the check describes the code at the new commit.

| Check part | Code |
|---|---|
| `P0` | `DictationBoundaries.ShortWakeReading`, `MinReadingDistance2`, `RejectedWakeMinConfidence` (DictationCore.cs), read by reflection at run time |
| `P1` actual | private `DictationBoundaries.ReadingRuleAllows(int size, int length, int distance, bool closed, int lexemeCount, string all)` by reflection; a rename or signature change throws `MissingMethodException` |
| `P1` expected, main part | `MainAllows` in `check/Program.cs`: the inline guard of `ReadingWake` at `2c28b7d` minus `allowFuzzy` (which stays at the call site), with `ShortWakeReading` / `MinReadingDistance2` written as the literals 4 / 10 on purpose, so a constant change fails `P0` instead of moving the baseline |
| `P1` expected, s1 / s2 | PLAN s1 and s2 goals |
| `P2` | public `RejectedWake(recognition, wakes)`; inside, `RejectedReadingMatches` and the `RejectedReading` row. Every caller (`DictationSession`, `DictationRuntime.RecognitionOrigin`, the CLI `Verdict`) passes `config.Wakes()` |
| `P3` | public `LeadingWake(recognition, wakes)` with and without `Alternates`; inside, `MatchAlternate` and the `AlternateNear` row |
| `P4` | `WakeWord.From` (VoiceSwitchConfig.cs) and `WakeReadingFallback.Table` |

Bounds and input classes:

- `P1`: size 1–13, length size−1..size+1 (the only lengths `ReadingWake` passes), distance 0–4, closed both, lexemeCount 1–3, 19 tails (empty, the 5 particles, です, ます, and non-listed の ば が で でし でした はは よね くは ですよ あ). `ReadingRuleAllows` and main's guard read `size` only against 4, 5 and 10, `length` only against `size` and `all.Length`, `distance` only against 0–2, `lexemeCount` only against 1, and the tail only by membership, so these classes are every input the predicate can tell apart.
- `P2`: every rejected string of length 0–5 over お ん せ い セ ほ plus 音声, 明日, 音声入力, `おんせい `, オンセイ; wakes 音声 (reading おんせい) and 明日 (reading せい); extent ×3, text "" / "あ", confidence null / 0.05 / 0.2 / 0.35.
- `P3`: one-lexeme primaries = おんせいにゅうりょく with up to 4 substitutions by ほ / あ, each also with the last kana dropped or は added, plus おんせい おんせん ざつだん あ; 5 alternate sets (each wake reading, 法政入力's reading, katakana of the long wake); extent ×3; wakes 音声入力, 音声, 音声に入る with readings.
- `P4`: the 7 table words plus 明日 音声認識 音声メモ 温泉 入力 おんせい `音 声` ｲﾝﾌﾟｯﾄ.

## Does my change touch this?

From the repo root:

```sh
git log -1 --format=%h --grep 'RejectedWake を WakeWord'
git diff <that sha> -- dotnet/VoiceSwitch.Windows.Core/DictationCore.cs dotnet/VoiceSwitch.Windows.Core/VoiceSwitchConfig.cs dotnet/VoiceSwitch.Windows.Core/WakeReadingFallback.cs
```

Look for `WakeAllowances`, `ReadingRuleAllows`, the `ReadingWake` loop (`size`, `size - 1`, `size + 1`, the call to `ReadingRuleAllows`), `MatchAlternate`, `RejectedReadingMatches`, `RejectedWake`, the three constants, `WakeWord.From`, or the table. Any hit: run the check.

## When it fails

1. Read each `FAIL` line as a case: the wake reading length, the utterance prefix length and tail, the edit distance, closed or not, how many lexemes.
2. If the change meant to accept that case (a new relaxation the user asked for), the code is right and the expectation is stale: add the new shape to `expected` in `check/Program.cs` as its own named term (like `s1`, `s2`), add it to PLAN, break the new rule once to see the check fail, revert, run green, and move the sync commit. Never widen `MainAllows`; it is main's guard.
3. If nobody asked for that case, it is a real false wake (or a lost wake): report it with the case and the harm. Do not edit the check to pass.
4. `MissingMethodException`: `ReadingRuleAllows` was renamed or its parameters changed. Update the reflection lookup to the new member, then go to step 1. On a branch without the stack (main before #62–#66 land), `WakeAllowances` does not exist at all: that is the wrong branch, not a bug.

When the table itself grows, keep `P1` exhaustive: a row with a length threshold above 13 needs the `size` range raised past it, and a new `Remainder` kind or `Tail` set needs its members and a non-member added to `tails`. Without that, a pass no longer covers every input.

## Not covered

- Real audio, SAPI, Mac (Swift has none of these relaxations), Windows hardware.
- `LeadingWake`'s exact and fused text splitting, the prefix-gap rule, `EarlyWake`, and `ReadingWake`'s choice among size−1 / size / size+1 and its proportional split of the body start.
- `P2`–`P4` beyond their bounds: longer strings, other alphabets, other wake lists.
- Whether each intermediate slice (#62–#65) holds on its own: at those commits the later relaxations are absent, so P1/P2/P3/P4 for later slices are expected to fail there.
