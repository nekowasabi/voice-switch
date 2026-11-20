# スティッキーモード（2026-11-20）

対象は `feat/sticky-mode`（`origin/main` の `b75c91b` から作成）。マージも push もしない。

## Use pstack why

待つ苦痛の原因は、文字起こしが遅いことだけではない。宛先を「入力欄」として覚えていない。

### The Question

音声入力のあと、ユーザーが入力欄に戻って貼り付けと Enter を待つのをやめられるか。やめられないなら、今のコードのどこがそれを強制しているか。

### The Code in Question

- `Listener.Dictation.target`（`Sources/voice-switch/MacApp.swift`）。初期値は `NSWorkspace.shared.frontmostApplication`。コメントは「superwhisper pastes into whatever is frontmost」。blame は `e6ebd54`（2026-10-02）。
- `handoff` の末尾。ウェイクワードの録音は `decideDictationDelivery(modeRequested: false, ...)` が `.superwhisper` のとき、貼り付けを Superwhisper の自動ペーストに任せる。`223e91a` の本文は「ウェイクワード録音は Superwhisper の自動貼り付けに任せ、voice-switch の貼り付け経路を削除する」。
- `restoreFocusIfSuperwhisper`。Superwhisper が前面のあいだだけ、ウェイク時のアプリを `activate` する。目的は Superwhisper が前面のままで自動ペーストを飛ばさないようにすること（`handoff` 内コメント、`0703eb4` 付近の paste 経路とは別）。
- `pasteDictation`（`0703eb4` のコメント、ガードは `6df7ea6`）。自動ペーストが無い mode で、ペインが取らなかったときの履歴フォールバック。前面の pid が一致し、1 秒以上入力が無く、AX の role が `AXTextField` / `AXTextArea` で subrole が `AXSecureTextField` でなく、`IsSecureEventInputEnabled` でなく、入力ソースがキーボードレイアウト（IME ではない）のときだけ、クリップボードと Cmd+V。Enter は送らない。
- `sendKeysToPane`（`PaneRoute.swift`）。本文のラベルが一つの tmux ペインに当たったとき `send-keys -l`。開始時のペインは覚えない。Enter は送らない。
- `Macrowhisper.prepare`。`autoReturn` はコマンドモード（辞書入力が開かない経路）の Superwhisper 録音に Return を足す。辞書入力の handoff からは呼ばれない。`scheduleAction` があると `autoReturn` は捨てる。

### What We Found

- ウェイクを聞いた瞬間に残るのは前面アプリの `NSRunningApplication` だけである（`e6ebd54` のコメントがそう書いている）。フォーカス中の AX 要素も、tmux の pane id も、その時点では取らない。
- ウェイクワード辞書の貼り付け本体は Superwhisper である（`223e91a`）。Superwhisper は処理が終わったときの前面へ貼る。音声のあとユーザーが別のアプリへ移ると、文章はそっちへ落ちる。移らないためには、文字起こしが終わるまで元のアプリに留まる必要がある。
- `d.target` を手off に渡すのは、Superwhisper が前面のあいだフォーカスを戻すためで、貼る場所を固定するためではない。自動録音をユーザーが停止語か finish で終えたときは、開始時ではなく「今の前面」を渡す（`submit` のコメント、`223e91a` 系）。
- voice-switch 自身の Cmd+V（`pasteDictation`）は履歴フォールバックに残っている。1 秒のアイドルと、入力ソースがキーボードレイアウトであることの両方が要る。日本語 IME は `kTISTypeKeyboardLayout` ではないので、`compositionClear` は偽になる（`macPasteSafety`）。`docs/no-wake-word/verification.md` は、Mac の日本語入力ソースでは履歴からの明示的なコピーに落とす、と書いている。
- Enter を押す処理は辞書入力の経路に無い。`macrowhisper.autoReturn` は別経路で、覚えた入力欄には結び付かない。

### What We Can Reasonably Infer

開始時のアプリを覚えているのに貼り付けが「終わったときの前面」なのは、Superwhisper に貼らせる設計を残したまま、前面の奪い合いだけを足したため、と読める。`223e91a` が voice-switch の貼り付けをウェイク経路から外したことと、`e6ebd54` のコメントが同時に残っているのがその形である。

