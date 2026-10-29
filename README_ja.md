# voice-switch

メニューバーアプリ。ウェイクワードだけ言うと、コマンドを `/bin/sh` 経由で実行する。ウェイクワードに続く発話を録音して superwhisper で開くのは、`dictation` があるときだけ。無いときは、その長い発話は無視する。入力の本文が返ると、ペインのカタログ（必要なら Jev が確認する）がちょうど 1 つのペインを指すときだけ、その tmux ペインへ送る。それ以外は何も送らない。

[English](README.md)

バンドル ID は `local.voice-switch`。`Info.plist` の `LSMinimumSystemVersion` は 26.0。Mac でのビルド成功は書いていない。

## インストール

`make install` はリリースビルドを `VoiceSwitch.app` にし、`local.voice-switch` としてアドホック署名し、`~/Applications` にコピーする。`~/.config/voice-switch/config.json` が無いときだけ `config.example.json` をそこへコピーし、アプリを開く。初回は Whisper のモデル（約 550 MB）も `~/Library/Application Support/voice-switch/models/` にダウンロードする。`make model` でモデルだけを取得できる。

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

`stopWords` を単体で言うと、進行中の入力を終える。代わりに `skipWhileMicInUseBy` のアプリがマイクを使っているときは、同じ語で `stopCommand` を実行する。見本の語は `入力ストップ`。`stopWords` を書かないときは locale ごとの既定が入る。`ja-JP`（`locale` 省略時も）は `入力ストップ`、`en-US` は `stop dictation`、それ以外の locale は既定なし。自分で書いた値は `[]` も含めて上書きしない。停止ワードは、その locale の言葉で、本文に出にくい言葉を選ぶ。（macOS の既定は Mac 未検証。）見本の `stopCommand` は同じ superwhisper の録音 URL で、録音はトグルする。

Windows では、本文のあとに続けた停止語を dictation が吸い込むことがある。`dictation.dedicatedStopRecognizer`（Windows だけ。見本は false）を true にすると、同じ PCM を停止語だけの別の SAPI 認識器に渡し、通常の経路が単独の停止を見つけなかったとき、その手前で入力を終える。人の声と打鍵音は未測定なので見本は false。Mac はこのキーを読んでも使わない。測定と実装は [docs/stop-word-recognizer.md](docs/stop-word-recognizer.md)。

`skipWhileMicInUseBy` のアプリがマイクを開いているあいだ、ウェイクワードは無視する。前面アプリのバンドル ID が `dictation.excludeBundleIDs` にあるときは入力しない。

任意の `dictation.superwhisperMode`（Mac + Windows。key または表示名）は、voice-switch の入力用に自動ペーストを切った Superwhisper の mode を選ぶ。未設定なら従来どおり（Superwhisper が自動ペーストしてもよい）。設定すると、handoff の前にその mode へ切り替え、結果のあと元の mode に戻す。この mode は自動録音と、自前の mode を持たない action の録音にだけ使う。ウェイクワードの録音では切り替えず、貼り付けは Superwhisper に任せる。Mac の mode 切替はソースに配線済み。この box では Mac 実機ランタイムは未検証（swiftc / Apple Speech なし）。

トップレベルの `maxSeconds`（見本は 2.5）は、短いウェイクワード発話の上限。入力の上限ではない。

`earlyWakeMs`（任意。Mac と Windows の入力モード。試すなら 300）を設定すると、発話が閉じる前に認識する。無音が 60 ms と 150 ms 続いた時点、および発話 `earlyWakeMs` ごとに認識し、`hangoverMs` を待たずにウェイクを確定する。長いウェイクワードの先頭でもある語（`音声入力` と並ぶ `音声`）は、150 ms の無音のあとでだけ確定する。そのまま続けて入力を始めるのも、直後に 150 ms の間があるときだけなので、`音声認識…` はウェイクにならない。未設定なら従来どおり。合成音声での計測は `.claude/hillclimb/wake-latency/` にある。実機での確認は両プラットフォームとも未実施。

