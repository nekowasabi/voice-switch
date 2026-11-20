# スティッキーモードの可否（2026-11-20）

プロトタイプは macOS のウェイクワード辞書だけ。フラグ `macOS.stickyTarget` は既定オフ。オフのとき既存の handoff / paste / route は変えない。Windows の貼り戻しは今回は作らない（not possible）。共有 JSON の `macOS` は Windows が無視する。

届けたあとのフォーカス: 配信を始める瞬間に前面だったアプリへ戻す。切り替えていなければ、そのアプリが宛先なのでフォーカスは宛先のまま。復帰に失敗したら宛先に残してログする。tmux は前面を動かさない（ユーザーは今いるアプリのまま。切り替えていればそれが「戻る先」と一致する）。

## 判定

| 問い | 判定 | 根拠 |
|---|---|---|
| 開始時の前面 pid と bundle id | works | 既存の `NSWorkspace.shared.frontmostApplication`。`Dictation.target` が同じ値を既に持っている。 |
| AX の focused element と role / subrole | works | アクセシビリティ許可（`AXIsProcessTrusted`）があるとき。role が `AXTextField` / `AXTextArea` なら field。ブラウザは subrole を欠くことが多いので、subrole の読み失敗だけでは field を捨てない。 |
| ウィンドウ（`kAXWindowAttribute`） | works | 取得はできる。 |
| アプリの再アクティブ | limited | `NSRunningApplication.activate` は既存経路と同じ。前面にならないことがある。 |
| ウィンドウを上げる（`kAXRaiseAction`） | limited | ネイティブは通りやすい。失敗しても、その後の同一要素チェックで止める。 |
| 要素へフォーカス（`kAXFocusedAttribute`） | limited | ネイティブは通りやすい。Chrome / Electron は失敗が多い。戻った要素がキャプチャ時と `CFEqual` で一致しなければ貼らない。 |
| 要素消滅・アプリ終了・ウィンドウ閉鎖 | works | `kAXErrorInvalidUIElement`、`isTerminated`。クリップボードに残し、今の前面には貼らない。 |
| AXValue / `kAXSelectedTextAttribute` で挿入 | not possible | Web、Electron、端末では信頼できない。プロトタイプでは使わない。 |
| ペーストボードと Cmd+V（退避して成功時だけ戻す） | works | ネイティブのテキスト欄。成功したときだけ退避を戻す。 |
| ブラウザ（Chrome / Safari） | limited | フォーカスが同じ要素に戻ったときだけ Cmd+V。戻らなければクリップボードに残す。 |
| Electron（Grok Bot、Slack） | limited | ブラウザと同じ。Enter は送信になることが多い。 |
| iTerm2 + tmux（Claude Code） | works | 開始時の tty を pane id に合わせ、`send-keys -l` の括弧付きペーストのあと Enter。AX よりこちらを優先する。前面にはしない。 |
| tmux なしの iTerm | limited | 端末として activate し、Cmd+V と Enter。括弧付きペーストは端末側の挙動に依存する。 |
| Enter（CGEvent、virtual key `0x24`） | limited | 貼り付けが成功し、宛先がまだ生きているときだけ。IME の変換途中は検出しない。複数行欄では改行になる。Slack や Claude Code では送信になる。 |
| セキュア欄の拒否 | works | 開始時と配信時の両方。subrole `AXSecureTextField` または `IsSecureEventInputEnabled`。 |
| 本文をログに出さない | works | `chars=` の文字数だけ。 |
| 3 分で捨てる | works | 1 tick = 60 秒、`maxAge = 3`。武装中と録音中だけ進む。 |
| オフなら今までどおり | works | セッションは nil。設定キーが無い、または false、かつメニューの上書きが無い。 |
| Windows でも同じ貼り戻し | not possible | 今回の範囲外。設定キーは `macOS` に置き、`DictationConfig` には足さない。 |

## 挿入と Enter

主経路はペーストボード + Cmd+V、または tmux の `send-keys`。AX で値を書かない。

tmux は `ESC [200~`、本文、`ESC [201~` を `-l` で送り、最後に `-l` なしの Enter。括弧が無いと本文の改行が Claude Code の送信になる。`send-keys` が失敗したらクリップボードに残し、前面へ Cmd+V しない。

通常欄は、activate の前に前面アプリを控える。raise、フォーカス、同一要素の確認のあとで Cmd+V。約 80 ms あと、宛先が生きていれば Return。貼ったあと宛先が消えたら Enter は押さない（別アプリに当たる）。