待つのが面倒な直接の理由は、文字起こしの秒数のあいだ宛先のアプリに留まり、終わったら自分で Enter を押す必要があることである。アプリを切り替えると、覚えていない入力欄ではなく、そのときの前面に貼られる。

### Competing Hypotheses

- 履歴フォールバックの 1 秒アイドルが主因、という読み。アイドルが無いと Cmd+V は走らないが、普段のウェイク経路はそこを通らず Superwhisper が貼る。主因ではない。
- tmux 振り分けが既に「正しい場所へ送る」ので足りている、という読み。振り分けは本文のラベルとカタログの一致で、開始時にフォーカスしていたペインではない。Claude Code のペインへ「今のカーソル」として送る用途とは違う。

### What We Don't Know

- Superwhisper が貼る瞬間の前面を、実機ログで何件取り違えたか。この作業ツリーのログにはその集計が無い。
- `macrowhisper.autoReturn` を辞書入力と組み合わせて使っているか。コード上、辞書の handoff は `prepare` を呼ばない。
- Chrome の AX テキスト欄が、subrole 無しで role だけを返すか。実装時の判定は FEASIBILITY.md に書いた。実機では未確認。

### Sources Consulted

- Source control。`git log` / `git blame` を `MacApp.swift` の `pasteDictation` と `Dictation.target` に対して見た。上記のハッシュ。Issue 番号付きの PR 本文は、この環境に該当 PR の取得を掛けていない。コミット本文を引用した。
- Issue / ticket tracker。この環境に Linear / Jira / GitHub Issues 用の MCP が無いので検索していない。
- Long-form documents。リポジトリ内の `README_ja.md`、`docs/no-wake-word/verification.md`、`docs/windows-feature-parity.md`、`plan/2026-10-30/PLAN.md`、`HANDSOFF.md` を読んだ。Notion 等の MCP は無い。
- Real-time team chat。Slack 等の MCP は無い。
- Infrastructure observability。該当 MCP は無い。貼り違いのメトリクスは見つからなかった。
- Error / exception tracking。該当 MCP は無い。
- Product analytics warehouse。該当 MCP は無い。

### Confidence Summary

Direct。宛先が前面アプリだけで、Superwhisper が終わったときの前面へ貼る、というコード上の事実。Enter が辞書経路に無いこと。

Supported。待つ苦痛は「処理が終わるまで宛先に留まる」ことと「自分で確定キーを押す」ことの両方、という読み。コミット本文とコメントが同じ方向を向いている。

Unknown。実機で何割がアプリ切り替えによって別の欄に貼られたか。

### Preserve / Change / Avoid / Risk

- Preserve。フラグがオフのとき、`handoff` と `pasteDictation` と `routeDictation` の今の呼び出しを変えない。アクション（URL / POST / command）と自動録音はスティッキーに載せない。セキュア欄には貼らない。本文はログに出さない（長さだけ）。
- Change。フラグがオンのウェイクワード辞書だけ、開始時に入力欄（または tmux pane id）を覚え、テキストができたあとそこへ貼って Enter する。Superwhisper の自動ペーストは、そのセッションでは走らせない。
- Avoid。AX の value 設定で挿入しない。失敗したときに「今の前面」へ貼らない。`pasteDictation` の 1 秒アイドルをスティッキーに再利用しない（再利用すると、別アプリで作業中は毎回失敗する）。
- Risk。Cmd+V は宛先を前面にする必要がある。配信のあと、配信開始時に前面だったアプリへ戻す。tmux は前面にせず `send-keys` する。モード切替に失敗した Superwhisper を開くと自動ペーストが残るので、開かない。

## Use pstack how

### Overview

辞書入力は、ウェイクで `Dictation` を作り、無音か停止語か上限で `submit` し、`handoff` が wav を Superwhisper に渡す。結果の文字列は履歴に入り、ウェイクワードなら Superwhisper が既に貼ったものとして `routeDictation` が走る。ペインが一つに決まれば `send-keys -l`。決まらなければ追加の貼り付けはしない。

