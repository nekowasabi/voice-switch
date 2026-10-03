# voice-switch

メニューバーアプリ。ウェイクワードだけ言うと、コマンドを `/bin/sh` 経由で実行する。ウェイクワードに続いて話すと、その音声を録音して superwhisper で開く。`main` では、その本文を別のプログラムには渡さない。

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
- wav は superwhisper で開く。`dictation.recordingsDir`（見本は `~/Documents/superwhisper/recordings`）の新しい `meta.json` から `llmResult`、無ければ `result` を読み、長さだけログする。`main` ではその文字列は渡さない。superwhisper が前面にいるあいだ、ウェイクワードを聞いたときの前面アプリを activate する。これは superwhisper がペーストを飛ばさないようにするため。

`stopWords` を単体で言うと、進行中の入力を終える。代わりに `skipWhileMicInUseBy` のアプリがマイクを使っているときは、同じ語で `stopCommand` を実行する。見本の語は `入力ストップ`。見本の `stopCommand` は同じ superwhisper の録音 URL で、録音はトグルする。

`skipWhileMicInUseBy` のアプリがマイクを開いているあいだ、ウェイクワードは無視する。前面アプリのバンドル ID が `dictation.excludeBundleIDs` にあるときは入力しない。

トップレベルの `maxSeconds`（見本は 2.5）は、短いウェイクワード発話の上限。入力の上限ではない。

メニューは、一時停止（マイクを放す）、マイク、設定ファイルを開く、ログを開く、ログイン時に起動、終了。

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