`windows.actions`（任意。Windows の入力モード。Mac の `macOS.actions` に当たる）を設定すると、ウェイクワードごとに専用の URL を開ける。各項目は `name`・`wakeWords`・`url` と、任意の `superwhisperMode`（無ければ `dictation.superwhisperMode`）。既定のウェイクワードが一致しなかったときだけ、設定順に試す。録音は通常の入力と同じで、Superwhisper の結果を URL の `input=` に入れて `rundll32 url.dll,FileProtocolHandler` で開く。ペインへの送信と貼り付けはしない。URL は `macOS.wakeURL` と同じ規則で読み込み時に検査する。早期ウェイクの判定は既定のウェイクワードだけを使う。例は [config.example.windows-url.json](config.example.windows-url.json)。Windows 実機での確認は未実施。

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

## macOS の URL スキーム実行設定

メニューの「設定ファイルを開く」から、既存の JSON オブジェクトに次を追加します。

```json
"macOS": {
  "wakeURL": "superwhisper://record/start",
  "stopURL": "superwhisper://record/stop"
}
```

`wakeURL` は `command`、`stopURL` は `stopCommand` より優先され、シェルを介さず
`/usr/bin/open` に URL を一引数で渡します。互換性のため `command` は引き続き必須です。
各 URL の省略・null は従来コマンドに戻り、空文字列や不正 URL は設定エラーになります。
Windows .NET は `macOS` を無視し、従来のコマンドを使います。

ウェイクワードで URL を実行するには **`dictation` オブジェクトを削除してコマンドモードにします**。
既存のディクテーション中の停止処理や Superwhisper への WAV 引き渡しは変更しません。
`stopURL` は `skipWhileMicInUseBy` のアプリがマイクを使用中の場合だけ実行されます。
URL の空白は `%20` にし、シェル用の引用符を付けないでください。
不正な再読込はログに記録し、前の有効な設定を維持します。

[完全な設定例](config.example.macos-url.json)と[詳細・Mac 確認手順](docs/macos-url-actions.md)を参照してください。
Mac で `make build`、`make url-test` を実行後、別の設定コピーで次を実行すると、
voice-switch のマイクを使わず URL を確認できます（Superwhisper の録音を開始します）。

```sh
VOICE_SWITCH_CONFIG="$PWD/config.example.macos-url.json" .build/release/voice-switch --fire
```

URL 起動失敗時は非ゼロ終了となり、ターミナルまたは「ログを開く」で詳細を確認できます。
クラウドでは静的検証のみ実行済みです。Swift / .NET SDK がなく、Mac ビルド、
LaunchServices、実マイク、Superwhisper の録音・文字起こし・貼付の一連の動作は未確認です。