スティッキーは、この `Dictation` が生まれた瞬間にだけ宛先を足し、`handoff` が結果を持ったあと（またはローカル文字起こしに落ちたあと）に純関数の状態機械へ渡す。機械が delivering と言うときだけアダプタが貼る。

### Where Things Live

- 遷移、失効、貼ってよいか、Enter してよいか、フォーカスを戻すか。`Sources/voice-switch/StickyMode.swift`。AppKit を import しない。
- キャプチャと貼り付けと Enter とフォーカス復帰。`MacApp.swift` の薄いアダプタ（AX / CGEvent / `NSPasteboard` / `tmux`）。
- 設定。`MacOSActions.stickyTarget`（`macOS.stickyTarget`）。欠落はオフ。Windows の `VoiceSwitchConfig` は `macOS` を持たないので、共有 JSON に書いても Windows は無視する。`DictationConfig` には足さない（parity のフィールド比較を崩さない）。
- モデル。`.claude/skills/verify-sticky-mode/StickyMode.tla`。

### 実装スライス

各スライスは前が緑になってから次へ。オフのとき既存呼び出しの引数は変えない。

1. **#high** 純関数 `StickyMode.swift` と `tests/macos/StickyMode/main.swift`。遷移は TLA と一致。Linux の `swiftc` で実行できる。
2. **#high** `macOS.stickyTarget: Bool?` と `stickyEnabled(configFlag:override:)`。メニュー「スティッキー貼り付け」は UserDefaults `stickyTarget` が無いとき設定を使い、トグルしたらその bool が勝つ。オフの経路は触らない。
3. **#high** 開始時キャプチャ。ウェイクワードで `action == nil` かつ有効なときだけ、メインスレッドで前面の pid / bundle id、AX の focused element（role / subrole / window）、可能なら端末の tty と `tmux list-panes` の pane id。自動録音とアクションではキャプチャしない。
4. **#xhigh** 配信時に Superwhisper の自動ペーストを通さない。有効なセッションの `handoff` は、`dictation.superwhisperMode` が自動ペースト無しと確認できたときだけその mode で開き、結果をスティッキーへ渡す。`routeDictation` は呼ばない（二重送信を避ける）。mode が無い、または切替に失敗したら Superwhisper を開かず、今のローカル文字起こしに落とす。オフの `handoff` は引数の既定 nil のまま。
5. **#xhigh** 貼り付け。機械が delivering かつ `stickyMayPaste` のときだけ。tmux なら括弧付きペースト（`ESC [200~`、本文の `-l`、`ESC [201~`）のあと `Enter`。それ以外は宛先を activate し、同じ AX 要素にフォーカスが戻ったときだけクリップボードを退避して Cmd+V。成功したときだけ Return（virtual key `0x24`）。失敗、要素無効、アプリ終了、3 分以上、セキュア、はクリップボードに本文を残してログし、前面には貼らない。成功時だけ退避したクリップボードを戻す。
6. **#high** フォーカス。配信を始める直前の前面アプリを覚える。Enter のあと、切り替わっていたならそのアプリへ戻す。戻せず、または切り替えていないなら、貼ったアプリのまま。tmux は前面を動かさない。
7. **#high** `README_ja.md` と `README.md` に短い節。`docs/windows-feature-parity.md` に、Windows は範囲外で `macOS` オブジェクトを無視する、と一行。見本の `config.example.json` にはキーを足さない（欠落 = オフ。sample gap を増やさない）。

### Gotchas

- `pasteDictation` はオフの履歴フォールバックに残す。スティッキーからそれを呼ぶと、1 秒アイドルと IME 判定で日本語の宛先に届かない。
- `compositionClear` のキーボードレイアウト判定はスティッキーに使わない。セキュアは `AXSecureTextField` と `IsSecureEventInputEnabled` だけ。IME の変換途中はプロトタイプでは検出できない。
- 本文を `log` に渡さない。`chars=` だけ。
- スライス 4 と 5 は二重貼り付けと「別アプリへ貼る」が同時に起きる場所なので、#high で失敗したら #xhigh に上げる。