失敗（要素が違う、無効、終了、セキュア、3 分超過、貼り付け失敗）はクリップボードに本文を残す。退避した古いクリップボードは戻さない。理由と文字数をログし、HUD は失敗。今の前面には貼らない。

## 状態遷移

機械は `Idle → Armed → Recording → Delivering → Delivered / FellBack`。図の名前は日本語。モデルは `.claude/skills/verify-sticky-mode/StickyMode.tla`。TLC は 300 distinct states、1138 states generated、depth 9。エラーなし。

図が隠しているもの: 終了後の `Reset`（次の入力を待つ）、3 分に達する前の tick、終了後の ToggleOn、終状態の Done スタッタ。statechart は 598 step を view で隠し、同じラベルの 114 step を矢印に重ねた。終状態からも次の矢印が出る。

```mermaid
stateDiagram-v2
  state "Legend" as legend {
    lg0: off
    lg1: hold
    lg2: send
    lg3: done
    lg4: back
  }
  class lg0 tan
  class lg1 blue
  class lg2 orange
  class lg3 teal
  class lg4 yellow
  s0: オフ
  s1: 待機
  s2: 覚えた
  s3: 入力欄がない
  s4: パスワード欄
  s5: 覚えたが宛先が消えた
  s6: 貼り付け中
  s7: クリップボードに残す
  s8: 録音中
  s9: 貼る前に宛先が消えた
  s10: 貼り付け済み
  s11: 宛先が消えた
  s12: 録音中 / 別のアプリ
  s13: 届けた
  s14: 貼ったあと宛先が消えた
  s15: 届けて元のアプリへ戻る
  [*] --> s0
  [*] --> s1
  s0 --> s1: スティッキーを入れた
  s1 --> s0: スティッキーを切った
  s1 --> s2: 音声入力を開始 [テキスト欄] or 音声入力を開始 [tmux のペイン]
  s1 --> s3: 音声入力を開始 [入力欄がない]
  s1 --> s4: 音声入力を開始 [パスワード欄]
  s2 --> s1: 空のまま終わった
  s2 --> s5: 宛先が消えた
  s2 --> s6: テキストができた
  s2 --> s7: スティッキーを切った or 3分を過ぎた
  s2 --> s8: 話し始めた
  s3 --> s1: 空のまま終わった
  s3 --> s7: スティッキーを切った or テキストができた [貼れない] or 3分を過ぎた
  s4 --> s1: 空のまま終わった
  s4 --> s7: スティッキーを切った or テキストができた [貼れない] or 3分を過ぎた
  s5 --> s1: 空のまま終わった
  s5 --> s7: スティッキーを切った or テキストができた [貼れない] or 3分を過ぎた
  s5 --> s11: 話し始めた [宛先は既に無い]
  s2 --> s12: 話し始めた [別のアプリ]
  s6 --> s7: スティッキーを切った or 貼り付けに失敗した
  s6 --> s9: 宛先が消えた
  s6 --> s10: 覚えた場所へ貼った
  s8 --> s1: 空のまま終わった
  s8 --> s6: テキストができた
  s8 --> s7: スティッキーを切った or 3分を過ぎた
  s8 --> s11: 宛先が消えた
  s8 --> s12: 別のアプリに移った
  s9 --> s7: スティッキーを切った or 貼り付けに失敗した
  s10 --> s13: Enter を押した
  s10 --> s14: 宛先が消えた
  s10 --> s7: スティッキーを切った or Enter に失敗した
  s11 --> s1: 空のまま終わった
  s11 --> s7: スティッキーを切った or テキストができた [貼れない] or 3分を過ぎた
  s12 --> s1: 空のまま終わった
  s12 --> s6: テキストができた
  s12 --> s7: スティッキーを切った or 3分を過ぎた
  s12 --> s11: 宛先が消えた
  s14 --> s7: スティッキーを切った or Enter に失敗した
  s10 --> s15: Enter して元のアプリへ戻る
  class s0 tan
  class s1 blue
  class s2 blue
  class s3 blue
  class s4 blue
  class s5 blue
  class s6 orange
  class s7 yellow
  class s8 blue
  class s9 orange
  class s10 orange
  class s11 blue
  class s12 blue
  class s13 teal
  class s14 orange
  class s15 teal
```

色: tan = オフ、blue = 保持、orange = 送信、teal = 完了、yellow = クリップボードへ戻す。
