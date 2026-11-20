# スティッキーモード結果（2026-11-20）

ブランチ `feat/sticky-mode`。push も PR もしていない。配線は `90f3d20`、配信の修正は `80fd417`、parity の印は `c2c24c9`。HEAD はこのファイルを追加したコミットで、その親は `c2c24c9`。

## 判定の要約

開始時の前面アプリと AX の入力欄、tmux の pane id は取れる。届け直しは activate / raise / 再フォーカスが limited で、同じ要素に戻らなければ貼らない。挿入はペーストボード + Cmd+V、または tmux の括弧付き `send-keys`。AX で値を書くのは not possible。セキュア欄、別アプリへの貼り付け、3 分超過はクリップボードに残す。Windows の同じ貼り戻しは今回 not possible（`macOS` を無視する）。

届けたあとのフォーカスは、配信開始時に前面だったアプリ。切り替えていなければ宛先のまま。復帰に失敗したら宛先に残してログする。tmux は前面を動かさない。

## 設定キー

- `macOS.stickyTarget`（`MacOSActions.stickyTarget: Bool?`）。無い、null、false はオフ。
- UserDefaults `stickyTarget`。キーが存在するとき設定より優先。メニュー「スティッキー貼り付け」。
- `config.example.json` には足していない。`DictationConfig` にも足していない。

## pstack の順

1. Use pstack why。`PLAN.md`。待つ理由は文字起こしの秒数だけではなく、宛先が前面アプリだけで、Superwhisper が終わったときの前面へ貼るため（`e6ebd54`、`223e91a`）。
2. Use pstack how。同じ `PLAN.md` のスライス。
3. Use pstack create-formal-verification-skill。`.claude/skills/verify-sticky-mode/`。Use pstack statechart。図は `FEASIBILITY.md` と `chart.mmd`。
4. Use pstack poteto-mode。implementer（opencode、`opencode-go/muse-spark-1.3-contributor#high`）が配線。コミット `90f3d20`、修正 `80fd417`。
5. Use pstack verify。この結果。

## 修正ループ

2。1 回目は implementer（`80fd417`）。セキュア拒否を `.stop(true)` の前にしていたので `pasteFail` が無効で、HUD が成功になった。Cmd+V のあと要素が消えると `enterFail` が `deliveries == 0` のまま無効だった。スティッキー中の `restoreFocusIfSuperwhisper` を外した。2 回目は orchestrator（`c2c24c9`）。parity が `let wantedMode = controlled ? ...` の印を要求するため、nil のセッションはその行と `enterSuperwhisperMode(wantedMode, target: target)` のままにし、スティッキーだけ `enteredMode`（`cfg.superwhisperMode`）を使う。

## 検証

| 項目 | 結果 |
|---|---|
| TLC `sh .claude/skills/verify-sticky-mode/check.sh` | PASS。`Model checking completed. No error has been found.` 1138 states generated、300 distinct、depth 9 |
| `break.sh` | PASS。`break: rejected a paste into the wrong place or a secure field` |
| Swift `swiftc` + `tests/macos/StickyMode/main.swift` | PASS。`PASS sticky logic 300 states`。exit 0 |
| `python3 tests/macos/StickyMode/test_sticky.py` | PASS。`PASS python sticky 300 states`。exit 0 |
| `verify.sh doctor` | exit 0。live Mac は SKIP `MAC_UNVERIFIED` |
| `verify.sh drive` | exit 0。末尾 SKIP `MAC_UNVERIFIED` |
| ライブのキャプチャ / 貼り付け / Enter | SKIP `MAC_UNVERIFIED` |
| `dotnet run --project dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj -c Release` | exit 0。258 PASS、0 FAIL |
| `python3 tests/parity/run_parity.py` | exit 1。SUMMARY pass=136 allow=23 fail=3（既存の 3 件のまま） |
| `python3 tests/url-actions/run.py` | exit 0。88 checks passed |
| swift-format lint（StickyMode、main、MacApp、Config） | exit 0。警告は既定 2 スペースに対する既存の 4 スペース。設定ファイルは無い。`--in-place` はファイル全体を書き換えるので掛けていない |
| swiftlint | 未インストール（none） |
| `python3 -m py_compile tests/macos/StickyMode/test_sticky.py` | exit 0 |
| `dotnet format` | 触った .cs が無いので未実行 |

モデルと Swift の BFS と Python は 300 状態で一致する。同期コミットは `0937dc4`（`stickyStep` はそれ以降変えていない）。

## takets が Mac で見ること

アクセシビリティ許可があること。Chrome、Safari、Electron（Grok Bot、Slack）、iTerm で開始時の欄を覚えること。iTerm2 + tmux の Claude Code では pane id を取り、括弧付きペーストのあと Enter で送信すること。tmux が無い iTerm は activate + Cmd+V + Enter。配信の開始時に前面だったアプリへフォーカスが戻ること。パスワード欄は拒否すること。フラグがオフのとき今までどおり Superwhisper が貼ること。3 分を超えた宛先は捨ててクリップボードに残ること。`~/Library/Logs/voice-switch.log` に本文が出ず `chars=` だけであること。

## 残っていること

- IME の変換途中は検出しない。貼り付けは止めない。
- Chrome / Electron で `kAXFocusedAttribute` が同じ要素に戻らないときは貼らず、クリップボードに残す。実機では未確認。
- Terminal.app の tty 取得と、Warp / Ghostty（tty スクリプトが無く、欄としての貼り付けに落ちる）は未確認。
- キャプチャはメインスレッドで osascript と tmux を最大約 400 ms ずつ待つ。
- オフのときもキャプチャ関数は一度メインに乗るが、セッションは nil のまま。handoff の引数を省略した経路は従来の mode 行のまま。
- 複数行の欄では Enter が改行になる。Slack や Claude Code では送信になる。
