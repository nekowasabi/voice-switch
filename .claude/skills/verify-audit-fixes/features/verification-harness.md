# 検証の仕組みが失敗を隠さない（指摘 2・3・12・13・14・15）

## Sub-features
- 2: `verify-voice-switch-mac/verify.sh build` は swift build の失敗で失敗する。直したら `verify-pr-merge/verify.sh` の回り道（cmd_build の「Judge by the binary」）も消す。
- 3: `verify-sticky-mode/verify.sh drive` は Swift 検査の `fatalError` で失敗する。
- 12: `tests/journal/Program.cs` と `tests/macos/{ListeningPolicy,RecognitionSlot,SuperwhisperResult}` を、どれかの検査から実行する。
- 13: `tests/budget.sh` が HudBlip（104）・HudNoise（92）・MeetPause（120）の行数を固定する。
- 14: `make parity-test` は今のソースから作ったバイナリで比べる。
- 15: `scripts/pane_route_proof.py` の落ちない 2 つの証明を消す。

## How to get to it (user POV)
変更を入れた人が `sh tests/check.sh` や各スキルの `verify.sh` を流し、PASS を見て「壊れていない」と判断する場面。

## Driving it with check.sh
- `check.sh 2`: PATH の先頭に「必ず失敗する swift」と「何もしない make」を置き、前回のビルドを残した一時ルートで `verify.sh build` を走らせる。終了コードが 0 でなければ PASS。
- `check.sh 3`: `sh` の shim で TLC の段を飛ばし、`swiftc` の shim で「Fatal error を出して 134 で終わる」検査を作る。drive が 0 以外で終われば PASS。
- `check.sh 13`: 一時の git リポジトリにモデルと `budget.sh` を写し、モデルを 1 つずつ固定値 +1 行にして `budget.sh` が落ちるかを見る。
- 12: 直したあと、`tests/journal/Program.cs` の 1 件を壊して（例: 期待値を変える）`make win-test` が落ちることを確かめ、戻す。Mac の 3 つも同じく、1 件壊して `sh tests/check.sh` が落ちることを確かめる。
- 14: `Sources/voice-switch/SpeechGate.swift` の判定を一時的に変えて `make parity-test` を流し、DIFF が出ることを確かめてから戻す。
- 15: 削除のあと baseline が PASS であること。`HANDSOFF.md` の「証明」の記述も同じコミットで直す。

## Gotchas
- 自分の検査スクリプトでも、検査対象の出力を `| tail` に流さない。終了コードが tail のものになる。
- 12 の Windows 側は dotnet が無いと走らない。`tests/no-verification.txt` の「tested by」行は、実行されるようになってから直す。
