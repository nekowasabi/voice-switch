# 作業引き継ぎ（2026-10-05）

## 目的

音声入力で「〈pane 名〉に〜して」と話すと、superwhisper の文字起こし結果が WSL / Mac の tmux pane に届くようにする。pane 名の聞き間違いは Jev（TypeSafe）で補正する。Jev に許すのは、拒否・絞り込み・提案の三つだけ。Jev が送り先を新しく作ることはない。

## ブランチと状態

- ブランチは `feat/pane-route-jev`（先端 `0225c99`）。Overnight A–D / B2 に加え、本文 F1–F3（candidates / resolve / route+HTTP）まで入り。SendFailed paste は `RouteResult.Body`（send-keys に渡した本文）を使う。
- PR #4（`feat/tmux-pane-route`）は触っていない。PR #4 は 63 コミット古い main から分かれていて、main と衝突している。このブランチで新しく PR を作り、PR #4 を閉じる想定。
- `b612216`（RESEARCH.md の研究比較）は別の話題の変更。PR を作る前に別ブランチへ分けるか決める。

| コミット | 内容 |
|---|---|
| `4c6f359` | PR #4 の pane 振り分けを main の Swift 構成へ移植（`Sources/voice-switch/PaneRoute.swift`） |
| `f5ef52f` | Mac に Jev 補正層を追加 |
| `6a41bc5` `ea8455c` | Windows Core に純粋ロジック（`PaneRoute.cs`）、Windows に `TmuxPaneRouter.cs` |
| `4c6a35d` `1f8f78a` | README と、判定表を共有する fixture `tests/parity/fixtures/pane_route.json` |
| `2598cd3` | `PC=wsl make` の配置先を `C:\takeda\tools\voice-switch` に変更（focusbm と同じ方式） |
| `f254ea0` | Windows から WSL の tmux を、稼働中サーバーと同じ実行ファイルとソケットで呼ぶ |

## 判定の表（Mac と Windows で共通）

| 名前の一致 | Jev の役割（confidence 0.8 以上のときだけ） | 送信 |
|---|---|---|
| 1件 | 別の pane か「該当なし」なら拒否 | それ以外は送る |
| 2件以上 | 一致した候補の中から絞り込み | 選ばれたときだけ送る |
| 0件 | 提案（ログに `jev suggests %N` と出すだけ） | 送らない |

Jev が使えないとき（キーなし、5 秒のタイムアウト、HTTP エラー、読み取り失敗）は PR #4 と同じ動作になる。0.8 という閾値の根拠は、手で作った 8 件だけ（`scripts/pane_jev_probe.py`）。

## Windows 実機で確認できたこと

- 「ボイススイッチの Claude Code の pane に『ハローワールド』を送信して」が pane `%2` に届いた。ログは `tmux: hits=1 jev=off -> send %2 (unique hit)`。
- HUD が出なかったのは、古い設定（`dictation` ブロックなし）のまま exe を起動したため。設定を新しくして起動し直したら直った。音声入力モードにするかどうかは起動時にしか決まらない。
- `wsl.exe -e` からは `TMUX_TMPDIR` が見えない。さらに PATH で古い `/usr/bin/tmux` 3.4 が選ばれ、稼働中の nix の tmux 3.6a と版が合わない。この二つは `f254ea0` で直した。

## 未解決（優先順）

### 1. 指示の部分まで送られる — **対応済み（Slice A）**

`extractSendBody` / `ExtractSendBody` が、最初の空でない `「…」`、なければ `『…』` の中身だけを返す。照合と Jev は全文のまま。send-keys は切り出した本文だけ。切り出し失敗時は全文を送らず、`tmux: … -> skip (no send body for %N; not sending)` で終える。fixture `pane_route.json` の `extract` と Windows / parity / `pane_route_proof.py` で確認済み。 鉤括弧なしは F1 `sendBodyCandidates`（`231c171`）と F2 `resolveSendBody` / body Choice parse・request（`7350cb2`）まで純関数＋fixture 済み。 route は Mac `routeDictation` と Win `TmuxPaneRouter` で body Jev まで配線済み。候補の右端 particle や mid-token label 食い込みは既知のヒューリスティック限界（quote 優先で緩和）。

### 2. 二重表示 — Mac / Windows ともソース配線済み（実機の mode 切替・paste は未検証）

