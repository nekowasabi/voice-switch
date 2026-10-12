# 音声入力製品の主張の一次情報照合

対象: `products.md`（元ノートは変更していない）。取得は公式ページ・公式ドキュメント・GitHub の README と LICENSE を優先した。検索スニペットや第三者ブログからの確定はしていない。

取得に使った経路: `ctx_fetch_and_index` で一次ソースを取り込み、`ctx_search` で該当箇所を確認した。検索はレート制限に当たったため、一部は取り込んだ索引を `ctx_batch_execute` の global 検索や保存済みの出力に対する `rg` で照合した。

## 集計

VERIFIED 31 件 / CORRECTED 9 件 / NOT FOUND IN SOURCE 17 件 / UNREACHABLE 8 件（計 65 件）

## 主張ごとの照合表

| 主張 | URL（取得したもの） | 判定 | 一次ソースの引用 | 訂正 |
|---|---|---|---|---|
| Superwhisper: トグル録音の開始・停止 | https://superwhisper.com/docs/get-started/essential-settings | VERIFIED | "Toggle Recording: Press to start/stop recording." | なし |
| Superwhisper: 各モードが専用ショートカットを持つ | 同上 | VERIFIED | "each mode has its own Keyboard shortcut that starts a recording in that specific mode" | なし |
| Superwhisper: モード切替は長押しで開く | 同上 | VERIFIED | "Change mode: Press-and-hold to open the mode changer" | なし |
| Superwhisper: 音は「録音開始」と「処理完了」の2種 | https://superwhisper.com/docs/get-started/settings-sound（取得内容は Essential settings ページ） | CORRECTED | "Sound Effects: adjust the audio cues for recording start and stop." | 音は録音の開始と停止の2種。処理完了の音は記載なし。 |
| Superwhisper: 音量を独立に設定、または無効化できる | 同上 | VERIFIED | "Adjust their volume independently or turn them off." | 「独立」の基準（システム音量など）は記載なし。 |
| Superwhisper: 録音ウィンドウを消しても音は鳴る | 同上 | NOT FOUND IN SOURCE | 取得本文に該当記述なし | 未確認 |
| Superwhisper: 無音除去は起動制御ではなく幻覚防止 | 同上 | VERIFIED | "Silence Removal: recommended; helps prevent hallucinated text during periods of silence." | 「起動制御ではない」は記載なし（ノートの解釈）。 |
| Superwhisper: 音・録音のファイル文字起こしを URL scheme で開ける | https://superwhisper.com/docs/get-started/transcribe-files.md | NOT FOUND IN SOURCE | 文字起こしは CLI の "superwhisper transcribe /path/to/your/audiofile.mp3"、Finder の「Open with」、Windows のトレイ「Transcribe File」で示される。URL scheme の記述はなし。 | URL scheme 経由ではない。CLI と OS メニューが正規の経路。Pro 機能: "File transcription is a Pro feature." |
| Superwhisper: 無音時の幻覚防止 | 同上 | VERIFIED | （上行と同じ） | なし |
| Wispr Flow: Mac の hold-to-talk は Fn | https://docs.wisprflow.ai/articles/2612050838-supported-unsupported-keyboard-hotkey-shortcuts | VERIFIED | "Push to talk (hold to dictate) | Fn | Ctrl+Win" の行（Mac 列） | なし |
| Wispr Flow: Windows の push-to-talk 既定は Ctrl+Win+Alt | 同上 | CORRECTED | 同じ表の Windows 列: "Ctrl+Win" | 既定は Ctrl+Win。Ctrl+Win+Alt はノートの二次情報源の記載。 |
| Wispr Flow: hands-free（トグル）がある | 同上 | VERIFIED | "Hands-free (press to start/stop)" | なし |
| Wispr Flow: Mac の hands-free は Fn+Space（cheat sheet） | 同上 | NOT FOUND IN SOURCE | 取得した抜粋では Mac の hands-free 既定キーが切れていた | 未確認。cheat sheet（第三者 PDF）は未取得。 |
| Wispr Flow: Mac は Fn の二度押しで hands-free | 同上 | CORRECTED | "Double-tapping Push to talk still starts hands-free dictation without a dedicated Hands-free binding." | 二度押しは push-to-talk キー全般の機能。Fn 固有とは書かれていない。 |
| Wispr Flow: 「クラウド型が既定」 | https://wisprflow.ai/post/revolutionizing-accessibility-tools | VERIFIED（ベンダー主張） | "Wispr Flow leverages online speech recognition for instant, cloud-based dictation and system control" | Pro では offline support があると同ページが書く: "The Pro plan offers more features, including enhanced integration, translation capabilities, and offline support". |
| Wispr Flow: 自動編集がある | 同上 | VERIFIED | "Its AI handles the transcription and auto-editing process" | なし |
| Wispr Flow: hands-free は連続口述 | 同上 | VERIFIED | "a 'hands-free' mode, enabling continuous dictation without requiring manual input" | なし |
| Wispr Flow: hold-to-talk が標準の流れ（accessibility-voice-tools） | https://wisprflow.ai/post/accessibility-voice-tools | UNREACHABLE | 本パスでは取得していない（取得失敗ではなく未実施） | 未確認。上の公式 shortcuts 記事で代替済み。 |
| Talon: 既定で常時待機、"go to sleep" / "wake up" で切替 | https://talonvoice.com/docs/ と https://archive.ejfox.com/wiki/Talon_Voice_Control | CORRECTED | 公式ページは索引のみ。wiki の図: "Command --> Sleep: \"talon sleep\"" / "Sleep --> Command: \"talon wake\"" | 合言葉は "talon sleep" / "talon wake"。これは個人の設定例（ejfox の dotfiles）で、Talon の既定ではない。 |
| Talon: pop / hiss をコマンド入力に使える | https://talonvoice.com/docs/ | NOT FOUND IN SOURCE | 取得した索引ページに該当記述なし（詳細ページは未取得） | 未確認 |
| Willow: ホットキーを押し続けて口述 | https://willowvoice.com/features/dictation | VERIFIED | "Press and hold your Willow hotkey, speak naturally, then release." | なし |
| Willow: ホットキーで hands-free を直接トグル（2025年7月） | https://willowvoice.featurebase.app/changelog | VERIFIED | "We've also removed the requirement to double-tap to enter hands-free mode. You can toggle it directly with your assigned hotkey" （v1.2.13〜v1.2.15、Jul 15, 2025 の項） | なし |
| Willow: Mac 専用（YC の掲載情報） | https://www.ycombinator.com/companies/willow | CORRECTED | "→ [Try it for free on Mac.]" | YC は Mac での試用を案内しているだけで「Mac 専用」とは書かれていない。公式ページは "Works seamlessly across your Mac, PC, and mobile devices." |
| Willow: 精度（最大3倍）など | https://willowvoice.com/features/dictation | VERIFIED（ベンダー主張） | "Willow is also up to 3x more accurate than other dictation tools" | 独立検証ではない。 |
| Willow: 編集機能・iOS キーボード（Yahoo Tech） | https://tech.yahoo.com/ai/meta-ai/articles/willow-voice-keyboard-lets-type-072100796.html | UNREACHABLE | 本パスでは未取得（二次報道のため優先度を下げた） | iPhone 対応は公式ページの見出しで確認: "AI Speech to Text Mac, Windows, and iPhone Dictation Software" |
| Windows Voice Typing: Win+H で開始 | https://support.microsoft.com/en-us/windows/use-voice-typing-to-talk-instead-of-type-on-your-pc-fec94565-c4bd-329d-e59a-af033fa5689f | VERIFIED | "Press **Windows** key+**H** on a hardware keyboard." | なし |
| Windows Voice Typing: 「Stop listening」で停止 | 同上 | VERIFIED | "Say a voice typing command like \"Stop listening\"." | なし |
| Windows Voice Typing: インターネット接続が必要 | 同上 | VERIFIED | "To use voice typing, you'll need to be connected to the internet" | なし |
| Windows Voice Typing: 無音で 5〜10 秒の自動停止 | 同上（yaps.ai 記事は競合の第三者ブログ） | NOT FOUND IN SOURCE | MS の公式ページに無音タイムアウトの記述なし | 無音タイムアウトは競合ブログの主張のみ。公式では未確認。 |
| Windows Voice Typing: Microsoft は「意図的で設定不可」と説明 | 同上 | NOT FOUND IN SOURCE | 公式ページに該当記述なし | 競合ブログの引用のみ。 |
| Windows Voice Typing: フォーカス変更や手入力で停止 | 同上 | NOT FOUND IN SOURCE | 公式ページに該当記述なし | 未確認。 |
| Windows Voice Typing: Anki で停止・Chrome や Notepad では停止しない | https://forums.ankiweb.net/t/windows-voice-typing-functionality-is-limited-on-this-app/23255 | UNREACHABLE | 取得は成功したが本文照合は未完了。ユーザー投稿でありプライマリではない | 参考情報としても一次情報ではない。 |
| Windows Voice Access: 起動・停止・無音タイムアウト | MS の推測 URL（404） | UNREACHABLE | HTTP 404。公式の正しい URL は探索していない | 未確認 |
| Apple: "Go to sleep" で音声を止め、"Wake up" で再開 | https://support.apple.com/guide/mac-help/control-your-mac-and-apps-using-voice-control-mh40719/11.0/mac/11.0 | CORRECTED | "If you take a break and don't want words you say to be interpreted as commands, say \"Go to sleep\" or click Sleep in the feedback window." | 止まるのは「コマンドとして解釈すること」。聞き取りを止めるとは書かれていない。 |
| Apple: メニューバー項目で聞き取りを開始・停止（Sonoma 14 以降） | 同上 | NOT FOUND IN SOURCE | 取得本文に該当記述なし | 未確認 |
| Apple: "Command mode" / "Dictation mode" の切替 | 同上 | VERIFIED | "say “Command mode.”" / "say “Dictation mode.”" | なし |
| Apple: 既定のキーボードショートカットはなし | 同上 | NOT FOUND IN SOURCE | 該当記述なし | 未確認 |
| Apple: 「Apple にデータを送らない」（Computerworld 2019） | https://www.computerworld.com/article/3442721/how-to-use-voice-control-in-macos-catalina.html | UNREACHABLE | 本パスでは未取得。古い記事のため一次情報でもない | 未確認 |
| Dragon: 寝ている間の起動語は "Wake up" | https://nuance.com/products/help/dragon/dragon-for-mac/enx/Content/Introduction/Microphone/MicrophoneModes.html | VERIFIED | "Say Wake Up or Turn microphone on." | Mac 版の記述。PC 版の "Microphone off" は未確認。 |
| Dragon: 起動の手段（Windows の既定は numeric keypad plus） | 同上（Mac 版） | NOT FOUND IN SOURCE | Mac 版は "Use the keyboard shortcut to toggle the microphone (default is **⌘F11**)." | Windows の既定キーは未確認。Mac の既定は ⌘F11。 |
| Dragon: 寝ている間は "Wake Up" 以外を無視 | 同上、およびhttps://www.nuance.com/products/help/dragon/dragon-for-pc/scriptref/Content/scrptref/gotosleep.htm | VERIFIED | "Dragon ignores all speech except Wake up." ／ "The sleep mode Vocabulary typically includes only the Wake Up command." | 「typically」（通常は）。 |
| Dragon: Mac は既定で 1 分無操作でスリープ | 同上 | VERIFIED | "By default, Dragon's microphone will go to sleep after one minute of inactivity." | なし |
| Dragon: 寝ている間も "Wake Up" を監視している（IDRC チュートリアル） | https://legacy.idrc.ocadu.ca/resources/idrc-online/51-tutorials/tutorials/130-dragon-naturally-speaking-basic-features.html | UNREACHABLE | 本パスでは未取得。公式の sleep 記述は「無視する」であり「監視する」とは書かれていない | 未確認 |
| Handy: 既定は push-to-talk（押している間だけ録音） | https://github.com/cjpais/Handy | NOT FOUND IN SOURCE | "Press a configurable keyboard shortcut: hold it to record and release to stop, or tap it to toggle recording on and off (Hold-only and Toggle-only modes are also available)" | 既定がどちらかは README に書かれていない。 |
| Handy: トグル・ホールドのみ・トグルのみのモードがある | 同上 | VERIFIED | "(Hold-only and Toggle-only modes are also available)" | なし |
| Handy: Silero VAD で無音を除去 | 同上 | VERIFIED | "Silence is filtered using VAD (Voice Activity Detection) with Silero" | なし |
| Handy: 音声フィードバックは既定でオフ | 同上 | NOT FOUND IN SOURCE | "Enable \"Audio Feedback\" (also in Advanced) if you still want audible confirmation of recording state" | 「既定でオフ」の記述なし。有効化は Advanced 設定で行う。 |
| Handy: Overlay Position を None にすると表示を消せる | 同上 | VERIFIED | "Open **Settings > Advanced** and set **\"Overlay Position\"** to **\"None\"** to disable the overlay" | 記載は Linux（X11）の項の中。 |
| Handy: Linux の overlay は既定でオフ | 同上 | NOT FOUND IN SOURCE | 該当記述なし | 未確認 |
| Handy: 完全にローカル・オフライン | 同上 | VERIFIED | "The process is entirely local" | なし |
| Handy: 履歴を保存（History view） | 同上 | NOT FOUND IN SOURCE | Raycast 連携のみ: "browse transcript history" | 履歴の閲覧は Raycast 拡張の機能として記載。Handy 本体の History 画面は確認できず。 |
| Handy: "Your words appear in any text field without sending your voice to the cloud" と引用 | 同上 | CORRECTED | "Private: Your voice stays on your computer. Get transcriptions without sending audio to the cloud" | 引用文は原文と異なる。意味は同じ。 |
| Handy: 常時待機はない | 同上 | NOT FOUND IN SOURCE | 常時待機の記述は見つからず（不在の証明ではない） | ノートの「常時待機なし」は README の記述に基づかない。 |
| VoiceInk: 録音と push-to-talk のショートカット | https://github.com/Beingpax/VoiceInk | VERIFIED | "Configurable keyboard or mouse shortcuts for quick recording and push-to-talk functionality" | なし |
| VoiceInk: 100% オフライン | 同上 | CORRECTED | README: "100% offline processing ensures your data never leaves your device" / 公式 docs: "VoiceInk turns speech into text using a local model or a cloud provider you connect." (https://tryvoiceink.com/llms.txt) | 「100% オフライン」は README の宣伝文。公式 docs ではクラウドプロバイダを接続できると書かれている。 |
| VoiceInk: GPL v3 | https://github.com/Beingpax/VoiceInk/blob/main/LICENSE | VERIFIED | "GNU GENERAL PUBLIC LICENSE Version 3, 29 June 2007" | 「第三者による」という但し書きは不要。 |
| VoiceInk: 常時待機はない | https://github.com/Beingpax/VoiceInk | NOT FOUND IN SOURCE | 常時待機の記述は見つからず | 不在の証明ではない。 |
| VoiceInk: 価格 | 競合ブログ | UNREACHABLE | 公式の価格ページは未取得 | 未確認 |
| MacWhisper: 全般 | https://goodsnooze.gumroad.com/l/macwhisper | UNREACHABLE | 取得本文が空（タイトルのみ） | 未確認。公式ページを探していない。 |
| Aqua Voice: 全般（ノートでは Gap） | https://aquavoice.com/ | VERIFIED | "HoldSpace and speak to draft an email"（ホームページの文）。取得時は "Hold" と "Space" が連結された表記。 | 無音時の挙動・終了方法は未確認。 |
| nerd-dictation: 起動・停止（ノートでは Gap） | https://github.com/ideasman42/nerd-dictation | VERIFIED | "As this relies on manual activation there are no background processes." ／ 起動 `./nerd-dictation begin`、停止 `./nerd-dictation end` | なし |
| whisper-writer: 起動モード（ノートでは Gap） | https://github.com/savbell/whisper-writer | VERIFIED | "`continuous` (default): Recording will stop after a long enough pause in your speech. The app will transcribe the text and then start recording again." | 既定は一時停止で区切って自動再開するモード。 |
| OpenWhispr: 起動方法（ノートでは Gap） | https://github.com/OpenWhispr/openwhispr | VERIFIED（ホットキーの存在のみ） | "Press a hotkey, speak, and your words appear at your cursor." | 起動モード（押下・トグル等）は未確認。 |
| 製品横断: 無音で自動開始する方式は見つからない | 複数（上の whisper-writer） | CORRECTED | whisper-writer の既定 `continuous` は、一時停止後に録音を自動再開する。 | 一時停止検出による自動再開は既に存在する。ノートの「energy-based auto-start はない」は不正確。 |
| 製品横断: 録音を先に残し、後で採否を決める（retroactive）または下書き確認（draft-then-confirm） | Superwhisper history.md、VoiceInk shortcuts | NOT FOUND IN SOURCE | Superwhisper: "saves every dictation as you speak: the audio, the transcription, and the AI result. Use History to review past dictations, retry failed ones" | 録音後の閲覧・再処理はあるが、採否を後から決めるための仕組みとは書かれていない。 |

## 修正済みの製品比較表（一次情報で確認できた事実のみ）

空欄は未確認。

| 製品 | 開始 | 終了 | 誤起動の防止 | 音・表示 | 履歴・確認 | 出典 |
|---|---|---|---|---|---|---|
| Superwhisper | トグル（既定 ⌥ Space）、長押し push-to-talk、モードごとのショートカット | トグルの再押下、または離す（push-to-talk） | 無音除去は幻覚防止。起動制御ではない | 録音開始と停止の音。音量を調整、または無効化できる | History に音声・文字起こし・AI 結果を保存。保存期間を設定できる | essential-settings, history.md |
| Wispr Flow | push-to-talk（Mac: Fn、Windows: Ctrl+Win）、hands-free トグル | 離す（push-to-talk）、hands-free のトグル | 空欄 | 空欄 | 空欄 | shortcuts 記事, a11y ページ |
| Willow | ホットキーを押し続ける。hands-free はホットキーでトグル（2025年7月〜） | 離す、または hands-free のトグル | 空欄 | 空欄 | 空欄 | dictation ページ, changelog |
| Windows Voice Typing（Win+H） | Win+H。"Listening..." の表示を待つ | "Stop listening" または マイクボタン | 空欄（無音タイムアウトは未確認） | 空欄 | 空欄 | MS サポートページ |
| Apple Voice Control | "Go to sleep" でコマンド解釈を止め、"Wake up" で再開 | 同左 | 寝ている状態（スリープ）で、コマンドとして解釈しない | 空欄 | 空欄 | Apple サポートページ |
| Dragon（Mac） | "Wake Up"（寝ている状態から） | "Microphone off" / "Go to sleep" | 寝ている間は "Wake Up" 以外を無視。Mac は既定で 1 分無操作でスリープ | 空欄 | 空欄 | Nuance の Mac マイク・モード、GoToSleep |
| Handy | 押す（ホールド / トグル / 両方）。既定は未確認 | 離す（ホールド）、またはもう一度押す（トグル） | Silero VAD で無音を除去 | Overlay Position を None で非表示（Linux の文脈）。音声フィードバックは Advanced で有効化 | Raycast 連携で履歴を閲覧 | GitHub README |
| VoiceInk | 録音ショートカット。モード: Toggle / Push to Talk / Hybrid / Double Tap | 同左 | 空欄 | 空欄 | 空欄 | docs/shortcuts, README, LICENSE |
| Aqua Voice | "Hold Space and speak" | 空欄 | 空欄 | 空欄 | 空欄 | aquavoice.com |
| nerd-dictation | `begin` で開始 | `end` で停止 | 手動起動のみ（常駐プロセスなし） | 空欄 | 空欄 | GitHub README |
| WhisperWriter | `activation_key`（既定 ctrl+shift+space）。モード: continuous（既定）/ voice_activity_detection / press_to_toggle / hold_to_record | continuous は一時停止で区切り、ショートカットの再押下で止める | 空欄 | 空欄 | 空欄 | GitHub README |
| OpenWhispr | ホットキー | 空欄 | 空欄 | 空欄 | 空欄 | GitHub README |
| Talon | 空欄（公式の既定は未確認） | 空欄 | 空欄 | 空欄 | 空欄 | talonvoice.com/docs（索引のみ） |

## 重要な追加事項

- **WhisperWriter の既定は一時停止で自動再開する `continuous` モード。** ノートの「energy-based 自動開始を使う製品は見つからなかった」は誤り。ユーザーの「3 秒アイドルで自動開始」の検討にとって最も近い既存例。ただし既定が一時停止で区切って再開するため、意図しない録音が続く危険もある。停止はショートカットの再押下が必要。
- **Superwhisper の録音キャンセルは短い録音なら即時、長い録音は確認が出る。** 録音後の採否を決める挙動に近い。
- **Superwhisper の録音は常に保存される（Keep recordings for で期間設定）。** 録音を先に残し、後で再処理・再試行する設計。ただし「後から採否を決める」という機能ではない。
- **Superwhisper の文字起こしはファイル入力（Pro）。** CLI `superwhisper transcribe <file>` があり、Finder や Windows のメニューからも開ける。URL scheme での文字起こしは確認できず、確認できた deep link は `superwhisper://settings` のみ。
- **Superwhisper の Sound 設定は全体設定で、モード別の記述はない。** 「録音ウィンドウを消しても音は鳴る」は未確認。
- **Wispr Flow の hands-free は push-to-talk の二度押しでも開始できる。** 専用ショートカットを割り当てなくてもよい。
- **Wispr Flow の Windows の既定は Ctrl+Win。** ノートの Ctrl+Win+Alt は誤り。
- **Willow は「Hey Willow」の音声起動をホットキーに割り当て可能（v1.2.13）。** 音声起動からホットキーに移行した経緯がある。
- **Windows Voice Typing は「Stop dictation」「Stop listening」「Pause voice typing」などの音声コマンドで止められる。** 公式ページの一覧にある。
- **Apple Voice Control の sleep は「止める」ではなく「コマンドとして解釈しない」。** 寝ている間も音声は処理されている可能性がある（公式記述からは断定できない）。
- **Dragon の寝ている状態は "Wake Up" 以外を無視する。** 寝ている間の語彙を 1 語に絞る設計の一次根拠。
- **Handy の hold と toggle の両方のモードがある。** 既定が push-to-talk かは README に書かれていない。
- **VoiceInk の「100% オフライン」はマーケティング文。** 公式 docs はクラウドプロバイダの接続も可能と書く。
- **retroactive（録音を先に残し後で採否）や draft-then-confirm を採る製品は、今回の一次情報の範囲では見つからなかった。** Superwhisper の履歴は後から閲覧・再処理できるが、採否を決める仕組みではない。VoiceInk の Auto Send は "Finishing normally with a recording shortcut does not send the pasted message." とあり、送信前に確認する設計に近い。
- **Talon の既定動作（常時待機・寝る合言葉・pop / hiss）は、今回の公式ページの索引では確認できなかった。** 公式の詳細ページを取り直す必要がある。
- **Windows Voice Typing の無音タイムアウトは、公式ページに記述がない。** ノートの 5〜10 秒は競合ブログのみ。

## 未了の項目

- Windows Voice Access（公式 URL の推測が 404）。
- MacWhisper（取得本文が空）。
- Talon の公式詳細ページ（pop / hiss、寝る合言葉）。
- Apple のメニューバー項目。
- Windows 版 Dragon の既定キー。
- Wispr Flow の Mac の hands-free 既定キー。
- 無音タイムアウトの有無（Windows Voice Typing を除く各製品）。
- 第三者ブログ、フォーラム、Yahoo Tech、IDRC チュートリアル、Computerworld 記事は一次情報ではないため、いずれも確定に使っていない。
