# device_directed_speech.md の一次資料検証

検証日: 2026-10-12。元のノート (device_directed_speech.md) は変更していない。

集計: VERIFIED 58 / CORRECTED 4 / NOT FOUND IN SOURCE 1 / UNREACHABLE 2 (計 65 行)

取得方法: arXiv は abs ページと PDF/HTML を取得した。Google Patents を USPTO 原本の代わりに使った (下記参照)。引用は取得テキストからの抜粋で、数値は原文のまま書いた。検索スニペットだけで VERIFIED にした行はない。

## 検証表

| # | claim (元ノートの記述) | URL (取得に使ったもの) | status | 一次資料の引用・値 | correction |
|---|---|---|---|---|---|
| 1 | Garg 2022: タッチ起動の誤起動を、ASR 教師 (LatticeRNN) から音響のみの生徒へ蒸留して抑える | https://arxiv.org/abs/2203.15975 | VERIFIED | "We focus on audio coming from a touch-based invocation ... distill ... from an ASR-based (LatticeRNN) model" | なし |
| 2 | Garg 2022: 基準の音響のみモデルに対し 66% の改善 (EER) | https://arxiv.org/abs/2203.15975 | VERIFIED | "resulting in 66% gain in accuracy, as measured by equal-error-rate (EER), over the base acoustics-only model" | 相対か絶対かは要旨に書かれていない。単位は未確定 |
| 3 | Garg 2022: LatticeRNN と蒸留モデルのアンサンブルで 20% の改善 | https://arxiv.org/abs/2203.15975 | VERIFIED | "the ensemble of the LatticeRNN and acoustic-distilled models brings further accuracy improvement of 20%" | 同上 |
| 4 | Garg 2022: Interspeech 2022 | https://www.isca-archive.org/interspeech_2022/garg22_interspeech.html | VERIFIED | 引用情報: "Proc. Interspeech 2022, 1258-1262" | なし |
| 5 | Wagner 2312: 入力は ASR 1-best、デコーダ信号、音声エンコーダ特徴 (Whisper か UAD) | https://arxiv.org/html/2312.03632v1 | VERIFIED | "Multi 4 ... combines information from text, audio, as well as decoder signals"; UAD と Whisper を比較 | なし |
| 6 | Wagner 2312: 7B の Falcon / RedPajama を凍結して使う | https://arxiv.org/html/2312.03632v1 | VERIFIED | "the underlying LLM is frozen"; "We compare the 7B parameter versions of Falcon and RedPajama" | なし |
| 7 | Wagner 2312: LoRA rank 8、prefix tuning | https://arxiv.org/html/2312.03632v1 | VERIFIED | Appendix D Table 4: "rq=rv=d=8, α=32" が Table 1 と同じ設定。"combination of low-rank adaptation and prefix tuning" | Table 5 の別設定は rq=rv=64 |
| 8 | Wagner 2312: 最良は Falcon + UAD、80k 例、EER 8.23% | https://arxiv.org/html/2312.03632v1 | VERIFIED | "uses all 80k available training examples ... an EER of 8.23% with the UAD backbone" | なし |
| 9 | Wagner 2312: Whisper 併用で EER 8.80% | https://arxiv.org/html/2312.03632v1 | VERIFIED | "an EER of 8.80% using Whisper as the audio encoder" | なし |
| 10 | Wagner 2312: 音声のみモデルに対して約 12% と約 16% の相対改善 | https://arxiv.org/html/2312.03632v1 | VERIFIED | "relative improvements of ≈16% and ≈12% over the corresponding audio-only models" | 対応は UAD 8.23 = 約12%、Whisper 8.80 = 約16%。元ノートの順序と一致 |
| 11 | Wagner 2312: デコーダ信号のみは EER 35〜37% | https://arxiv.org/html/2312.03632v1 | VERIFIED | "Decoder signals (Uni 3)... EER=36.90% with Falcon and EER=35.04% with RedPajama" | なし |
| 12 | Wagner 2312: テキストのみは音声のみより悪い | https://arxiv.org/html/2312.03632v1 | VERIFIED | "Using only the audio modality (Uni 2) yields lower EERs than using only the text modality (Uni 1)" | なし |
| 13 | Wagner 2312: Whisper tiny 約39M、large 約1.5B (Appendix D) | https://arxiv.org/html/2312.03632v1 | VERIFIED | "'Whisper Tiny' has ≈39M parameters ... 'Whisper Large' has ≈1.5B parameters" | 本体の実験は Whisper medium (769M) を使用。元ノートに無い |
| 14 | Wagner 2312: 学習例を 1k まで減らしても機能する | https://arxiv.org/html/2312.03632v1 | CORRECTED | "The multimodal system with Falcon and the UAD backbone trained on only 10k examples (Multi 4.3) still performs better than the audio-only model trained on 80k examples (EERs of 8.84% and 9.31%). This is not the case when Whisper representations are used instead (EERs of 11.71% and 10.45%)." | 原文が示すのは 10k 例 (UAD) が 80k 例の音声のみモデルに勝つこと。1k 例の数値は本文抜粋に無い。Whisper では 10k でも勝たない |
| 15 | Wagner 2403: 多モーダルで相対 39% と 61% 改善 | https://arxiv.org/abs/2403.14438 | VERIFIED | "relative equal-error-rate improvements over text-only and audio-only models of up to 39% and 61%" | なし |
| 16 | Wagner 2403: LLM の拡大と LoRA でさらに最大 18% | https://arxiv.org/abs/2403.14438 | VERIFIED | "further relative EER reductions of up to 18% on our dataset" | なし |
| 17 | Rudovic 2411: ASR の 1-best と n-best (n=8)、FST-LM 再採点 | https://arxiv.org/html/2411.00023v2 | VERIFIED | "Conformer encoder with a CTC and attention-based decoder ... rescored using an external Finite State Transducer-based language model"; "rows with n=8 and n=1-8" | なし |
| 18 | Rudovic 2411: (a) Vicuna-7B-v1.3 にプロンプト | https://arxiv.org/html/2411.00023v2 | VERIFIED | "We use a pretrained instruction-tuned LLM, Vicuna-7B-v1.3" | なし |
| 19 | Rudovic 2411: (b) 凍結 LLM の上に分類ヘッド、任意で LoRA | https://arxiv.org/html/2411.00023v2 | CORRECTED | "we experiment with the full finetuning, and with LoRA adapters"; "ClassHead ... full finetuning of the classifier on top of the LLM"; "ClassHead approach is more accurate, as it allows to tune all model parameters" | ClassHead は LLM を全パラメータ学習する。凍結なのは ClassLoRAHead の基底 LLM 側。ClassLoRAHead の最良は FRR 14.9%、FAR 1.8% |
| 20 | Rudovic 2411: LoRA rank 8、4.1M パラメータ、全体の 0.06% | https://arxiv.org/html/2411.00023v2 | VERIFIED | "The LoRA adapters (rank=8) have 4.1M parameters that is only 0.06% of the 7B parameters" | なし |
| 21 | Rudovic 2411: 分類ヘッドは約 8k パラメータ追加 | https://arxiv.org/html/2411.00023v2 | VERIFIED | "The classifier head adds only ∼8k additional parameters" | なし |
| 22 | Rudovic 2411: 初回質問と ASR 不確かさを使うと、後続だけより 10% FRR 固定で FAR が 20〜40% 減る | https://arxiv.org/abs/2411.00023 | VERIFIED | "(20-40% reduction in false alarms at 10% fixed false rejects) ... compared to when follow-ups are modeled alone" | 要旨の "false alarms" は本文の FAR。単位は相対 |
| 23 | Rudovic 2411: 約 19k 録音、1.3k 人、約 245k 組、1:4、平均 3.5 秒 | https://arxiv.org/html/2411.00023v2 | VERIFIED | "∼19k audio recordings from 1.3k participants ... roughly 245k segments ... ratio ... ∼1:4, with average audio duration of 3.5±3.25 seconds" | なし |
| 24 | Rudovic 2411: 推論は A100 4 枚 | https://arxiv.org/html/2411.00023v2 | VERIFIED | "The inference is done using 4 NVIDIA A100 GPUs" | なし |
| 25 | Chi 2508: 教師は凍結 ASR エンコーダ、アダプタ、生徒と同時学習 | https://arxiv.org/abs/2508.02801 | VERIFIED | "task-specific adapters, on top of the (frozen) teacher encoder, trained jointly with the student model" | なし |
| 26 | Chi 2508: キーワード +26%、キーワードなし +19% の EER 改善 | https://arxiv.org/abs/2508.02801 および https://arxiv.org/html/2508.02801 | CORRECTED | 要旨: "an improvement of +26% and +19% in terms of Equal Error Rate, respectively"。本文 4.3: "the accuracy improvement of aKD with the Conformer architecture ... keyword-based invocations (HAG and AG), where aKD achieves an average relative gain of 26%"; "relative improvement in keyword-free invocations (FCO) is 19%" | 相対改善。26% は Conformer の keyword 2 種の平均。絶対 EER は要旨にない |
| 27 | Apple 音声トリガ: 2023 年 8 月公開 | https://machinelearning.apple.com/research/voice-trigger | VERIFIED | "published August 11, 2023" | なし |
| 28 | Apple: Siri の方向性検出は発話全体 (トリガ句を含む) を分析して誤トリガを減らす | https://machinelearning.apple.com/research/voice-trigger | VERIFIED | "Siri directed speech detection (SDSD) analyzes the full user utterance, including the trigger phrase segment, and decides whether to mitigate any potential false voice trigger utterances" | なし |
| 29 | Apple: iPhone、HomePod、AirPods、Watch で端末上動作 | https://machinelearning.apple.com/research/voice-trigger | VERIFIED | "The voice trigger system supports several Apple device categories like iPhone ..." と "on-device" の記述 | 取得抜粋は "like iPhone" で途切れ、機種名の全列挙は確認できていない。DDSD 論文の遅延値は無い |
| 30 | Amazon 2020 ICASSP: EER 14% 改善 | https://www.amazon.science/blog/how-alexa-knows-when-youre-talking-to-her | VERIFIED | "our machine learning model demonstrated a 14% improvement over the best-performing baseline in terms of equal error rate" | 論文本体ではなくブログの値 |
| 31 | Amazon 2020: 意味・構文の語彙特徴を追加 | https://www.amazon.science/blog/how-alexa-knows-when-youre-talking-to-her | VERIFIED | "Leveraging semantic content improves performance of acoustic-only model"; "The fractured syntax of these fragments" | なし |
| 32 | Amazon: Follow-Up Mode は約 5 秒の聴取窓 | https://www.amazon.science/blog/how-alexa-knows-when-youre-talking-to-her | NOT FOUND IN SOURCE | "With Follow-up Mode enabled, a customer can ask ... then follow up by asking 'How about tomorrow?', without having to repeat the wake word" (窓の長さの記述なし) | 5 秒の記述は取得したブログに無い |
| 33 | Amazon: 機器は TV や子供の声と区別する必要がある | https://www.amazon.science/blog/how-alexa-knows-when-youre-talking-to-her | VERIFIED | "distinguish ... between phrases like 'How about tomorrow?' and children's shouts or voices from the TV" | なし |
| 34 | Mallidi 2018: LSTM 2 本 (音響と ASR 1-best) と、デコーダ特徴を入れた DNN | https://arxiv.org/abs/1808.02504 | VERIFIED | "two long short-term memory (LSTM) neural networks trained on acoustic features and ... 1-best hypotheses ... A feed-forward deep neural network (DNN) ... ASR decoder" | なし |
| 35 | Mallidi 2018: EER デコーダ 9.3%、音響 10.9%、1-best 20.1%、統合 5.2%、44% 相対改善 | https://arxiv.org/abs/1808.02504 | VERIFIED | "ASR decoder, acoustic embeddings, and 1-best embeddings yield an equal-error-rate (EER) of 9.3%, 10.9% and 20.1%, respectively. Combination ... 44% relative improvement and a final EER of 5.2%" | 44% の基準は要旨に明記されていない。9.3→5.2 は 44% 減に一致 |
| 36 | Mallidi 2018: 1-best 単独は最弱 (20.1%) | https://arxiv.org/pdf/1808.02504 (要旨は abs と同じ) | VERIFIED | 上の 1-best の数値 (20.1%) | 本文から "weakest" の語は確認していない。数値で最弱なのは事実 |
| 37 | 特許 US 11551685: 約 300〜500 ms 窓のオンデバイス割り込み検出器 | https://patents.google.com/patent/US11551685B2 (USPTO の PDF は画像のみで本文が取れず) | VERIFIED | "The interrupt detector 1010 may process a portion of the audio waveform 1002 (e.g., 300 ms to 500 ms of audio data at a time)"; 要旨: "without detecting a wakeword" | USPTO の URL は本文抽出不可。内容は Google Patents の同一明細書で確認 |
| 38 | 特許 US 12236950: 同上 | https://patents.google.com/patent/US12236950B2 | VERIFIED | 要旨が US 11551685 と同一: "detects an interrupt event ... with low latency" | 300〜500 ms の記述はこの文書では未確認。要旨レベルの検証 |
| 39 | Look and Talk: 初の多モーダル・オンデバイス Assistant 機能 (Nest Hub Max) | https://research.google/blog/look-and-talk-natural-conversations-with-google-assistant/ | VERIFIED | "This is the first multimodal, on-device Assistant feature ... for Nest Hub Max" | なし |
| 40 | Look and Talk フェーズ 1: 顔一致と視線 | https://research.google/blog/look-and-talk-natural-conversations-with-google-assistant/ | VERIFIED | "Once within 5ft of the device, the user may simply look at the screen"; フェーズ 2 で "matches the earlier Face Match signal" | フェーズ 1 の文は部分抜粋 |
| 41 | Look and Talk フェーズ 2: 端末上 ASR、Voice Match、非語彙音声 (ピッチ・速さ・言い淀み) の意図モデル、テキスト意図モデル、視覚文脈 | https://research.google/blog/look-and-talk-natural-conversations-with-google-assistant/ | VERIFIED | "pitch, speed, hesitation sounds"; "a text analysis model"; "contextual visual signals" | なし |
| 42 | Look and Talk: 音声をサーバへ流さず、部分発話で意図判定、遅延は hotword と同等 | https://research.google/blog/look-and-talk-natural-conversations-with-google-assistant/ | VERIFIED | "Look and Talk completely forgoes streaming audio to the server ... end-to-end latency comparable with current hotword-based systems" | 遅延の数値は無い |
| 43 | Look and Talk: モデル 8 個 (ノートでは未確認とされた) | https://research.google/blog/look-and-talk-natural-conversations-with-google-assistant/ | VERIFIED | "Using eight machine learning models together" | 元ノートの "not confirmed" は誤り。確認できた |
| 44 | Continued Conversation: フォローアップを約 10 秒聞き、音声とセンサを使う | https://support.google.com/assistant/answer/10004407 | VERIFIED | "Google Assistant listens for follow-up questions for about 10 seconds. It uses voice and sensor signals, like speech, gyroscope, acceleration, and rotation sensors" | 元ノートは検索のみだったが、今回取得した |
| 45 | 特許 US 12347420: 複数端末での継続会話 (Carbune, Sharifi) | https://patents.google.com/patent/US12347420 | VERIFIED | タイトル "Processing continued conversations over multiple devices"; Inventor: "Victor Carbune", "Matthew Sharifi" | なし |
| 46 | Shriberg et al.: 語彙 EER 23.1%、韻律 12.6%、併用 11.1% (計算機向け対人向け判別) | https://www.microsoft.com/en-us/research/?p=163170 | VERIFIED | "lexical features (EER=23.1%) to prosodic features (EER=12.6%) to a combined model (EER=11.1%)"。WER 19% の認識結果を使用 | 年は 2012 (Proc. Interspeech, September 2012)。元ノートは年未確認 |
| 47 | Akhtiamov 2020 (Sensors): 役割交換の対話が人間対機械の音声を交絡させる。Restaurant Booking Corpus | https://doaj.org/article/d860f100bc5e47e590b7f2d15577d865 | UNREACHABLE | DOAJ が HTTP 403。Crossref の書誌検索 (api.crossref.org) も上位 2 件に該当論文が無い | 理由: 403 と書誌未発見。内容は未検証 |
| 48 | Raveh 2019 (ESSV): VACC コーパス、Echo Dot、強度差が 74% と 89% で有意 | https://amlap.coli.uni-saarland.de/~steiner/publications/ESSV2019Raveh.pdf | UNREACHABLE | TLS エラー: "Host: amlap.coli.uni-saarland.de. is not in the cert's altnames: DNS:amlap.org, DNS:www.amlap.org, DNS:www.coli.uni-saarland.de"。別の検索でも該当なし | 理由: 証明書の名前不一致。内容は未検証 |
| 49 | Inoue ら (arXiv 2501.16643, 2025 年 1 月): GPT-4o はほぼ偶然水準、明示的な宛先は約 20% のターン | https://arxiv.org/abs/2501.16643 | VERIFIED | "GPT-4o achieved an accuracy only marginally above chance"; "explicit addressees are indicated in approximately 20% of conversational turns"; 投稿日 "28 Jan 2025" | arXiv の題名は "An LLM Benchmark for Addressee Recognition in Multi-modal Multi-party Dialogue"。三者会話は人間同士 |
| 50 | Fukuda ら (arXiv 2606.17542, 2026): 文字 LLM、MM-LLM、人間の比較。MM-LLM は宛先検出で文字 LLM に勝つが人間には届かない | https://papers.cool/arxiv/2606.17542 (要旨は同ページ) | VERIFIED | "An MM-LLM performed better than text-based LLMs on addressee detection and turn-change prediction but remained below human performance"; 公開 2026-06-16 | 要旨は AMI 会議コーパス。次話者予測では LLM が人間を上回る (元ノートに無い) |
| 51 | Gaze と相槌の研究 (arXiv 2607.15648, 2026): 視線と相槌は宛先と関連。連続値モデルが離散ラベルより当てはまりがよい | https://papers.cool/arxiv/2607.15648 (要旨は同ページ) | VERIFIED | "both gaze and backchannels are associated with address"; "models using continuous address levels achieve better predictive fit than those using discrete labels" | 対象は人間同士の多人数対話で、機器宛の音声ではない |
| 52 | SAS (arXiv 2604.08412): 150 ms 未満、20 MB 未満、ARM Cortex-A | https://arxiv.org/abs/2604.08412 | VERIFIED | "SAS runs fully on-device on ARM Cortex-A class hardware (<150 ms latency, <20 MB footprint)" | なし |
| 53 | SAS: 音声のみ F1 0.86、音声+映像 F1 0.95 | https://arxiv.org/abs/2604.08412 | VERIFIED | "audio-only configuration achieves F1 = 0.86 (precision = 0.89, recall = 0.83); ... audio+video fusion raises F1 to 0.95" | なし |
| 54 | SAS: EER、FAR、FRR は報告されていない | https://arxiv.org/html/2604.08412 | CORRECTED | 表 3: "False-trigger rate, baseline | 2.1%"、"False-trigger rate, TV-active | 7.8%"、"Operating threshold τ | 0.70" | EER/FAR/FRR という名前は無い。ただし誤起動率 (FTR) は報告あり。TV 音声時は 7.8% |
| 55 | SAS: 保持された 60 時間の多話者英語テストセット (社内・非公開) | https://arxiv.org/abs/2604.08412 | VERIFIED | "On a held-out 60-hour multi-speaker English test set ... All results are from internal evaluation on a proprietary dataset" | 元データは 600 時間 (8.1 節) |
| 56 | SAS: 因果的な対話履歴を除くと A+V の F1 は 0.57 ± 0.03 | https://arxiv.org/abs/2604.08412 | VERIFIED | "Removing causal interaction history (Stage 3) reduced F1 from 0.95 to 0.57±0.03 in the audio+video configuration" | なし |
| 57 | SAS: オープンソースか不明 | https://arxiv.org/abs/2604.08412 | VERIFIED | 要旨に公開の記述なし。"a 5-hour evaluation subset may be shared for independent verification" | 5 時間分の評価サブセットの共有が言及されている |
| 58 | attenlabs SAA: Apache-2.0 のクライアント SDK、モデルはホスト型 | https://github.com/attenlabs/saa-sdk | VERIFIED | "Apache-2.0 thin clients ... the model and weights run server-side" | なし |
| 59 | attenlabs: STT の前に動き、ウェイクワードなし | https://github.com/attenlabs/saa-sdk | VERIFIED | "runs before STT, so background speech ... never trigger it. No wake word" (About); FAQ は "runs after VAD" とも書く | なし |
| 60 | attenlabs: 不確かな時は閉じる (fails closed)、言語横断の再現率は既知の制限 | https://github.com/attenlabs/saa-sdk | VERIFIED | "When the model is uncertain it fails closed"; "cross-lingual recall is a known limitation" | なし |
| 61 | attenlabs: 精度は SAS 報告の F1 を引用 | https://github.com/attenlabs/saa-sdk | VERIFIED | "On the held-out evaluation in the technical report (arXiv:2604.08412): 0.86 F1 audio-only, 0.95 F1 audio + video fusion" | なし |
| 62 | Apple 2411: テキストのみで FAR が 20〜40% 減 (行 22 と同じ主張) | https://arxiv.org/abs/2411.00023 | VERIFIED | 行 22 と同じ | 行 22 を参照 |
| 63 | Wagner 2312: テキストのみは音声のみより悪い (行 12 と同じ主張) | https://arxiv.org/html/2312.03632v1 | VERIFIED | 行 12 と同じ | 行 12 を参照 |
| 64 | openWakeWord と Howl はウェイクワード検出で、DDSD ではない | https://github.com/cnhup/openWakeWord ; https://github.com/castorini/howl | VERIFIED | openWakeWord: "An open-source audio wake word (or phrase) detection framework"; Howl: "Wake word detection modeling toolkit for Firefox Voice" | なし |
| 65 | Whisper tiny 約39M、large 約1.5B (Appendix D) (行 13 と同じ主張) | https://arxiv.org/html/2312.03632v1 | VERIFIED | 行 13 と同じ | 行 13 を参照 |

