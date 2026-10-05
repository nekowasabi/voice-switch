# voice-switch

メニューバーアプリ。ウェイクワードだけ言うと、コマンドを `/bin/sh` 経由で実行する。ウェイクワードに続く発話を録音して superwhisper で開くのは、`dictation` があるときだけ。無いときは、その長い発話は無視する。入力の本文が返ると、ペインのカタログ（必要なら Jev が確認する）がちょうど 1 つのペインを指すときだけ、その tmux ペインへ送る。それ以外は何も送らない。

[English](README.md)

バンドル ID は `local.voice-switch`。`Info.plist` の `LSMinimumSystemVersion` は 26.0。Mac でのビルド成功は書いていない。

## インストール

`make install` はリリースビルドを `VoiceSwitch.app` にし、`local.voice-switch` としてアドホック署名し、`~/Applications` にコピーする。`~/.config/voice-switch/config.json` が無いときだけ `config.example.json` をそこへコピーし、アプリを開く。

`make uninstall` はアプリを終了してそのコピーを消す。`make logs` は `~/Library/Logs/voice-switch.log` を追う。

## 許可

マイクと、端末内の音声認識を求める。入力の録音中は、superwhisper の録音開始とキャンセルのショートカットを使うキーボードタップも入れる。これにはアクセシビリティが要る。許可されるまではログに `hotkey: Accessibility not granted yet` と出る。

## 設定

`$VOICE_SWITCH_CONFIG`。未設定なら `~/.config/voice-switch/config.json`。発話のあいだに更新時刻が変わっていれば読み直す。

`command` と `stopCommand` は `/bin/sh -c` で実行する。見本は `config.example.json`。

ウェイクワードは、その発話全体と一致する必要がある。見本は `音声入力`、`音声入る`、`おんせい`、`音声に入るよ`、`音声に入る`、`音声によって`。ロケールは `ja_JP`。

`dictation` が無いとき、ウェイクワード単体は `command` を実行する。見本のコマンドは `open -g superwhisper://record`。

`dictation` があるとき:

- ウェイクワード単体は入力を開始し、続きの音声を待つ。見本の待ちは `startTimeoutMs` の 3000。その前に無音なら取り消す。
- ウェイクワードで始まり、続きがある発話は、およそ `endSilenceMs` の無音（見本は 1200）、停止語、superwhisper の録音ショートカット、または `dictation.maxSeconds`（見本は 60）まで録音する。ウェイクワード部分の音声は切る。
- wav は superwhisper で開く。`dictation.recordingsDir`（見本は `~/Documents/superwhisper/recordings`）の新しい `meta.json` から `llmResult`、無ければ `result` を読み、長さだけログする。その文字列は下のペイン振り分けに渡す。superwhisper が前面にいるあいだ、ウェイクワードを聞いたときの前面アプリを activate する。これは superwhisper がペーストを飛ばさないようにするため。

`stopWords` を単体で言うと、進行中の入力を終える。代わりに `skipWhileMicInUseBy` のアプリがマイクを使っているときは、同じ語で `stopCommand` を実行する。見本の語は `入力ストップ`。見本の `stopCommand` は同じ superwhisper の録音 URL で、録音はトグルする。

`skipWhileMicInUseBy` のアプリがマイクを開いているあいだ、ウェイクワードは無視する。前面アプリのバンドル ID が `dictation.excludeBundleIDs` にあるときは入力しない。

任意の `dictation.superwhisperMode`（Mac + Windows。key または表示名）は、voice-switch の入力用に自動ペーストを切った Superwhisper の mode を選ぶ。未設定なら従来どおり（Superwhisper が自動ペーストしてもよい）。設定すると、handoff の前にその mode へ切り替え、結果のあと元の mode に戻す。pane へ送れなかったときだけ、voice-switch がウェイク時点のアプリへ一度ペーストする（SendFailed のときは send-keys に渡したのと同じ解決済み本文だけ。全文ラッパーは貼らない）。pane は当たったが送信本文を切り出せなかったときは、全文をペーストしない。Mac の mode 切替・paste はソースに配線済み。この box では Mac 実機ランタイムは未検証（swiftc / Apple Speech なし）。

トップレベルの `maxSeconds`（見本は 2.5）は、短いウェイクワード発話の上限。入力の上限ではない。

メニューは、一時停止（マイクを放す）、マイク、設定ファイルを開く、ログを開く、ログイン時に起動、終了。

## ペインへの振り分け（tmux と Jev）

