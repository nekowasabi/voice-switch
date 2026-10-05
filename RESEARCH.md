# 数時間放置するとウェイクワードに反応しなくなる件の調査

調査日: 2026-10-05。Windows は実機のログで計測済み、Mac は未計測。この文書は Mac 側で確認を進めるためのもの。

## 結論

- Windows では、USB マイク（Yeti Nano）が一瞬消えて戻ったあとの再起動が 1 回だけ試されていた。その 1 回が失敗するとエラー状態のまま止まり、マイクがもう一度抜き差しされるまで二度と試していなかった。エラーはログに出ていなかったので、ログは黙っていた。修正済み（コミット済み `8fbfc49`、実機には未導入）。
- VAD・認識・バッファの詰まりは、Windows のログでは原因ではなかった。
- Mac にも同じ形の欠陥が疑われる。仮説 1（再起動再試行）・仮説 2（transcribe 期限）・仮説 3（RebaseFloor）はコードに反映済み。いずれも **Mac 実機ランタイムは未検証**（計測も未実施）。

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

### 修正（コミット済み `8fbfc49`）

- `TrayRuntimeSupervisor.ShouldRetryStart`: エラー状態でマイクが見えている間は、10 秒ごとに Start を再試行する。
- `TrayRuntimeSupervisor.SetSnapshot`: エラーや `Finished` になったら、理由を `tray: Error: …` の形で 1 行ログに出す。同じエラーが続くときは最初の 1 行だけ。
- テスト `tray retries start while in error with a microphone present` を追加した。修正前は FAIL、修正後は PASS。`make win-test` の結果は `SUMMARY pass=80 allow=20 fail=0`。
- **実機では確認していない。** 新しい exe を入れて、Yeti を 15 秒抜いて戻し、`dictation capture:` がまた出ることを確かめる必要がある。

## Mac で確認すること

### 疑っている箇所（仮説）

1. **再起動が 1 回失敗すると、二度と試さない（Windows と同じ形）。** `MacApp.swift:300-305` では、`AVAudioEngineConfigurationChange` を受けると `stop()` してから `start()` する。`start()` が投げると `restart failed` がログに出て、`running` は false のまま残る。observer は `guard … self.running` で始まるので、以後の通知はすべて無視され、二度と再起動しない。
2. **`transcribe` がハングすると止まる。** かつては `transcribe`（SpeechAnalyzer）に期限が無く、consume ループが直列に待つため 1 回でも戻らないとループが止まった。**コード上の修正済み**（`transcribeDeadlineSeconds` / `TranscribeDeadline`、期限 `max(10, audioSeconds + 20)`、タイムアウトは既存の `transcribe failed` 経路）。**Mac 実機ランタイムは未検証**（この box では Apple Speech を実行できない）。
3. **定常音で VAD が発話中のまま抜けない。** かつては Mac の `Segmenter.swift` は無音のときにしか floor が追従せず、閾値を超える定常音で `skipping` から抜けられなかった。**コード上の修正済み**（Windows `RebaseFloor` を `rebaseFloor` / `rebaseFloorAfterNoWake` として移植）。**Mac 実機ランタイムは未検証**。

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
- 仮説 2: **コード反映済み。** Mac `transcribe` に Windows と同じ式の期限（`max(10, 秒数 + 20)` 秒 = `transcribeDeadlineSeconds` / `TranscribeDeadline`）を付け、超えたら既存の `transcribe failed` の経路へ流す。box では Apple Speech を実行できないため **Mac 実機ランタイムは未検証**。
- 仮説 3: **コード反映済み。** Windows の `RebaseFloor` を Mac `Segmenter.rebaseFloor` に移植し、over-cap head で wake が無いときに呼ぶ。**Mac 実機ランタイムは未検証**。

## 最新研究・OSS との比較

調査日: 2026-10-05。方法は WebSearch、Google Scholar 検索、Genspark の deep research。論文は要旨まで、OSS は README までしか読んでいない。数値はどれも各配布元の自己申告で、こちらの環境では測っていない。

### 結論