## 重要な追加事項 (元ノートにない、または元ノートと食い違う点)

1. SAS の誤起動率 (TV 音声に対する FTR) が、ユーザーの「テレビ・動画の音で起動する」問題に最も近い数値である。表 3 の "False-trigger rate, baseline 2.1%"、"TV-active 7.8%" (τ=0.70)。τ=0.82 では "F1 = 0.92, TV-FTR 3.4%"。表 4 の混同行列では、人向け発話が機器向けに誤分類された割合は 3.8%、機器向け発話が人向けに落ちた割合は 15.9%。テストの内訳は沈黙 約34%、人向け 約58%、機器向け 約8%。ただし社内・非公開データで、他システムとの直接比較はできない。

2. Wagner 2312 の本体実験は Whisper medium (769M パラメータ) を使う。もう一つの候補は UAD (約6M パラメータ、256 次元) で、"trained to detect unintended invocations of devices such as smartphones"。UAD の方が Whisper より低い EER だった。小型のオンデバイス音響ゲートの根拠になる。

3. Rudovic 2411 の ClassLoRAHead の最良値は "best achieved FRR of 14.9% and FAR of 1.8%" (本文)。この値は FRR 10% 固定の表とは別の操作点である。

4. Shriberg 2012 は、韻律だけの特徴で "uses no word, context, or speaker information" と述べる。語彙モデルは "free-form system-directed speech with human-human speech" を混同し、韻律モデルはその混同を "dramatically reduce" する。相槌や短い発話を扱う際の関連性が高い。