上の例は[公式の deep link 仕様](https://superwhisper.com/docs/modes/switching-modes)にある
開始専用・停止専用 URL を使います。`superwhisper://record` はトグルなので、
繰り返すと状態を反転させます。`--fire` の再実行で開始状態が維持されることを確認し、
停止は Superwhisper の UI、またはテスト用コピーの `wakeURL` を `/record/stop` に変更して確認してください。


## ウェイクワードなしの録音（実験的）

`dictation.noWakeWord` の `followup`、`autoIdle`、`relaxedReadingDistance2` は個別に指定できます。すべて既定では無効です。Mac では `dictation.superwhisperMode` も必須です。

自動で始まった録音は履歴だけに進みます。`dictation.superwhisperMode` が `autoPaste: false` のモードなら、そのモードに切り替えて Superwhisper で変換します。そうでなければ端末内で文字起こしします。Jev、tmux、アクション URL、貼り付けには渡しません。自動録音中は Escape などの Superwhisper ショートカットを奪わず、無音か停止ワードで終わります。ただし、停止ワードを言って終えた自動録音はウェイクワードの録音と同じ扱いになり、Superwhisper が手前のアプリに貼り付けます（ログは `dictation session: automatic ended by stop word, pasting like a wake-word dictation`）。自動録音中にウェイクワードを話すと、自動録音を破棄して通常の録音に切り替えます。履歴はメモリ上の最大50件で、30分後またはアプリ終了時に消えます。選んだものだけコピーできます。

- Ctrl+Shift+F10 は手動録音の開始
- Ctrl+Shift+F9 または HUD のクリックは履歴
- Windows の Ctrl+Shift+F8 は直近の取りこぼしに MISS ラベルを付ける
- config.json の `pauseShortcut`（例 `"Control+Shift+F7"`）は一時停止と再開を切り替える。書かなければ登録しない。修飾キーは `Control`・`Shift`・`Alt`（Mac は Option）・`Meta`（Mac は Command、Windows は Win）、キーは `KeyA`〜`KeyZ`・`Digit0`〜`Digit9`・`F1`〜`F12`（Windows は `F24` まで）・`Space`・`Escape`・`Enter`・`Tab`・`Backspace`。読むのは起動時だけなので、変えたらアプリを再起動する
- macOS: `"pauseDuringMeet": true` にすると、Chrome か Edge がマイクを使う間（Google Meet の通話中）は待ち受けを止め、手放したら再開する。手動で一時停止していた場合は再開しない。Meet 以外でもこれらのブラウザがマイクを使えば止まる。書かないか `false` なら無効。読むのは起動時だけ

ウェイクワードと Ctrl+Shift+F10 の録音はモードを切り替えず、その時点の Superwhisper のモードで変換し、貼り付けも Superwhisper に任せます。変換結果は履歴にも残りますが、停止時の HUD に履歴の表示（チェックの下線）が出るのは自動録音のときだけです。

### 雑音と咳払いを Superwhisper に渡さない

自動録音は、Superwhisper を開く前に次の 3 つの確認を通ります。ウェイクワードと Ctrl+Shift+F10 の録音には適用しません。

1. 長さが `noWakeWord.minHandoffMs`（既定 1000 ms）以上あること。短ければ `dictation dropped: automatic audio shorter than minHandoffMs` と記録して捨てます。
2. Silero VAD が、32 ms の窓のうち 1 つ以上で声を検出すること。打鍵音や物音はここで `dictation dropped: automatic audio had no speech` として捨てます。Mac と Windows で同じモデルを使います。
3. 2 つの認識器の書き起こしに、順序どおり共通する文字が 3 文字以上あること。Mac は Apple Speech と Whisper large-v3-turbo、Windows は [speech.cpp](https://github.com/nyosegawa/speech.cpp) の parakeet-tdt_ctc-0.6b-ja と Qwen3-ASR 0.6B を使います（BUILD.md を参照）。咳払いからは認識器ごとに別の言葉が出ますが、話した言葉はどちらでも同じになります。咳払いは `dictation agreement: shared=<n>` に続けて `dictation dropped: automatic audio had no agreed words` と記録して捨てます。この確認で、Superwhisper が開くまでが約 0.4 秒延びます。一致するまで HUD は録音中を出さないので、咳払いで HUD が点滅しません。録音中に一致した録音は、渡す前の確認を省きます。

「うん」「はい」のような短い相づちは、3 つ目の確認で捨てられることがあります。Silero のモデルや認識器（Mac は Whisper、Windows は speech.cpp）を読み込めないときは、その旨を記録し、その確認を飛ばして渡します。`voice-switch --check a.wav` は、標準エラーに `voiced=` と `agreed=`、2 つの書き起こしを出します。ファイルが残る理由と捨てられる理由を確かめるときに使えます。

Mac 未検証。実機での音声・権限・UI と精度の評価はまだ必要です。[実装と設定](docs/no-wake-word/implementation.md)、[実機チェック](docs/no-wake-word/native-validation.md)、[合格ライン](docs/wake-acceptance.md) を確認してから有効にしてください。
