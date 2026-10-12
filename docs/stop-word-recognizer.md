# Windows の停止語認識器（option C）

Mac は対象外です。macOS は `dictation.dedicatedStopRecognizer` を読み捨てます。Apple Speech にはこの経路はありません。

Windows の SAPI 子プロセスは、1つの `SpeechRecognitionEngine` に `DictationGrammar` と停止語の `Choices` を載せます。本文のあとに停止語を続けると（「…まとめます入力ストップ」）、SAPI は dictation だけを返し、停止を落とします。同じエンジンの停止文法に `AppendWildcard` を足した形（B）も同様です。dictation の文法のほうが選ばれます。

option C は、停止の文法（`GrammarBuilder.AppendWildcard()` のあと `Choices(stopWords)`）だけを載せた別の `SpeechRecognitionEngine` に、同じ PCM を渡します。dictation の結果はそのまま残します。通常の経路が単独停止を見つけず、専用認識器が停止を報告したときだけ、`FinishReason.StandaloneStop` で本文をその手前で切ります。

`dictation.dedicatedStopRecognizer` の既定は false です。人の声と打鍵音はまだ測っていません。見本の `config.example.windows.json` も false です。子プロセスは起動時の環境変数 `VOICE_SWITCH_DEDICATED_STOP`（`1` / `0`）と `VOICE_SWITCH_STOP_MIN_CONFIDENCE`（`DictationBoundaries.StopGrammarMinConfidence`、0.4）を読みます。オンにする変更はトレイの再読み込みか再起動のあとです。オフは次の発話から判定側で無視します。

## 測定（takets-pc、合成音声、コード変更なし）

条件: ja-JP 認識器 `MS-1041-80-DESK`（入っている認識器はこれだけ）。声は Haruka の TTS、16 kHz、モノラル。止まった判定は stop 文法の結果で conf ≥ 0.4。wav はメモリ上で作り、ファイルは残していません。

- A: いまの形（`DictationGrammar` ＋ 停止語の `Choices`）
- B: A の停止文法を `AppendWildcard()` ＋ 停止語に替えたもの（認識器は同じ）
- C: 停止の文法（ワイルドカード ＋ 停止語）だけを持つ、別の認識器

| 言葉 | パターン | A | B | C |
|---|---|---|---|---|
| 入力ストップ | 単独 | 5/5 | 5/5 | 5/5 |
| 入力ストップ | 〜です＋停止語 | 0/5 | 0/5 | 5/5 |
| 入力ストップ | 〜です、停止語。 | 0/5 | 0/5 | 5/5 |
| おわり | 単独 / 末尾 / 句点 | 5 / 0 / 0 | 5 / 0 / 0 | 5 / 5 / 5 |
| stop dictation | 単独 / 末尾 / 句点 | 5 / 0 / 0 | 5 / 0 / 0 | 5 / 5 / 5 |

単体の例（A と同じ文法。本文に続けると dictation が本文ごと1件返す）:

| # | 言った内容 | 結果 | 一致したか |
|---|---|---|---|
| c1 | 入力ストップ | stop 文法 / 「入力ストップ」/ conf 0.57 | 一致 |
| c2 | 今日は会議の資料をまとめます入力ストップ | dictation / 「…めとります入力ストップ」/ 最後の2語は `入力`、`ストップ` / conf 0.42 | 文字としては末尾にある |
| c3 | 今日は会議の資料をまとめます、入力ストップ。 | dictation / 「…有力紙TAP」/ conf 0.38 | 不一致 |
| c4 | おわり | stop / conf 0.86 | 一致 |
| c5 | 今日は…まとめますおわり | dictation / 「…での障害」 | 不一致 |
| c6 | 今日は…まとめます、おわり。 | dictation / 「…ガスをはり」 | 不一致 |
| c7 | stop dictation（ja-JP 認識器） | stop / conf 0.61 | 一致 |
| c8 | 今日は…まとめます stop dictation | dictation / 「…苦肉テンション」 | 不一致 |

- 誤って止まった数: 停止語を含まない本文20本（「入力欄」「ストップウォッチ」「終わり次第」「入力ストップという言葉は使いません」など）を3つの言葉で流して、A・B・C のどれも 0/60。
- 1本あたりの認識時間: A と B は約 130〜320 ms、C は約 20〜40 ms。
- 止まった位置と wav の終わりの差は、どれも約 640〜690 ms。TTS の末尾無音の長さなので、止まるまでの時間としては使えません。
- `EndSilenceTimeout` は 150 ms、`EndSilenceTimeoutAmbiguous` は 500 ms（既定を読んだもの）。
- トップレベルの `maxSeconds` の境目で割った wav（「〜です入力ストップ」を全体の 85% で2つに分けたもの5本）は、A・B・C のどれも前半でも後半でも止まりませんでした（0/5）。境目をまたぐ停止語は、この実装でも拾いません。
- conf は TTS の参考値です（A の単独 0.57〜0.86、C は 0.87〜0.90）。下限は既存の 0.4 をそのまま使います。
- en-US の認識器はこの PC に入っていません。

## 実装

専用エンジンは dictation のパスのあと、同じ PCM に対して順に走らせます。失敗やタイムアウトは dictation の結果を捨てません。停止語の位置は、単語列の末尾から詰めた文字列が設定された停止語と一致する最長の範囲です。サンプル位置は dictation と同じ `TickSamples`（絶対位置）です。

JSON は `tailStop`、`tailStopConfidence`、`tailStopWord`、`tailStopStart`、`tailStopEnd` です。`tailStop` は信頼度が 0.4 以上で、範囲がリクエストの中にあるときだけ true です。閾値未満でも信頼度と範囲は残し、ログの `tailConf` / `tailRange` で近傍を見ます。ログに停止語と、切り落とした本文は出しません。

判定は `DictationBoundaries.TryDedicatedTailStop` です。閉じた発話だけで、`TailStop` が true、信頼度が 0.4 以上、正規化した語が `stopWords` にあり、範囲がソースの中にあるとき採用します。`PrefixHead` とプローブは無視します。本文テキストは、終端が停止の開始以前の lexeme だけを連結したものです。停止に食い込む語は落とします。音声の終端は停止の開始です。通常の `StandaloneStopRange` が先に一致したときは、そちらの開始位置で切ります。

この box では SAPI を実行できません。専用エンジンの C# は PowerShell の `Add-Type` に渡す C# 5 です。
