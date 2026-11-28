# 配送の表示と固まり（指摘 5・8・9）

## Sub-features
- 5: Mac の HUD は、届かなかった口述（`tokenEnv` 未設定の POST、送れなかったコマンド、捨てた録音）に ✓ を出さない。
- 8: tmux・wsl.exe・振り分けのコマンドが固まっても、5 秒で諦めて次の口述を受け付ける。
- 9: Mac の音声フレームのキューに上限があり、`macrowhisper` が固まってもメモリが増え続けない。

## How to get to it (user POV)
5: `tokenEnv` に存在しない環境変数名を書いた POST アクションでウェイクし、HUD とログを見る。8: tmux の代わりに固まる偽物を PATH に置いて口述し、次の口述が受け付けられるかを見る。

## Driving it with verify-voice-switch-mac
- 守る振る舞い: `verify-voice-switch-mac` の `build` → `run`（`--simulate`）で、今の正常系の口述が届くこと。
- 5: `config.safe.json` を写して、存在しない `tokenEnv` の POST アクションを足して `run` する。ログに「delivered」が出ず、HUD の状態が失敗になることを確かめる。
- 8: `sleep 60` するだけの `tmux` を PATH の先頭に置いて `run` し、5 秒ほどで次の発話が処理されることをログで確かめる。
- 9: `macrowhisper` を `sleep 60` の偽物にして 30 秒以上の音声を流し、プロセスのメモリ（`ps -o rss`）が上限で止まることを確かめる。

## Gotchas
- 8・9 は監査の時点で未検証。直す前にこの手順で再現しなければ、直さない。
- 利用者の VoiceSwitch.app は止めない。`--simulate` のバイナリは別のビルドパスに作る（verify-voice-switch-mac の build がそうしている）。