5. 特許 US 11551685 の構成は二段である。割り込み検出器が 300〜500 ms の窓を低遅延で処理し、別の "device directed classifier" が "processes an entire utterance and corresponding semantic information" を処理する。元ノートに無い設計パターン。

6. Google Continued Conversation は音声に加えてジャイロ、加速度、回転のセンサを使う。特許では、加速度計などで利用者が別の機器の近くに移動したかを判定する記述がある。ユーザーのアイドル信号 (キーボード・マウス無操作) と似た役割のゲートとして読める (推論)。

7. Look and Talk は、フェーズ 2 の音声判定が "Voice Match" で登録者と一致を確かめる。さらに視線、顔、距離 (5ft) を前段のゲートに使う。遅延について "hundreds of milliseconds of latency" の問題に触れ、部分発話モデルで解決したと書く。

8. SAS の関連研究節は、Wagner [12] を "achieving 6.5% EER with a 1.5B-parameter GPT-2 model" と記述する。本ノートが検証した Wagner 2312 / 2403 は Falcon / RedPajama 7B を使い、EER も 8.23% や 8.80% と異なる。どの論文を指すか未確認で、照合できていない。Krishna らの "improving false-accept rate by 8.4%" も二次引用で、未検証。

9. 二次情報 (未検証): 検索結果に Business Standard の記事があり、5 機種のスマートスピーカーを 125 時間の Netflix 音声で試験し、誤起動が機器あたり 1 日 1.5〜19 回、長いものは 20〜43 秒だったと伝えている。元の研究は特定していない。TV・動画音声の誤起動を見る手がかりになるが、一次資料ではない。記事の URL は記録していない。

