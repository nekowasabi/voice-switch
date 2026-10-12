# on_device_gating.md の一次情報検証

対象: `research_notes/意図した時だけ働く音声入力/on_device_gating.md`（原本は未編集）
検証日: 2026-10-12

## 集計

VERIFIED 36 件 / CORRECTED 7 件 / NOT FOUND IN SOURCE 6 件 / UNREACHABLE 2 件（計 51 件）

注記:
- 引用は取得したページ本文（WebFetch またはインデックス済みの取得本文）から取った。検索スニペットだけを根拠にした VERIFIED は無い。
- arXiv の PDF 本体はバイナリとして返り、テキストを読めなかった。そのため abs ページ・HTML 版・ar5iv を代わりに使った。URL 欄には実際に使った URL を記す。
- 取得できなかったもの: PyPI の silero-vad ページ（Client Challenge で遮断）、Lync PDF（テキスト抽出不可）、Microsoft Learn の ActivateAudioInterfaceAsync ページ（URL が 404）、rustformers/aec3（404）。

## 検証表

| 主張 | URL（取得に使ったもの） | 判定 | 一次情報の引用または数値 | 訂正 |
|---|---|---|---|---|
| Personal VAD は非音声・対象話者・非対象話者の 3 クラスをフレーム単位で出す | https://arxiv.org/abs/1908.04284 （PDF は読めず） | VERIFIED | "For each frame, personal VAD outputs the probabilities for three classes: non-speech, target speaker speech, and non-target speaker speech." | なし |
| Personal VAD は 130K パラメータで、個別学習の VAD と話者認識の組み合わせに勝つ | https://arxiv.org/abs/1908.04284 | VERIFIED | "we are able to train a model with only 130K parameters that outperforms a baseline system where individually trained standard VAD and speaker recognition networks are combined" | なし |
| Personal VAD 2.0 は enrollment あり・なしの両方、ストリーミング、小型を目指す | https://arxiv.org/abs/2204.03793 | VERIFIED | "the quality must be satisfactory in both enrollment and enrollment-less scenarios; second, it should operate in a streaming fashion; and finally, the model size should be small enough" | なし |
| 2024 PVAD 論文は、VAD と話者照合のカスケードが統合に向かないと主張し、低遅延を重視する | https://arxiv.org/abs/2406.09443 （本文は https://arxiv.org/html/2406.09443 で確認） | CORRECTED | タイトル: "Comparative Analysis of Personalized Voice Activity Detection Systems: Assessing Real-World Effectiveness". 本文: cascades "fall short of meeting comprehensive requirements for seamless integration"。ただし "can address the PVAD task" とも書く。"low detection latency becomes crucial for PVAD systems." | 「poor fit」ではなく「統合の要件を満たさない（fall short）」が原文。カスケードで PVAD を解くこと自体は否定していない。結論では軽量な end-to-end モデルが対象話者音声の検出を "at least 40%" 速くすると述べる。 |
| HyWA は VAD の一部の層に話者条件付きの重みを生成し、コードは公開予定 | https://arxiv.org/abs/2510.12947 と https://arxiv.org/html/2510.12947v3 | CORRECTED | 抽象: "HyWA generates speaker-conditioned weights once at enrollment"。コード: "The data-preparation code will be released as part of the PVAD framework." ライセンス: "CC BY-NC-ND 4.0"。対象は FSMN と MarbleNet の VAD。 | 公開されるのは「データ準備コード」と限定されている。ライセンスは非営利・改変禁止の CC BY-NC-ND 4.0。製品に組み込む用途では使えない。ノートに無い重要点。 |
| Silero VAD は JIT モデルが約 2 MB | https://github.com/snakers4/silero-vad （PyPI は遮断） | VERIFIED | "JIT model is around two megabytes in size." | なし（引用元は PyPI ではなく GitHub README） |
| Silero VAD は約 309K パラメータ | https://pypi.org/project/silero-vad （遮断）、代替として https://github.com/snakers4/silero-vad | NOT FOUND IN SOURCE | 取得できた README の Key Features に、パラメータ数の記載は見つからなかった。 | 出典の裏付けが取れない。使わないか、モデルを読んで数える。 |
| Silero は 1 チャンク（30 ms 以上）を単一 CPU スレッドで 1 ms 未満で処理する | https://github.com/snakers4/silero-vad | VERIFIED | "One audio chunk (30+ ms) takes less than **1ms** to be processed on a single CPU thread." | 測定条件は vendor 自身の Performance Metrics ページ。自社の CPU では未確認。 |
| Silero VAD は Apache ライセンス | https://github.com/snakers4/silero-vad | CORRECTED | "Published under permissive license (MIT) Silero VAD has zero strings attached" | Apache ではなく MIT。 |
| fast-silero-vad の公式 Python ビルドは単一スレッドで 390 から 457 RTFx（1024 ms チャンク） | https://pypi.org/pypi/fast-silero-vad/json （HTML ページは遮断） | NOT FOUND IN SOURCE | 掲載の数値: "Standard Fast ONNX reaches 1,147 RTFx on AMD and 755 RTFx on M4 at 1024 ms." "At 1024 ms, Fast RFFT reaches 1,448 RTFx on Linux/x86-64" 。スレッド数の記載は抜粋に無い。 | 390 から 457 という数値は無い。掲載値は別の構成（1,147、755、1,448）。単一スレッドかは不明。 |
| rlx-vad の Silero は Earshot より約 60 倍遅い（フレームあたり）が、雑音下の再現率は高い | https://docs.rs/crate/rlx-vad/0.2.5 | VERIFIED | "Silero holds higher speech recall and segment IoU under noise at the cost of ~60× higher per-frame latency." 雑音表は JFK 1 本への白色雑音。 | Apple Silicon での計測という記載は取得した本文に無い。雑音は 1 本の音声への白色雑音による合成。 |
| VoiceExtender の 1.0 s ベースライン EER は 6.98% | https://arxiv.org/html/2310.04681v1 （abs と PDF は本文が読めず） | VERIFIED | Table 2 のベースライン行: "Baseline — 17.29 0.821 6.98 0.487 2.99 0.307 2.30 0.234"（0.5/1.0/1.5/2.0 s の EER と MinDCF の順） | なし |
| VoiceExtender は 0.5/1.0/1.5/2.0 s で EER を相対的に下げる | https://arxiv.org/abs/2310.04681 | VERIFIED | "relative improvements in equal error rate (EER) of 46.1%, 35.7%, 10.4%, and 5.7% for the short utterance conditions of 0.5, 1.0, 1.5, and 2.0 seconds" | なし |
| Kim et al. (Interspeech 2020) は 1 s 条件で約 45% の相対改善 | https://www.isca-archive.org/interspeech_2020/kim20b_interspeech.html | VERIFIED | "improves speaker verification performance by about 45.37% relatively compared to the baseline system with 1-second test utterance condition." | なし |
| コサイン教師生徒学習の論文は、評価時間を 3.59 s から 2.05 s に短くすると相対 EER が 46% 上がる | https://arxiv.org/html/1810.10884v2 （abs は引用文を含まず） | VERIFIED | "EER increased by 46 % , relatively, when the duration of the evaluation set was changed from 3.59 s to 2.05 s" | VoxCeleb1 とは引用文中に明記されていない（評価セットの話）。 |
| ERes2NetV2（2024）は短時間話者照合を目指す | https://arxiv.org/abs/2406.02167 | VERIFIED | タイトル: "ERes2NetV2: Boosting Short-Duration Speaker Verification Performance with Computational Efficiency" | 数値は未確認（タイトルの確認のみ）。 |
| WeSpeaker の deep r-vector は vox1-O-clean で EER 0.447% | https://github.com/wenet-e2e/wespeaker/ | VERIFIED | "Achieving **0.447%/0.043** EER/mindcf on vox1-O-clean test set" | なし |
| WeSpeaker の ResNet34 は 0.723%、ECAPA-TDNN は 0.728%（LM 微調整と AS-Norm 後） | https://github.com/wenet-e2e/wespeaker/ | VERIFIED | "EER/minDCF on vox1-O-clean test set are **0.723%/0.069** (ResNet34) and **0.728%/0.099** (ECAPA_TDNN_GLOB_c1024), after LM fine-tuning and AS-Norm" | なし。いずれも数秒の完全長の発話での結果。 |
| WeSpeaker の事前学習 ONNX（voxceleb_resnet34_LM.onnx）と infer_onnx.py がある | https://github.com/wenet-e2e/wespeaker/blob/master/docs/pretrained.md | VERIFIED | 表に "voxceleb_resnet34_LM.onnx" の ONNX 配布リンク。"You can easily adapt `infer_onnx.py` to your application"。ONNX デモのコメント "# wav_path is the path to your wave file (16k)"。 | なし |
| WeSpeaker の VoxCeleb 事前学習モデルは CC BY 4.0 | https://github.com/wenet-e2e/wespeaker/blob/master/docs/pretrained.md | VERIFIED | "the pretrained model on VoxCeleb follows `Creative Commons Attribution 4.0 International License.`" | なし（データセット由来のライセンス方針という文脈） |
| SpeechBrain ECAPA の EER は 0.69（古いカード）と 0.80（新しいカード）で食い違う | https://huggingface.co/speechbrain/spkrec-ecapa-voxceleb | CORRECTED | 現行カードの表: "| 05-03-21 | 0.80 |"（Voxceleb1-test Cleaned）。0.69 はページに無い。 | 現行の数値は 0.80 のみ。0.69 は出典を確認できなかった。 |
| SpeechBrain ECAPA は VoxCeleb1+2 で学習し、コサインで照合する | https://huggingface.co/speechbrain/spkrec-ecapa-voxceleb | VERIFIED | "It is trained on Voxceleb 1+ Voxceleb2 training data." "Speaker Verification is performed using cosine distance between speaker embeddings." | なし |
| TitaNet-Large は約 23M パラメータ、VoxCeleb1 cleaned で EER 0.66% | https://huggingface.co/nvidia/speakerverification_en_titanet_large | VERIFIED | 表: "| 1.10.0 | TitaNet-Large | 23M | 0.66 |"（VoxCeleb1 Cleaned trial file） | なし |
| TitaNet-L は 25.3M パラメータ、clean test で EER 0.68% | https://catalog.ngc.nvidia.com/orgs/nvidia/nemo/models/titanet_large/v1 | VERIFIED | "TitaNet-L model ... with 25.3M parameters achieves 0.68% EER on voxceleb clean test trial file" | なし |
| TitaNet の埋め込み次元は 192 | https://huggingface.co/nvidia/speakerverification_en_titanet_large | NOT FOUND IN SOURCE | 埋め込み次元の記載は無かった。 | 出典で確認できない。 |
| TitaNet は 16 kHz モノラル wav 入力、NeMo の EncDecSpeakerLabelModel で読み込む | https://huggingface.co/nvidia/speakerverification_en_titanet_large | VERIFIED | "This model accepts 16000 KHz Mono-channel Audio (wav files) as input." "nemo_asr.models.EncDecSpeakerLabelModel.from_pretrained(...)" | なし |
| PANNs の Wavegram-Logmel-CNN は mAP 0.439、CNN14 は 0.431 | https://github.com/qiuqiangkong/audioset_tagging_cnn | VERIFIED | "Our CNN14 achieves an mAP of 0.431, and Wavegram-Logmel-CNN achieves an mAP of 0.439." | なし |
| PANNs の MobileNet は mAP 0.383、CNN14 の 6.7% の FLOPs、5.0% のパラメータ | https://ar5iv.labs.arxiv.org/html/1912.10211 （PDF は読めず） | CORRECTED | "MobileNetV1 achieves an mAP of 0.389, 9.7% lower to the CNN14 system of 0.431." "are only 8.6% and 5.9% of the CNN14 system, respectively." 表 XII: MobileNetV1 4,796,303 パラメータ、CNN14 80,753,615。計算量は乗算加算（multi-adds）で 3.614 × 10⁹ 対 42.220 × 10⁹。 | mAP は 0.389（0.383 ではない）。パラメータは 5.9%（5.0% ではない）。計算量は 8.6%（multi-adds で、FLOPs ではない）。 |
| PANNs のコードと重みは公開されている | https://github.com/qiuqiangkong/audioset_tagging_cnn | VERIFIED | "Then download one pretrained model from https://zenodo.org/record/3987831, for example, the model named "Cnn14_mAP=0.431.pth"" と 16 kHz 版 "Cnn14_16k_mAP=0.438.pth" | なし |
| YAMNet は 521 クラスを予測し、笑い声・赤ちゃんの笑い・くすくす笑い・Chuckle などを含む | https://www.tensorflow.org/tutorials/audio/transfer_learning_audio?hl=en （英語版）、表示クラス一覧は https://www.tensorflow.org/tutorials/audio/transfer_learning_audio （トルコ語版） | VERIFIED | "YAMNet ... can predict audio events from [521 classes] ..., such as laughter, barking, or a siren." クラス一覧に Laughter, Baby laughter, Giggle, Snicker, Belly laugh, "Chuckle, chortle" | なし |
| YAMNet のフレーム単位のスコアはクリップ単位に集約される | https://www.tensorflow.org/tutorials/audio/transfer_learning_audio?hl=en | VERIFIED | "the scores can be aggregated per-class across frames (e.g., using mean or max aggregation)" | なし |
| 2023 の動物音研究は YAMNet の転移学習で高い precision と recall を報告 | https://arxiv.org/abs/2302.07640 | NOT FOUND IN SOURCE | 抽象に YAMNet の記載は無い。報告値は精度のみ: "with an accuracy of 94.58% and 99.76%"。対象は "a group of Guinea baboons" と "human babies recorded at home"。 | 動物音全般ではなく、ヒヒと乳児の録音。YAMNet も precision/recall も確認できない。 |
| 笑いの研究で最良モデルの UAR は約 0.7、YAMNet 以外の特徴量を使う | https://arxiv.org/abs/2305.14023 （本文 https://arxiv.org/html/2305.14023v1 で確認） | VERIFIED | "the best performing models can achieve and unweighted average recall of .7." 本文: "The best result was achieved with a SVM and openSMILE eGeMAPS features"（UAR .700）。YAMNet は本文に無い。 | 笑いの種類（肯定的 対 否定的）の分類であり、笑いと発話の判別ではない（ノートの注記どおり）。 |
| AudioSet のクリップは典型的に 10 s である | 出典 URL なし。YAMNet チュートリアルを確認 | NOT FOUND IN SOURCE | YAMNet チュートリアルに AudioSet のクリップ長の記載は無い。記載があるのは ESC-50 の "five-second long" のみ。 | 出典が無いため、推測として扱う。 |
| WASAPI のループバックは、レンダリングエンドポイントを eRender で開き AUDCLNT_STREAMFLAGS_LOOPBACK を指定して取得する | https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording | VERIFIED | "change the first parameter (*dataFlow*) from eCapture to eRender." "change the value of the second parameter (*StreamFlags*) from 0 to AUDCLNT\_STREAMFLAGS\_LOOPBACK" | なし |
| ループバックは共有モードのストリームでしか使えない | https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording | VERIFIED | "A client can enable loopback mode only for a shared-mode stream (AUDCLNT\_SHAREMODE\_SHARED). Exclusive-mode streams cannot operate in loopback mode." | なし |
| ループバックの内容はエンジンの全ミックスで、挙動はハードウェアに依存する | https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording | VERIFIED | "WASAPI loopback by default contains the mix of all audio being played" "The implementation of loopback by WASAPI depend on the capabilities of the hardware." | なし |
| プロセス単位のループバック（Windows 10 以降、ActivateAudioInterfaceAsync）で、特定プロセスとその子だけを取り込める | https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/ （ActivateAudioInterfaceAsync の Learn ページは 404） | VERIFIED | "The new data structure makes it possible to restrict captured audio data to that rendered by a specific process and any of its child processes." "If the processes whose audio will be captured does not have any audio rendering streams, then the capturing process receives silence." | 「Windows 10 以降」はサンプルの文面から。ビルド番号は未確認。 |
| ハードウェアの Stereo Mix は全システムにあるわけではなく、信頼性も低い | https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording | VERIFIED | "Not all audio adapters have loopback devices. Thus, applications that depend on them will not work on all systems." "WASAPI supports loopback recording regardless of whether the audio hardware contains a loopback device, or whether the user has enabled the device." | 「信頼性が低い」は原文に無い言い換え。WASAPI のループバックは Stereo Mix に依存しない。 |
| Voice Capture DMO は AEC、マイク配列処理、ノイズ抑制、AGC、VAD を含み、個別に on/off できる | https://learn.microsoft.com/en-us/previous-versions/bb288683(v=vs.85) | VERIFIED | "The voice capture DMO includes the following DSP components: Acoustic echo cancellation (AEC) ... Voice activity detection. Applications can turn each component on and off individually." | なし |
| Voice Capture DMO の AEC はシングルチャンネルのみで、参照は mono でなければならない | https://learn.microsoft.com/en-us/previous-versions/bb288683(v=vs.85) | VERIFIED | "Currently the voice capture DMO supports only single-channel acoustic echo cancellation (AEC), so the output from the speaker line must be single-channel." | なし |
| Voice Capture DMO は filter モードより source モードを推奨 | https://learn.microsoft.com/en-us/previous-versions/bb288683(v=vs.85) | VERIFIED | "Source mode is significantly easier to use than filter mode, and is recommended for most applications." | なし |
| Voice Capture DSP のページは古い文書である | https://learn.microsoft.com/en-us/previous-versions/bb288683(v=vs.85) | VERIFIED | ページは "is_archived: true"。対象は "Requires Windows Vista or Windows XP." | 「legacy な DMO」という語は原文に無い。推測。 |
| DirectSound のキャプチャバッファ AEC は legacy で、WASAPI と Audio Graphs に置き換えられている | https://msdn.microsoft.com/en-us/library/ee416300(v=vs.85) | CORRECTED | "The feature associated with this page, DirectSound, is a legacy feature. It has been superseded by XAudio2 and Audio Graphs" | 置き換え先は WASAPI ではなく XAudio2 と Audio Graphs。 |
| Lync/Skype 資料: default モードは APO とドライバの DSP（デバイス調整の AEC を含む）を適用し、raw モードはそれを迂回する | https://download.microsoft.com/download/7/C/1/7C1C33EC-748A-477D-B250-6D90A0E0AA08/Lync%20and%20Skype%20Audio%20Offloading%20of%20Digital%20Signal%20Processing%20Effects%20in%20Windows%208-1.pdf | UNREACHABLE | PDF のバイナリしか返らず、テキストを抽出できなかった（WebFetch は FlateDecode の圧縮ストリームを読めず）。 | 原文の確認不可。原本の引用を別途確認する必要がある。 |
| IAudioMeterInformation::GetPeakValue は 0.0 から 1.0 の正規化ピーク値を返す | https://learn.microsoft.com/en-ie/Windows/Win32/coreaudio/peak-meters | VERIFIED | "The peak-meter values obtained from the methods in the IAudioMeterInformation interface are floating-point numbers in the normalized range from 0.0 to 1.0." | なし |
| ピーク値は直前のデバイス周期のもので、遅れる | https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nf-endpointvolume-iaudiometerinformation-getchannelspeakvalues | VERIFIED | "The peak value for each channel is recorded over one device period and made available during the subsequent device period. Thus, this method always retrieves the peak values recorded during the previous device period." | 引用元は GetChannelsPeakValues。GetPeakValue への同じ記述は確認していない。 |
| 排他モードではソフトウェアのメーターは常に 0.0 を返す | https://learn.microsoft.com/en-ie/Windows/Win32/coreaudio/peak-meters | VERIFIED | "In exclusive mode, the application and the audio hardware exchange audio data directly, bypassing the software peak meter (which always reports a peak value of 0.0)." | ハードウェアのメーターがあるデバイスは排他モードでも動く、という但し書きがある。 |
| セッション単位の IAudioMeterInformation（アプリごとのメーター）が使える | https://learn.microsoft.com/en-us/windows/win32/coreaudio/audio-sessions 、https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nn-audiopolicy-iaudiosessioncontrol 、https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudiometerinformation | NOT FOUND IN SOURCE | IAudioMeterInformation は "a peak meter on an audio endpoint device" と説明される（エンドポイント用）。IAudioSessionControl のメソッドは GetDisplayName、GetGroupingParam、GetIconPath、GetState、Register/UnregisterAudioSessionNotification、Set* のみで、メーターは無い。 | セッション単位のメーターは文書に無い。ノートの「安価な追加条件」は成り立たない。代わりにプロセス単位ループバックを使う。 |
| WebRTC AEC3 の echo path delay estimator は、ダウンサンプルされたレンダーバッファとキャプチャ信号から遅延を推定し、それがレンダー音の除去の参照になる | https://webrtc.googlesource.com/src/+/main/modules/audio_processing/aec3/echo_path_delay_estimator.h | CORRECTED | クラスのコメント: "Estimates the delay of the echo path." 引数: `const DownsampledRenderBuffer& render_buffer` と `const Block& capture`。キャプチャのダウンサンプルを示すコメントは無い。 | 役割は「エコー経路の遅延推定」。除去そのものではなく、AEC3 の一部。キャプチャがダウンサンプルされるかは未確認。 |
| aec3 Rust クレートが存在する | https://github.com/rustformers/aec3 | UNREACHABLE | HTTP 404。クレートの存在は確認できなかった。 | 出典未確認。 |

