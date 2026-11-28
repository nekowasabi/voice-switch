# Windows の handoff とトレイ（指摘 1・6・7・16）

## Sub-features
- 1: `RegisteredSuperwhisperHandoff.Sweep` は 10 分より古い WAV だけを消し、`mode-restore.json` と `mode-owner.lock` は残す。
- 6: Reload の停止が時間切れになったあと、古い実行が 0 で終わっても Error のまま残り、自動の再試行が走る。
- 7: ジャーナルの書き込みが 1 回失敗しても、次の行は設定したフォルダに書く。
- 16: `SpeechPowerShell` の診断は、子プロセスが終わった直後に出した最後の行も読む。

## How to get to it (user POV)
1: 自動の口述のあと 10 分以上たってからマイクを差し替える。そのあとウェイクワードで口述し、貼り付くかを見る。6: Superwhisper の結果待ちの間にマイクを差し替え、トレイが「停止中」のまま止まらないかを見る。

## Driving it with dotnet/VoiceSwitch.Windows.Tests
dotnet がある機械（WSL）で `make win-test`。直す前に、次のテストを `Program.cs` の一覧に足して FAIL を確かめる。
- 1: 既存の「dictation handoff sweeps WAVs older than 10 minutes」の隣に、「sweep keeps mode-restore.json older than 10 minutes」を足す。11 分前の時刻にした `mode-restore.json` と WAV を置いてハンドオフを作り、WAV だけが消えて JSON が残ることを確かめる。そのあと `RecoverAsync` がモードを戻すことも確かめる。
- 6: `TrayMeetTests.cs` の supervisor の偽物を使う。停止が時間切れ → 古い実行が 0 で終わる、の順に起こし、状態が Error で、再試行が 1 回予約されることを確かめる。
- 7: 当日のファイルを別のハンドルで `FileShare.ReadWrite` のまま開いておき、2 行書く。2 行とも設定したフォルダに入ることを確かめる。
- 16: すぐ `recognizer ready:` を出して終わる偽のスクリプトで診断を 20 回流し、20 回とも成功することを確かめる。

## Gotchas
- この Mac には dotnet が無い（`check.sh doctor` が SKIP を出す）。ここに書いたテストは未作成で未検証。
- 6・7・16 は監査の時点で未検証（読んだだけ）。足したテストが直す前に FAIL しなければ、その指摘は直さない。