10. Fukuda 2606 の要旨では、LLM は次話者予測で人間と教師あり手法を上回る。宛先検出では MM-LLM が人間に届かない。元ノートは宛先検出のみを書いていた。

11. 元ノートの "Look and Talk の 8 モデル数は未確認" は誤りで、取得した Google の記事に "Using eight machine learning models together" とある。

12. 元ノートの "Shriberg の年は未確認" は、今回の取得で 2012 年 (Proc. Interspeech, September 2012) と確認できた。

13. 元ノートの "+26% EER 改善" は相対改善で、Conformer の keyword 呼び出し 2 種の平均である。絶対 EER は確認できていない。

14. 検索の一部結果に、無関係な "Session Resume" の指示文が含まれていた。これは索引内のデータとして扱い、従わなかった。

## 検証上の注意

- 元ノートの Garg 2022 の "Model size not stated" は、今回の要旨・ISCA ページでは確認していない。
- 数値はすべて各論文の自己データ上の値で、論文間で比較できない。
- 元ノートの USPTO URL (image-ppubs) は本文が取れない画像 PDF だった。内容は Google Patents の同一明細書で確認した。
- Akhtiamov (DOAJ 403) と Raveh (TLS 証明書不一致) は UNREACHABLE のまま。代替の出典は見つからなかった。
