# 数時間放置するとウェイクワードに反応しなくなる件の調査

調査日: 2026-10-05。Windows は実機のログで計測済み、Mac は未計測。この文書は Mac 側で確認を進めるためのもの。

## 結論

- Windows では、USB マイク（Yeti Nano）が一瞬消えて戻ったあとの再起動が 1 回だけ試されていた。その 1 回が失敗するとエラー状態のまま止まり、マイクがもう一度抜き差しされるまで二度と試していなかった。エラーはログに出ていなかったので、ログは黙っていた。修正済み（未コミット、実機には未導入）。
- VAD・認識・バッファの詰まりは、Windows のログでは原因ではなかった。
- Mac にも同じ形の欠陥が疑われる。ただし計測していないので、修正はしていない。

## Windows の計測結果

対象は `%LOCALAPPDATA%\voice-switch\voice-switch.log`（PID 7708、2026-10-04 22:26 から約 22 時間）。

### 止まっていた区間

| マイクが消えた時刻 | 戻ったとの記録 | 次に `dictation capture:` が出た時刻 | 止まっていた時間 | 区間内のログ行数 |
|---|---|---|---|---|
| 10-04 21:29:58（別デバイス ID に変化） | なし | 21:40:26 | 10 分 | 0 |
| 10-05 11:58:57（`to none`） | 11:59:07 `is available; starting` | 12:29:59（もう一度 `is available` が出たあと） | 31 分 | 0 |
| 10-05 17:35:09（`to none`） | 17:35:19 `is available; starting` | 17:48:11（直前に tray の行が無いので、手動で再開したと思われる） | 13 分 | 0 |

- 同じようにマイクが消えても、4 秒で戻ったとき（10-04 の 20:48 と 21:29:08）は、すぐ `dictation capture:` が出て復帰していた。
- 止まった 2 回（11:59 と 17:35）は、戻るまでに 10 秒かかっていた。

### 原因でなかったもの

- **VAD の固着ではない。** 一日中 VAD は約 800 回/時の発話開始を検出していた。`floor rebased` は 63 回、`cap reached` は 82 回出ていて、定常音からも回復している。
- **認識の詰まりではない。** STT は終日 100〜400ms で返っていた。`recognition failed` は 10-04 の JSON エラー 1 件だけで、今回とは関係ない。
- **バッファの肥大ではない。** 止まった区間ではログが 0 行で、キャプチャ自体が開かれていなかった。

### 仕組み

- `VoiceSwitchTrayContext.cs` の `FollowDevice` は 2 秒ごとにデバイスを確認する。マイクが消えると Reload し、エラー状態になる。
- エラーから Start へ移る条件は「デバイスが変わった瞬間」だけだった。その Start が失敗すると、次にデバイスが変わるまで誰も再試行しない。
- `TrayRuntimeSupervisor` がエラーや `Finished` になる経路は、どれもログを出していなかった。

### まだ分かっていないこと

最初の Start が失敗した理由。ログにエラーが残っていないため。候補は次の 3 つ（`TrayRuntimeSupervisor.cs`）。

1. `StopCurrentRunAsync` が `TimeoutException` を投げ、`run` が残る。
2. そのあとの `StartAsync` が「runtime is still stopping」で断る。
3. `factory.StartAsync` が失敗する（マイクは一覧に出ているが、まだ開けない）。

戻るまでの時間から見て 3 が有力。修正後はエラーがログに残るので、次に起きたときに確定できる。

### 修正（未コミット）

- `TrayRuntimeSupervisor.ShouldRetryStart`: エラー状態でマイクが見えている間は、10 秒ごとに Start を再試行する。
- `TrayRuntimeSupervisor.SetSnapshot`: エラーや `Finished` になったら、理由を `tray: Error: …` の形で 1 行ログに出す。同じエラーが続くときは最初の 1 行だけ。
- テスト `tray retries start while in error with a microphone present` を追加した。修正前は FAIL、修正後は PASS。`make win-test` の結果は `SUMMARY pass=80 allow=20 fail=0`。
- **実機では確認していない。** 新しい exe を入れて、Yeti を 15 秒抜いて戻し、`dictation capture:` がまた出ることを確かめる必要がある。