superwhisper の自動ペーストと pane への送信の両方が届き、同じ文が二か所に出る問題。

- ユーザー作成の専用 mode: key `new-mode-1`、表示名 `voice_switch`、`autoPaste: false`。
- `dictation.superwhisperMode`（表示名か key）。未設定は従来どおり。設定時は handoff 前に mode 切替・後に復帰。pane に送れたら終わり。送れなければ voice-switch がクリップボード経由で一度ペースト。
- **本文なし（pane は当たったが `ExtractSendBody` / `extractSendBody` が null）は paste しない**（`RouteDisposition.SkippedNoBody` / `.skippedNoBody`）。全文フォールバック禁止。
- **SendFailed（mode 設定時）**: paste するのは send-keys に渡したのと同じ解決済み本文だけ（`RouteResult.Body` / `.body`。鉤括弧でも body Jev でも同じ。handoff で `ExtractSendBody` / `extractSendBody` を再実行しない）。mode 未設定時は従来どおり Superwhisper が全文を自動ペーストしうる。
- Mac: `SuperwhisperModes.swift` + `MacApp.handoff` に配線済み。mode パスは `~/Documents/superwhisper/{preferences.json,modes}`。**Mac 実機の mode 切替・paste は未検証**（この box に swiftc / Apple Speech なし）。
- **mode 切替の 3 s poll が失敗したら `modeRequested` は false**（Mac `enterSuperwhisperMode` / Windows `EnterModeAsync`）。Decide は paste せず Superwhisper / unset 経路のまま（autoPaste mode 残留での二重配信を防ぐ）。
- Windows 実機での mode 切替・paste の再確認は未実施（box のみ）。

### 3. Jev が `jev=off` のまま

- Windows のユーザー環境変数 `TYPESAFE_API_KEY` は登録済み。
- 21:43 のログでも `jev=off` だった。動いている exe が、キーのない環境から起動されたままの可能性が高い。
- 対処は、トレイから終了して、エクスプローラーかスタートメニューから起動し直すこと。make から起動するなら `WSLENV=TYPESAFE_API_KEY PC=wsl make` とする。`WSLENV` を付ければ `Start-Process` の子プロセスまでキーが届くことは確認済み。

### 4. RESEARCH hyp 2/3（Slice C/D）— コード反映済み・Mac 実機未検証

- Slice C: Mac VAD に `rebaseFloor`（Windows `RebaseFloor`）を移植。`vad-selftest` / parity 静的検査。
- Slice D: Mac `transcribe` に期限 `max(10, audioSeconds + 20)`（`transcribeDeadlineSeconds` / `TranscribeDeadline`）。タイムアウトは既存 `transcribe failed` 経路。期限に加え `cancelAndFinishNow` hard-stop（deadline-only ではない；Slice C / hyp 3 RebaseFloor とは別）。Windows.Tests で式を検証。
- **この box では Apple Speech を実行できない。Mac 実機ランタイムは未検証。ライブ Speech を主張しないこと。**

### 5. その他

- Swift は一度もビルドしていない。Mac で `make parity-test` を `--require-swift` 付きで実行するのが最初の確認。
- Windows 版では、エージェント名の自動検出をしていない。照合するのはウィンドウ名、pane タイトル、実行中のコマンドだけ。Claude Code は実行中のコマンドが `node` に見えることがある。
- キーがあると、0 件一致の発話でも毎回 Jev を呼ぶ。その間、次の音声入力が最大 5 秒待たされる。
- ウェイクワード自体の認識精度は、別件として後で考える（ユーザーの判断）。

## 確認コマンド

```sh
python3 scripts/pane_route_proof.py
dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release
python3 tests/parity/run_parity.py
python3 scripts/pane_jev_probe.py          # TYPESAFE_API_KEY が必要
WSLENV=TYPESAFE_API_KEY PC=wsl make        # Windows へ配置して exe を起動し直す
```

Windows のログは `%LOCALAPPDATA%\voice-switch\voice-switch.log` にある。音声入力 1 回ごとに `tmux: hits=… jev=… -> …` が 1 行出る。

## 配置先の設定ファイル

- `C:\takeda\tools\voice-switch\config.json` は最新版（`release/config.json` と同じ内容）。古い版は `config.json.bak` に退避した。
- make は既存の `config.json` を上書きしない。
