# 作業引き継ぎ（2026-10-05）

## 目的

音声入力で「〈pane 名〉に〜して」と話すと、superwhisper の文字起こし結果が WSL / Mac の tmux pane に届くようにする。pane 名の聞き間違いは Jev（TypeSafe）で補正する。Jev に許すのは、拒否・絞り込み・提案の三つだけ。Jev が送り先を新しく作ることはない。

## ブランチと状態

- ブランチは `feat/pane-route-jev` で、origin/main から 11 コミット進んでいる。まだ push していない。
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

### 1. 指示の部分まで送られる（今日の最後に見つけた問題）

「〜に『ハローワールド』を送信して」と話すと、発話の全文が pane に入る。送りたいのは「ハローワールド」だけ。

方針案は次の三つ。

1. まず決定論で処理する。本文に `「…」` があれば、その中身だけを送る。superwhisper の LLM が鉤括弧を付けるかどうかは、実際の `llmResult` で確認すること。
2. 鉤括弧がないときは、コードで本文の候補を切り出す。たとえば pane 名の後ろの部分、「を送信して」「と入力して」の前の部分など。そのうえで Jev の Choice にどれが本文かを選ばせる。Jev は文章を作るモデルではなく、候補から選ぶモデルなので、この形が合う（typesafe-ai skill の「Select instead of generate」）。
3. 切り出しに失敗したときは、全文を送るか送らないか。これは方針の判断なので、ユーザーに決めてもらう。

Mac（`PaneRoute.swift`）と Windows（`PaneRoute.cs`）の両方に入れる。fixture `pane_route.json` に、切り出しの例も足す。

### 2. 二重表示（作業途中・未コミット）

superwhisper の自動ペーストと pane への送信の両方が届き、同じ文が二か所に出る。

- 対策は、superwhisper に voice-switch 専用の mode を作り、その mode では自動ペーストを切ること。ユーザーが作成済みで、key は `new-mode-1`、表示名は `voice_switch`、`autoPaste: false`。
- `superwhisper://mode?key=<key>` で mode を切り替えられることは Windows で確認済み。切り替えは `preferences.json` の `activeMode` に反映される。
- 実装方針は次のとおり。
  - 設定の `dictation.superwhisperMode` に表示名か key を書く。
  - 音声を渡す前に専用 mode へ切り替え、結果を読んだら元の mode に戻す。
  - pane に送れたら、それで終わり。送れなかったら、voice-switch が元のウィンドウへクリップボード経由でペーストする（Ctrl+V の後、クリップボードを約 1 秒で元に戻す）。
  - 設定がなければ今の動作のまま。
- 作業途中の差分は `610566e`（wip）としてコミットした。9 ファイルで +308 / -62。ビルドは通り、既存テスト 147 件と parity（fail=0）も通る。新しい動作のテスト、設定ファイルへの `superwhisperMode` の追記、README はまだ。差分を読んでから続けるか、revert して作り直すかを決める。

### 3. Jev が `jev=off` のまま

- Windows のユーザー環境変数 `TYPESAFE_API_KEY` は登録済み。
- 21:43 のログでも `jev=off` だった。動いている exe が、キーのない環境から起動されたままの可能性が高い。
- 対処は、トレイから終了して、エクスプローラーかスタートメニューから起動し直すこと。make から起動するなら `WSLENV=TYPESAFE_API_KEY PC=wsl make` とする。`WSLENV` を付ければ `Start-Process` の子プロセスまでキーが届くことは確認済み。

### 4. その他

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