## Mac で確認すること

### 疑っている箇所（仮説）

1. **再起動が 1 回失敗すると、二度と試さない（Windows と同じ形）。** `MacApp.swift:300-305` では、`AVAudioEngineConfigurationChange` を受けると `stop()` してから `start()` する。`start()` が投げると `restart failed` がログに出て、`running` は false のまま残る。observer は `guard … self.running` で始まるので、以後の通知はすべて無視され、二度と再起動しない。
2. **`transcribe` がハングすると止まる。** `MacApp.swift:37-64` の `transcribe`（SpeechAnalyzer）には期限が無い。consume ループ（`MacApp.swift:445`）はこれを直列に待つので、1 回でも戻らないとループが止まる。その間も上限なしの AsyncStream（`MacApp.swift:296`）に音声が約 64KB/秒（約 230MB/時）溜まり続け、ログは何も出ない。
3. **定常音で VAD が発話中のまま抜けない。** Mac の `Segmenter.swift` は、無音のときにしか floor が追従しない（`:34`）。閾値を超える定常音が続くと `skipping` から抜けられない。Windows には救済（`RebaseFloor`、`Segmenter.cs:38-44`）があるが、Mac には無い。

### 止まったときに見るもの

対象は `~/Library/Logs/voice-switch.log`。

| 見るもの | 意味 |
|---|---|
| `audio configuration changed, restarting input` の直後に `restart failed: …` がある | 仮説 1 |
| 止まった時刻の前後にデバイスの抜き差しやスリープからの復帰がある | 仮説 1 の引き金 |
| 最後の `heard:` のあとに何も出ず、プロセスのメモリ（RSS）が時間とともに増えている | 仮説 2 |
| 最後の行が長い `utt`（2800ms 以上）の `heard:` で、そのあと何も出ない | 仮説 3 |
| `heard:` は出続けているのに `-> wake` が付かない | どれでもない。ウェイクの一致（正規化後の完全一致）を調べる |

あわせて取るとよいもの:

- 止まっている間のメモリの推移: `ps -o rss= -p $(pgrep voice-switch)` を数分おきに実行する。
- `restart failed` と `transcribe failed` の件数と時刻: `grep -nE "restart failed|audio configuration changed|transcribe failed|listening on" ~/Library/Logs/voice-switch.log | tail -50`
- 最後の `heard:` の時刻: `grep -n "heard:" ~/Library/Logs/voice-switch.log | tail -5`

### 仮説ごとの直し方の案（計測で確定してから入れる）

- 仮説 1: `start()` が失敗したら、Windows と同じく間隔を空けて再試行する。observer の `running` ガードで再試行が潰れないようにする。
- 仮説 2: `transcribe` に Windows と同じ式の期限（`max(10, 秒数 + 20)` 秒）を付け、超えたら既存の `transcribe failed` の経路へ流す。
- 仮説 3: Windows の `RebaseFloor` を移植する。

## 検討して外した案

ateam の idea チーム（run_id `482e8e68048c`）で 9 案を採点し、次の案を外した。

- **リングバッファ**: preroll の ring と `maxSeconds` での打ち切りが既にある。
- **VAD の強制リセット**: Windows には既にある。Mac は仮説 3 で扱う。
- **無音スキップ**: 無音のフレームは既に STT に渡っていない。
- **ストリームの定期的な開き直し**: Windows のログでは、キャプチャが途中で黙ることは起きていなかった。
- **省電力の回避設定**: マイクが 22 時間で 3 回消えたのは USB の省電力（selective suspend）かもしれない。ただし直す場所はアプリの復帰処理にした。
