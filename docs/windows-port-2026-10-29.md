# Mac → Windows 移植の洗い出し（2026-10-29 時点）

対象は `origin/main`（= `main`、`6c30bf3`）に push 済みで、Windows 側（`dotnet/`）に手が入っていない Mac のコミット `a3519e4`〜`6c30bf3` である。Windows 側の最後の変更は `37256c9`（`PasteMode` を読むだけ）と、その前の `5298a67` だ。判定は作業ツリーのコードを読んで行った。Windows の実機では何も再現していない。

## 判定一覧

| Mac コミット | 内容 | Windows | 根拠（読んだ箇所） |
|---|---|---|---|
| `fc7144b` `a2c5975` | Chrome/Edge がマイクを使う間（Meet）は待ち受けを止め、手放したら再開する。`pauseDuringMeet` で切り替える | **移植可・未実装** | `MicInUse.cs` に `IAudioSessionManager2` の照会がある。トレイは `TrayRuntimeSupervisor` の Start/Pause/Reload と `FollowDevice` で止まり、動き出す |
| `277afc2` | Superwhisper がマイクを使う間に始まった発話からは、自動録音を始めない | **移植可・同じ穴あり** | `DictationRuntime.cs:603-623` は `AutomaticSkipReason` をフレームごとに計算し直す。`phraseStart` は残るので、Superwhisper が放した次のフレームで、発話の頭（preroll 込み）から自動録音が開く |
| `ce14722` | 雑音が続き、発話が区切れない間もストップワードで止める | **移植可・同じ穴あり** | `DictationCore.cs:439,470` で、`StandaloneStopRange` と `TrailingStopRange` は `ClosedUtterance` しか見ない。雑音でしきい値を超え続けると、2.5 s の上限で `PrefixHead` しか来ず、判定が走らない |
| `de4c434` | 語が一致しないまま `maxSeconds` に達した自動録音を部屋の音とみなし、雑音の基準値を上げ直す | **移植可・一部** | `RebaseFloor` の呼び出しは `DictationRuntime.cs:390-393`（待ち受け中の `PrefixHead`）だけ。語の一致がない自動録音を捨てても（`:1168-1171`）基準値は動かない。Windows は未確定の自動録音を HUD に出さない（`PublishPhase`、`:938`）ため、「HUD が消えない」症状は出ない。開き直しが繰り返される点だけが残る |
| `6c30bf3` | ストップワードを聞いた時点で、HUD を処理中にする | **移植可・設計が要る** | Windows は `ClosedUtterance`（ハングオーバー後）で初めてストップを知る。プローブは待ち受け中しか出さない（`:630-636`）。先に出すには、録音中もプローブを出す設計が必要 |
| `37256c9`（pasteMode） | handoff の前に、Superwhisper のモードを毎回 `pasteMode` に指定する | **要調査** | `VoiceSwitchConfig.PasteMode` は読むだけで使っていない。`SuperwhisperHandoff.cs:242-299` にはモードの切り替えと `activeMode` の照合がある。Windows の Superwhisper でも「設定ファイル上のモードと実際に動くモードが食い違う」かは未確認 |
| `37256c9`（HUD フェード） | マイクの印の切り替えを 0.15 s のフェードにし、消すのを 0.2 s 待つ | 任意 | 見た目だけの変更 |
| `a3519e4` | VPIO をやめて生のマイクを開く。内蔵マイクへのフォールバック。メニューを実在の入力だけにする | 移植不要 | CoreAudio と VPIO に固有の変更。Windows は WinMM と `EchoCancelledCapture` を使う。フォールバックは `WinMmCapture.EffectiveDevice` と `FollowDevice` にすでにある |
| `485fdfd` | HAL の音声検出を、区切り・ストップ・録音継続の判定にそろえる | 移植不要 | HAL は macOS 固有 |
| `c327ab5` | HAL の 1.5 s ゲートで止めた 1 文目を保持する。Hearing の HUD | 移植不要 | Windows には 1.5 s ゲートがなく、`RetainStart` が 10 s の発話を保持する。Hearing は parity 文書で Mac 限定と決めている |
| `20b9246` `f51760e` `37dba2d` `2374887` | 検証スキル、計測ツール、テストの修正 | — | 今回の `verify-meet-pause-windows` が Windows 側の対応物 |