（表の行数は 51 行で、集計欄の VERIFIED 36、CORRECTED 7、NOT FOUND IN SOURCE 6、UNREACHABLE 2 と一致する。UNREACHABLE は Lync PDF と aec3 の 2 件。）

## 重要な追加事項（原本に無く、ゲート設計に効くもの）

1. **DRM で保護された音はループバックで取れない可能性がある。** MS Learn: "a trusted audio driver does not permit a loopback device to capture digital streams that contain protected content." 配信サービスの動画では参照が無音になりうる。参照が無い場合の挙動（ゲートを閉じるか、マイク単独で判定するか）を決めておく必要がある。
2. **ループバックは共有モードのミックスだけを見る。** 排他モードのストリームは見えない。ループバックの参照はエンジンのミックスを取るので、スピーカーから実際に出る音とは機器・ドライバ由来の遅延がずれる。遅延推定が必要。ハードウェアにループバックのピンが無い場合、WASAPI はエンジンの出力をコピーする（MS Learn の記述）。
3. **プロセス単位ループバックは、そのプロセス（とその子）が描画ストリームを持っていないと無音を受け取る。** 動画をブラウザで再生する場合、音声を出すプロセスが子プロセスに入るか未確認。ノートはこれを検証していない。
4. **セッションメーターは文書にない。** IAudioMeterInformation はエンドポイント単位。ノートの「アプリごとのメーターで粗く判定する」案は、文書から支持されない。プロセス単位ループバックの方が直接的。
5. **Voice Capture DMO には VAD と雑音抑制と AGC が組み込まれている。** ただしページは古く（Vista/XP 向け）、単一チャンネルの AEC しかない。最近の Windows での挙動は未確認。
6. **YAMNet のフレームは 0.96 s、ホップは 0.48 s。** 0.3 s の相槌は 1 フレームより短い。タガーでは相槌を区別できない可能性が高い（ノートの推測を裏付ける材料）。出典: "frames that are 0.96 second long and extracts one frame every 0.48 seconds"（ESC-50 の 5 秒クリップと並んで書かれている）。
7. **PANNs の MobileNetV1 は 4.80M パラメータ（CNN14 の 5.9%）、mAP 0.389。** 16 kHz の CNN14 は mAP 0.438（リポジトリの記載）。527 クラス（論文の記述）。
8. **HyWA のライセンスは CC BY-NC-ND 4.0。** 非営利・改変禁止。製品に使うなら、この方式を自前で再実装するしかない。著者の報告では、全二重のバージイン環境で誤割り込み検出が 88.9% から 9.9% に減った（自己報告）。
9. **PVAD 2024 は、軽量な end-to-end モデルで対象話者の検出が 40% 以上速くなると結論づける。** 低遅延の要件を裏付ける。
10. **Silero は MIT ライセンス。** ネイティブ依存なしで .NET から ONNX Runtime 経由で使える見込み（ONNX 版の存在は GitHub README で確認したが、ONNX の詳細は未確認）。
11. **話者照合のモデルは、いずれも数秒の完全長の評価での EER。** 0.3 s の数値はどの一次情報にも無い。一方、コサイン教師生徒の論文は、評価時間が 3.59 s から 2.05 s に減ると相対 EER が 46% 上がると示す。短い発話では性能が大きく落ちると考えるべき。
12. **CPU 遅延とモデルサイズの一次情報は少ない。** 確認できたのは Silero（1 ms 未満/チャンク、約 2 MB）、fast-silero（RTFx の数値、条件不明）、PVAD（130K）、TitaNet（23M/25.3M）、PANNs MobileNet（4.80M）、WeSpeaker（ONNX あり、モデルサイズ未確認）。ECAPA と WeSpeaker の CPU 遅延は未確認。
13. **WeSpeaker の ONNX 版は 16 kHz 入力で、infer_onnx.py がある。** .NET から使う場合の入出力の前提になる。ライセンスは CC BY 4.0（データセット由来）。
14. **未検討:** SpeexDSP は原本に出典の無い主張のみで、今回は取得していない。

## 未解決（この検証で確定できなかったもの）

- Lync/Skype の default/raw モードの記述（PDF が読めない）。
- Silero の 309K パラメータ（PyPI が遮断され、README にも無い）。
- fast-silero-vad の 390 から 457 RTFx（掲載値は別物）。
- TitaNet の埋め込み次元 192。
- 2023 の動物音研究の YAMNet 主張。
- 0.3 s の話者照合の EER（一次情報に無い）。
