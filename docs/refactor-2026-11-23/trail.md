# 構造の改善の作業記録（2026-11-23）

目標: 機能を 1 つも変えずに、AI が書いた意図しないコードが構造（コンパイラ、検査、フック）で止まる形にする。
完了の条件: 案の段階 0〜7 の確認がすべて通る。加えて、統合ブランチで sh tests/check.sh、sh tests/equivalence.sh <基準>、python3 tests/parity/run_parity.py --require-swift、python3 tests/url-actions/run.py --require-swift が通る。

## 進め方
- 1 段階を 1 ブランチ・1 worktree で進める。実装は ateam の single で Codex（gpt-6.2-sol）に頼む。互いに独立した段階は並行して進める。
- 各 Codex の報告はそのまま信じず、統合ブランチで同じ検査を流し直してから取り込む。

## 記録
| 時刻 (JST) | 段階 | 出来事 | 判断 |
|---|---|---|---|
| 07:37 | 0 | tests/check.sh に TLA+ の 6 モデル（7 回の実行）と wake-allowances を直列で足した。PASS (73 s)。構文の誤りを入れると exit 150 で止まる | 2300013 としてコミット |
| 07:38 | 1,2,3,6,7 | Codex 4 人を並行で起動（hooks+warnings / equivalence / covers / win-tests） | 段階 4・5 は差分検査（段階 2）が要るので後に回す |
| 07:41 | - | ateam wait が、作業中のペインを「pane gone」と誤って判定した | ペインのタイトルを見て待ち、その後に wait を流し直して返信を受け取った |
| 07:51 | 1,2,3,6,7 | 4 人とも報告。段階 6 は、macOS ではテストが WindowsDesktop を要して動かなくなるため、Tests.csproj の置き換えは戻された。TrayDiagnosticsProbe だけはアプリへの参照に替えた（元は、存在しない LoopbackCapture.cs を名指ししていてビルドできなかった） | 段階 6 は「Tests.csproj は置き換えない」で閉じる |
| 07:57 | 2+3 | 統合後、網羅の検査が、covers.txt の無い verify-refactor-equivalence を止めた | covers.txt を足した（fa7e17e）。検査は狙いどおりに働いた |
| 07:52-08:03 | 4, 検査の修正 | 段階 4（Swift の Core ライブラリ）と、検査の修正 2 件（TLC の一時ディレクトリの衝突、parity の既存の失敗 5 件）を並行で進めた | - |
| 08:12 | 統合 | 差分検査が 2300013 に対して DIFF parity。基準のコミットでは parity が exit 1、統合ブランチでは exit 0 | 製品の差ではなく、検査を直した分の差。構造の変更の基準は、検査を直した後のコミット（e12e124、bc1dc57）に置く |
| 08:19 | 統合 | parity の fail=3 は、シェルの dotnet が、バージョンの無い mise の shim を指していたため | PATH の先頭に $HOME/.dotnet を置いて流し直した |
| 08:24 | 統合 | 残った 1 件は、tests/cough-filter/csharp の 2 つのプロジェクトが obj/ と bin/ を共有しているためで、毎回落ちる | 元からある不具合。gate-fixes-2 で根本から直す |
| 08:25 | 5, 検査の修正 2 | 段階 5（Mac の口述の状態を Core の型へ）と、検査の修正 2 を並行で起動した | 段階 5 の基準は bc1dc57 |
| 08:46-08:53 | 5, 検査の修正 2 | 段階 5: 6 コミット。Listener.consume は 432 行から 396 行。equivalence bc1dc57 は passed (303 s)。検査の修正 2: tests/cough-filter/csharp に Directory.Build.props を置き、出力先をプロジェクトごとに分けた。負荷の低いときの check.sh の合計は 74 秒 | 取り込んだ |
| 08:54 | 統合 | verify-action-wake-latency が古い名前 seg.tailSilentFrames を grep していて、基準のコミットでも落ちていた | 今の名前 seg.probeSilentFrames に合わせた |
| 09:14 | 統合 | code-mutants の verify-route-in-batch が「anchor missing」。コードの名前が questionIDs から requestIDs に変わっていた。基準のコミットでも同じ | 表を今の名前に合わせた。流し直して CAUGHT |
| 09:20 | 全体 | 最終の検証（下の表） | - |

## 最終の検証（refactor/integration、macOS）
| 検証 | 終了コード | 最後の行 |
|---|---|---|
| sh tests/check.sh | 0 | PASS all quick checks (94 s) |
| sh tests/equivalence.sh e12e124 | 0 | Equivalence passed (265 s) |
| python3 tests/parity/run_parity.py --require-swift | 0 | SUMMARY pass=137 allow=29 fail=0 |
| python3 tests/url-actions/run.py --require-swift | 0 | URL actions: 88 checks passed |
| verify-action-wake-latency drive | 0 | items pass=20 skip=1 fail=0（skip は awl-swift-exec。Linux 向けの項目） |
| verify-triage-execution check.sh risk | 1（想定どおり） | FAIL StrictNoWrongExecution だけ |
| verify-triage-execution mutants.sh / code-mutants.sh | 0 / 0 | CAUGHT ... |
| verify-journal-append mutants | 0 | CAUGHT read-dot-folders by T6 |
| verify-hud-blip break.sh | 0 | 旧規則で NoiseNeverShowsHud 違反を検出: pass |
| verify-sticky-mode break / anchors / finding | 0 / 0 / 0 | F1 stands（既知の指摘のまま） |
| verify-voice-switch-wake drive（5 機能） | 0 | evidence ... |
| verify-voice-switch-grokbot build / drive（5 機能）/ tests / swift-mirror | 0 | MATCH など |
| verify-voice-switch-mac build / hud | 0 | ok both HUDs draw vector glyphs |

この Mac では流せないもの: verify-voice-switch-dotnet の cli（Linux 専用。macOS の date に %N が無く、verify.sh の 180 行目で止まる）、verify-voice-switch（WSL から Windows の exe を動かす）、make win-test の Windows 側、verify-pr-merge（開いている PR 用）。実機のマイクと Superwhisper への受け渡しは、合成音声による確認だけ。

## 完了の前に足した確認（ff250ed）
| 確認 | 結果 |
|---|---|
| 段階 5 の差分を読み合わせた（MacApp.swift と MacDictationSession.swift） | リセットの順番、frames(ms:) と Int(rate) * ms / 1000 の計算、ログの文字列は元と同じ |
| make app を e12e124 と HEAD のきれいな作業ツリーで実行 | どちらも成功。.app の中身は同じ 20 ファイル。--vad-selftest と --check の出力は同じ |
| Windows の Release win-x64 ビルド（--no-incremental） | 0 Warning(s) 0 Error(s)。Windows.Tests も 0 Warning(s) |
| 壊し版の記録の中身（CAUGHT 以外の行） | 無し |
| sh tests/check.sh | PASS all quick checks (76 s) |
| sh tests/equivalence.sh e12e124 | Equivalence passed (264 s)。SAME が 15 項目 |

環境について分かったこと:
- 新しく取り出した作業ツリーでは、onnxruntime-swift-package-manager の取得が「Repository not found」で失敗する（基準のコミットも同じ）。手元の .build のキャッシュを写すとビルドできる。
- parity は、PATH の先頭に $HOME/.dotnet を置かないと、mise の shim が dotnet のバージョン無しで落ちる。