入力の本文は、`tmux list-panes -a -F '#{pane_id}\t#{window_name}\t#{pane_title}\t#{pane_current_command}'` から作る閉じたカタログと照合する。macOS では `/usr/bin/env` 経由で実行する。Windows では `wsl.exe -e tmux ...` として実行するので、対象は WSL の中の tmux。アプリは tmux サーバを起動しない。tmux が失敗したか、サーバが無いときは、何も送らず失敗をログする。本文に、空でないラベルが大文字小文字を無視した部分文字列として含まれると、そのペインはヒットする。ラベルは、ペインのタイトル、ウィンドウ名、現在のコマンド。Linux では、`/proc` からそのペインに見つけた許可リストのエージェント名（`claude`、`aider`、`gemini`、`copilot`、`codex`、`devin`、`hermes`、`opencode`、`pi`、`grok`、`cursor-agent`）もラベルになる。Mac と Windows には `/proc` の走査が無いので、タイトル、ウィンドウ名、コマンドだけが一致する。

アプリの環境に `TYPESAFE_API_KEY`（無ければ `JEV_API_KEY`）があると、入力の本文とペインのラベル（id、ウィンドウ名、タイトル、コマンド、エージェント名）を TypeSafe の `https://api.typesafe.ai/v1/systemone`（モデル `jev-latest`）へ、「話者が名指ししているのは列挙したどのペインか、あるいは無いか」という 1 つの選択質問として送る。Finder から起こしたアプリと Windows のトレイはシェルの export を見ないので、この変数はアプリが起動する場所で設定する。答えは確信度 0.8 以上のときだけ、カタログに既にあるペインに対してだけ使う:

- ラベルのヒットが 1 つ: それを送る。ただし Jev が確信をもって別のペインか「無い」を答えたときは、何も送らない（`jev rejected`）。
- ヒットが 2 つ以上: Jev が答えたペインがヒットの中にあれば、それを送る（`jev narrowed`）。無ければ何も送らない。
- ヒットが無い: 何も送らない。確信のある答えは提案としてログするだけ。

鍵が無いとき、5 秒のタイムアウト、HTTP エラー、カタログの選択になっていない答えのときは、Jev 無しの規則に戻る。ラベルのヒットがちょうど 1 つなら送り、それ以外は送らない。Jev はカタログに無いペインを足さない。本文は `tmux send-keys -t <pane-id> -l -- <本文>` として argv で渡し、シェルは通さず、`%N` の形の id にだけ送る。入力 1 回につき、`tmux: hits=2 jev=%2@0.87 -> send %2 (jev narrowed)` のようなログを 1 行書く。`jev=off` は鍵が無い、`jev=error` は要求か答えが失敗したことを示す。鍵と入力の本文はログに書かない。振り分けが動いているあいだ、次の handoff は待つ。

0.8 の下限は `scripts/pane_jev_probe.py` から取った。固定のカタログに 8 つの発話を当て、外した 2 つの確信度は 0.49 と 0.52、当たった答えは 0.80 から 1.00 だった。再実行して `tmux:` のログ行を読み、下限を見直す。`scripts/pane_route_proof.py` は Linux の私有 tmux サーバで、Jev 無しの規則を証明する。方針表、ラベル照合、答えの解析は `tests/parity/fixtures/pane_route.json` を通じて Swift と C# で共有している。

手で試していないこと: Swift 側はここではコンパイルしていないので、Mac のビルドと Mac での実入力は確認済みとは書かない。Windows では、`wsl.exe` が tmux に届くこと、日本語が `wsl.exe` の往復で壊れないこと、実入力が WSL のペインに届くことは実行していない。WSL 上の `make win-test` が C# のロジックを、proof スクリプトが Jev 無しの規則をそれぞれ検証する。

## CLI

- `voice-switch --check a.wav` は、ファイルを VAD と音声認識に通して、発話ごとの判定を出す。
- `voice-switch --simulate a.wav` は、マイクの代わりに1つのファイルを本番の経路へ流す。

## main には無い

[PR #3](https://github.com/nekowasabi/voice-switch/pull/3) は開いたままで、未マージ。この節は現在の `main` の動作ではない。

そのブランチでは、空でない `llmResult`（無ければ `result`）を、シェルを通さず argv で渡す。

```
/usr/bin/env computer-use-jev -goal <本文>
```

空白だけなら起動しない。終了は待たない。終了コードが非ゼロならログだけ残す。`-key` は渡さない。`TYPESAFE_API_KEY` はソースにも設定にも書かない。CLI が鍵を読むなら、その環境変数を読む。このリポジトリに鍵の値は無い。

そのブランチの Mac ビルド成功は書いていない。フォーカスを変えたあとも superwhisper がペーストするかは、確認済みとは書いていない。