- Mac 仮説 1〜3 を解く研究は見つからなかった。ASR のハング対策とスリープ復帰後の再起動を扱う 2025〜2026 年の文献は無かった。耐障害性は実装で直す領域で、上の「仮説ごとの直し方の案」をそのまま使う。
- Windows は `RebaseFloor`、`transcribe` の期限（`DictationRuntime.cs:1418`）、10 秒ごとの再試行（`8fbfc49`）が入っており、研究から足すものは下の 2 点だけ。

### 取り込む価値がある

| 項目 | 内容 | 限界 |
|---|---|---|
| onset 遅延の計測 | [S4VAD](https://arxiv.org/abs/2609.11110) の、ラベルのずれを含めて onset 遅延の分布を推定する評価法。現状の検証は合格率だけで、遅延分布は未計測。合成 WAV 検証（`--input-wav`）に足せる。 | モデルは入れない。測定だけ借りる。 |
| ウェイクワード検出の実測 | [LiveKit Wakeword](https://github.com/livekit/livekit-wakeword)（Apache 2.0、Swift パッケージあり、学習対象 30 言語に日本語）。誤検知 8.50 → 0.08 回/時は、同社の "hey livekit" 検証セット（25 時間）での openWakeWord 比。 | README に「多言語モデルは英語モデルより精度が低い」と明記。日本語の実測が無く、C# の公式バインディングは未確認。置換は決めず、現行の完全一致方式と同じ fixture で比べるところまで。 |

### 見送り

| 項目 | 理由 |
|---|---|
| VAD の置換（[Earshot](https://github.com/pykeio/earshot)、[TEN VAD](https://huggingface.co/TEN-framework/ten-vad)、Silero、[kiloVAD](https://arxiv.org/abs/2607.25870)、Cobra） | 問題は VAD の精度でなく floor の追従。Mac は `RebaseFloor` 移植済み（実機未検証）。Windows のログでも VAD は原因でなかった。速度の数値は自己申告。 |
| [Foreground VAD](https://arxiv.org/abs/2609.19856) | 背景話者で終端が遅れる実害を観測していない。 |
| 終話判定（LiveKit Turn Detector、[Endpoint Anticipation](https://arxiv.org/abs/2606.13450)、[Next-Turn](https://arxiv.org/abs/2606.18094)） | 会話エージェント向け。本アプリは停止語と superwhisper の記録ショートカットでも終了でき、固定無音（`endSilenceMs`）で困った実例が無い。 |
| 専用ウェイクワードによる完全一致の置換 | 完全一致は誤作動を抑える設計上の利点でもある。日本語の学習と評価の運用コストが新たに要る。 |
| ストリーミング ASR の置換（Moonshine JA、WhisperKit、Vosk、Parakeet、Kyutai） | 精度の比較データが無い。Parakeet と Kyutai は英語のみ、または GPU 前提。 |
| Genspark の「AVAudioEngine は仮説 1 と一致する」 | 出典は 2021 年のブログで、`mainMixerNode` のクラッシュと Aggregate device の話。構成変更後に再起動が 1 回失敗すると二度と試さない問題とは別物。 |

### 確認できなかったもの

- Mac のスリープ復帰後のハングに関する 2025〜2026 年の情報。
- AssemblyAI の終話判定の仕様と、Kodama-ja-streaming-small の性能。
- Endpoint Anticipation と Next-Turn の実用ライブラリ。

## 検討して外した案

ateam の idea チーム（run_id `482e8e68048c`）で 9 案を採点し、次の案を外した。

- **リングバッファ**: preroll の ring と `maxSeconds` での打ち切りが既にある。
- **VAD の強制リセット**: Windows には既にある。Mac は仮説 3 で扱う。
- **無音スキップ**: 無音のフレームは既に STT に渡っていない。
- **ストリームの定期的な開き直し**: Windows のログでは、キャプチャが途中で黙ることは起きていなかった。
- **省電力の回避設定**: マイクが 22 時間で 3 回消えたのは USB の省電力（selective suspend）かもしれない。ただし直す場所はアプリの復帰処理にした。
