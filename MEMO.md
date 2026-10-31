# Superwhisper を持っていない人向けのローカル文字起こし（調査と方針メモ）

2026-10-21 時点の記録。「確認」は実物か公式ページを見たもの、「推測」はそこから読んだもの、「未検証」はまだ確かめていないもの。

## 目的

voice-switch のテーマは「音声で処理を実行させること」。ウェイクワードだけではできることが少ないため、Superwhisper を持っていない人でも本文を文字にできるようにする。Superwhisper より精度が落ちるのはある程度受け入れる。

## 現状（確認）

- 本文の文字起こしは Mac・Windows とも Superwhisper に頼っている。voice-switch が自分で録った WAV（16kHz・モノラル）を Superwhisper に渡し、録音フォルダの `meta.json`（`llmResult` か `result`）を最大30秒ポーリングして読む。
  - Mac: `Sources/voice-switch/MacApp.swift` の `handoff`（393 行付近）
  - Windows: `dotnet/VoiceSwitch.Windows/SuperwhisperHandoff.cs` の `SubmitAsync`（60 行付近）
- マイク・ウェイク・停止・tmux への送出（`PaneRoute.swift` / `TmuxPaneRouter.cs`）は、すでに Superwhisper に頼っていない。
- ウェイクワードで1つのコマンドを実行するモード（`dictation` を外し、`command` / `macOS.wakeURL` を使う）は、今でも Superwhisper なしで動く。ただし実行できる処理は1種類だけ。
- `macOS.actions` / `windows.actions`（ワードごとの URL）は、続けて話した内容を `input=` として URL に付けるため、必ず録音して Superwhisper に渡す（`MacApp.swift:1047-1066`）。Superwhisper が無いと使えない。
- 本文全体を文字にする関数はすでにある。
  - Mac: `WhisperGate.transcribe`（whisper.cpp large-v3-turbo、日本語固定）
  - Windows: `WordAgreement.TranscribeAsync`（speech.cpp の parakeet-ja / qwen3-asr）
  - どちらも、自動録音を送ってよいか判定する（同意判定）ためのもの。

## 検討した案（ateam idea、3者・2ターンの議論）

- 3者一致: 既存のローカル認識エンジンに本文用の呼び出しを足す。Superwhisper の経路は変えない。自動フォールバックはしない（ローカルとクラウドの間でも、Superwhisper と新しい経路の間でも）。失敗したら本文は送らずに知らせる。初版は LLM による後処理（`llmResult` 相当）をしない。
- 意見が分かれたまま: クラウド API を本体に組み込むか、外部コマンドで受けるか。Windows に別のランタイムを足すか。発話中に推論を進めるか。

## 採用したい方針: Cohere Transcribe の GGUF を transcribe.cpp で動かす

- Superwhisper がローカルで動かしているのは Cohere Transcribe（`~/Library/Application Support/superwhisper/models/cohere-transcribe/cohere-transcribe-q4/model.safetensors`、1.5GB）。これは transformers 形式で、Superwhisper を持っていない人の手元には無いため流用できない（確認）。
- 元のモデル `CohereLabs/cohere-transcribe-03-2026`: パラメータ数 2B、Apache 2.0、日本語に対応。HF で同意が必要（gated）。公式の動かし方は transformers / vLLM（Python）（確認）。
- **`handy-computer/cohere-transcribe-03-2026-gguf` は同意不要（gated=false）。ログインせずにダウンロードできる**（HF API と HEAD リクエストで確認）。Q4_K_M 1.56GB / Q5_K_M 1.77GB / Q6_K 1.97GB / Q8_0 2.41GB / F16 4.1GB。
- 実行エンジン [transcribe.cpp](https://github.com/handy-computer/transcribe.cpp): MIT、ggml、Mac は Metal、Windows は Vulkan。C API と Swift バインディングがある。入力は 16kHz モノラルの WAV。言語の指定（`-l ja`）が必須（確認）。
- Windows で使っている speech.cpp は Cohere を扱っていないようだ（README に記載なし。推測）。

### 実装方針（案）

1. 設定 `dictation.transcriber` を追加する: `"superwhisper"`（既定・今のまま）/ `"local"`。
2. `"local"` のときは `handoff` / `SubmitAsync` で Superwhisper を起動せず、transcribe.cpp で文字にして、既存の送出（tmux・貼り付け・`actions` の `input=`）に流す。
3. モデルは同梱しない。`make model` と同じ要領で GGUF を URL から直接ダウンロードする。
4. 組み込み方は2つの候補がある。
   - `transcribe-cli` を外部プロセスとして呼ぶ: whisper.cpp との ggml の二重リンクを避けられる。
   - ライブラリとしてリンクして常駐させる: 速い。ggml のシンボル衝突は未検証。
5. ホットキー（完了・取消）と `superwhisperMode` は、新しい経路からは切り離す。

## 計測結果（LT0、確認）

Superwhisper の過去の録音（モデル: Cohere Transcribe）8件、長さ 3.6〜9.3 秒、M-series Mac、Q4_K_M:

```
KPI n=8 failed=0 meanCER=0.056 p50local=252.5ms p50superwhisper=172.5ms
```

- 8件中5件が Superwhisper の `rawResult` と完全に一致した。違いは固有名詞・珍しい語（オミセイム→オミクロン、臨技書→臨時賞）と、冒頭に挿入された「どうも、」。
- 時間はプロセスを起動するたびにモデルを読み込んだ場合の値。初回だけ 4.4 秒かかった（ページキャッシュが冷えていたため）。
- CER は Superwhisper の出力との差であり、正解文との差ではない。
- 証拠: `~/Library/Caches/voice-switch-verify/evidence/lt0-20261021/lt0/`（`results.json`、`summary.json`）

→ 精度も速度も、当初の想定（大きく劣る・数秒かかる）よりずっと良い。割り切りが要るのは、主に Superwhisper の LLM 後処理が無くなる点。

## 残っている課題

- 初回（ページキャッシュが冷えた状態）の 4.4 秒をどう扱うか: 常駐させる、起動時に予熱する、など。
- Windows（Vulkan）で同じ精度と速度が出るか（未検証）。Windows では speech.cpp の約 1.5GB に加えて、1.56GB 増える。
- 英数字・識別子が混じった発話、長い発話（30秒〜）は計測していない。
- 合格の閾値（CER・遅延）はまだ決めていない。

## 検証項目

- Mac: `.claude/skills/verify-voice-switch-mac/features/local-transcription.md`（LT0〜LT6）と、ヘルパー `engine_bench_mac.py`
- Windows: `.claude/skills/verify-voice-switch/features/local-transcription.md`
- LT0 は今すぐ実行できる。LT1〜LT6 は実装後に、実際のログの行に合わせて書き直す。

## 2026-10-31 追記: 日本語本文は Haas を外部 GPU ランナーで

- 日本語本文の推奨は Haas-ja-v1.0（`third-intelligence/Haas-ja-v1.0`）を外部 GPU ランナー（`scripts/haas_transcribe.py`）で動かす方式。Linux + NVIDIA GPU（CUDA/Triton）が必須。
- Cohere GGUF / Whisper（transcribe.cpp / speech.cpp）は CPU と Mac の代替として残す。`local` は設定値ではない（`dictation.transcriber` は `superwhisper` か `haas` のみ）。