## 検証項目

### W1. Meet 中の一時停止（`fc7144b` `a2c5975`）

- 形式検証: `sh .claude/skills/verify-meet-pause-windows/check.sh`。性質は MP-F1〜F3 で、設計の 8 規則は同スキルの SKILL.md にある。対応表は実コードへ付け替え済み（`b7f0c58`）。
- 実機の受け入れ: `.claude/skills/verify-voice-switch/features/meet-pause.md` の MPW0〜MPW8。MPW0（Meet の取り込みが `chrome` / `msedge` の名前で見えるか）を最初に実機で確かめる。
- 実装中のユニットテスト（`VoiceSwitch.Windows.Tests`、偽の `ITrayRuntimeFactory` を使う）:
  - W1-1 Hold が `Error` 状態からも保留を立て、その後に積まれたデバイス再試行の Start が開かない（モデルの壊した版 1 と 3）。
  - W1-2 ユーザーの一時停止で保留が外れ、通話が終わっても再開しない（壊した版 2）。
  - W1-3 通話 A の終わりに積んだ Resume が通話 B の最中に走っても開かない（壊した版 5）。
  - W1-4 `pauseDuringMeet` が無いか false なら、今と同じ動きになる（MPW8）。

### W2. Superwhisper が使っていた間の発話（`277afc2`）

- W2-1（ユニット）: `DictationRuntime` に `micInUseBy` を差し込む。発話の途中で「使用中」から「空き」に変えても、その発話のあいだは `dictation session: automatic` が出ない。
- W2-2（ユニット）: その発話が終わり、`bridge` を超える無音のあとに始まった次の発話では、自動録音が開く。
- W2-3（実機）: Superwhisper のホットキーで話し、放した直後に voice-switch の handoff WAV が増えない。

### W3. 区切れない雑音の中のストップワード（`ce14722`）

- W3-1（WAV）: 一定の雑音（しきい値を超える）の上に「本文 → 入力ストップ」を重ねた fixture を `$V wav` に流し、`standalone-stop` か `trailing` の行で止まる。修正前の Windows では止まらないこと（`maxSeconds` で終わること）も先に記録する。
- W3-2: ストップワードが窓の先頭に来ても、前の文の末尾を削らない（Mac の「2 文字目の時刻から開始位置を見積もる」に当たる）。
- W3-3: 止まらない 4 ケース（`stop-negatives.json`）が、雑音の上でも止まらない。

### W4. 部屋の音とみなした自動録音のあとの基準値（`de4c434`）

- W4-1（WAV）: 一定の雑音だけの fixture で、自動録音が `no agreed words` で捨てられたあとに `dictation vad: floor rebased` が出る。続く 60 s で自動録音が開き直さない。
- W4-2: 普通の声の自動録音は、基準値を上げたあとでも開く（`automatic-wake.md` の既存ケースを回帰に使う）。
- モデル化するなら、`verify-hud-noise` の HudNoise.tla を Windows 用に写す。Windows では HUD が未確定の録音を出さないので、性質は「HUD が消える」ではなく「部屋の音だけでは自動録音が開き続けない」になる。

### W5. ストップを聞いた時点の処理中表示（`6c30bf3`）

- W5-1（ログ）: 録音中に「入力ストップ」と言ってから、`dictation phase: Ended` が出るまでの時間を `stdout.txt` で測る。いまの値を基準にする（ハングオーバー 300 ms と認識時間の分だけ遅れる見込み）。
- W5-2: 仮の Ended が取り消されたら Recording に戻り、貼り付けの条件は変わらない（Mac の `StopVerdict.heard` に当たる）。
- HUD そのものの見え方は Windows のデスクトップが要る（`hud-labels.md` と同じ制約）。

### W6. pasteMode（`37256c9`）

- W6-0（調査）: Windows の Superwhisper で、`activeMode` と実際に動くモードが食い違う場面があるかを先に確かめる。無ければ移植しない。
- W6-1: 移植するなら、起動時とウェイクワードの handoff の前に、`superwhisper mode:` の行で `pasteMode` を指定したことが残り、`ReadActiveMode()` がそのキーを返す。

## 移植の結果（`win-port` ブランチ）

テスト全件は `dotnet run --project dotnet/VoiceSwitch.Windows.Tests`（WSL の Linux 版 dotnet）で 225 PASS、0 FAIL、11 SKIP（Windows 専用）。`check.sh` は `No error has been found.`。

| 項目 | コミット | 確かめたこと | 残り |
|---|---|---|---|
| W1 | `7841de6` | W1-1〜W1-4 と「Error 後の一時停止が再試行で取り消される」のテスト。ガードを外すと W1-1 が落ち、モデルの写しは `HeldMeansOff` 違反になる | MPW0〜MPW8（実機の Meet） |
| W2 | `3f376f0` | W2-1／W2-2 を 1 本のテストで。修正前は FAIL | W2-3（実機の Superwhisper） |
| W3 | `2bfdf80` `90d49aa` | W3-2／W3-3 のテスト（`stop-negatives.json` は 20 件）。W3-1 は exe で、SAPI の口述は雑音の上のストップを「スタッフ」と聞き、止まらない。`dedicatedStopRecognizer: true` なら `end reason=StandaloneStop seconds=2.39` で止まり、雑音だけでは止まらない | 実声・実マイクでの確認。`dedicatedStopRecognizer` は既定オフのまま |
| W4 | `3f46762` | W4-1（雑音 18 s、自動録音の開き直しが 6 回 → 1 回）と W4-2。基準値の上げ直しは破棄の箇所ではなく `Submit` で行う（破棄は別スレッドで、その時点では上げ直せない） | WAV での確認 |
| W5 | `12e085e` | W5-1（プローブで Ended がハングオーバーより先）と W5-2（取り消すと Recording に戻り、渡す音声は同じ）。`earlyWakeMs` があるときだけ効く（`config.example.json` には無い） | 実機での ms 計測 |

レビュー後の修正（`win-port-fix-tray` `win-port-fix-dictation`）で、通話中の起動でマイクが最初のポーリングまで開く穴（M1）を塞ぎ、モデルに `NoAutoStartInCall` を足した。停止語の判定は `DictationBoundaries.FindStop` の `StopVerdict` 1 か所にまとめた。`make` は exit 0、parity `fail=0`。

残した指摘:

- L2: `MicInUse` に揺り戻しの抑えがない。Chrome の取り込みが通話中に一瞬 Inactive になるかは実機で見ないと決められない。
- L5: 録音中のプローブが、ストップを確定する閉じた発話を SAPI の列で待たせうる（`earlyWakeMs` があるときだけ）。W5-1 の実機計測で判断する。
- L6: 語の一致の判定が出る前に maxSeconds で終わった本物の声でも、基準値を上げ直しうる。上げ直しは `Segmenter` が上限超えの間しか効かず、遅らせると何もしなくなるため、直すには一致したら基準値を戻す仕組みが要る。

## 現在のコードで見つかったこと（未再現）

- **ユーザーの一時停止がデバイス再試行で取り消される。**（W1 の規則 4 で修正済み。テスト `tray meet user pause after Error survives the device retry`）Meet とは関係がない。実行中のランタイムがエラーで終わり、`Error` になったのに、メニューはまだ 待ち受け中 を表示している。ユーザーが 一時停止 を押しても、その Pause はゲート待ちになる。次の `FollowDevice` が再試行の Start を積み、Pause のあとに Start が走って待ち受けに戻る。モデルの壊した版 4（今の `StartAsync` と同じ動き）の反例で、窓は狭い。W1 の規則 4 で塞がる。
- **Error 中にマイクを選び直すと停止中のまま再試行が止まる。** `ReloadAsync` は動いていない状態から `Paused` で終わるため、`ShouldRetryStart` が対象にしなくなる。ユーザー操作のあとの動きなので、`HeldResumes` の対象からは外した。
