# voice-switch のエージェント向け規則

変更を「完了」と言う前に `make check` を流し、終了コードと最後の数行を報告に引用する。swift か dotnet が見つからなければ `make check` は落ちる。SKIP で通ったことにはしない。

| 規則 | 守っているもの | 起きた例 |
|---|---|---|
| Swift と C# の変更は、ビルドとテストを通してから完了と言う | `make check`（`swift build -c release`、url-test、win-test、`--require-swift` 付きの parity、verify-wake-allowances）。27183c9 の親コミットではコンパイルエラーで止まる | 27183c9、07c8db2、PR #71 の初版 |
| dotnet は Makefile の 1 か所で探す。スクリプトに探し方を足さない | Makefile の `DOTNET`。`--version` に答える最初の候補を PATH の先頭に置く | mise のシムが版未設定で parity が FAIL 3 件になり、変更のせいか分からなくなった |
| 検査の FAIL が「前からある」と言うときは、main で `make check` を流して比べる | main の `make check` が FAIL 0 で通るので、出た FAIL は変更が持ち込んだもの | PR #71 で、比較の相手を PR の古い版にしていた |
| ウェイクがどのセットに当たるかは、振る舞いの fixture で確かめる。ソースの関数名や式を grep する検査を足さない | `tests/parity/fixtures/wake_verdict.json` を Swift と C# の両方に流す | PR #71 で、判定を作り直すたびに run.py と verify スキルの grep を書き換えていた |

`MacApp.swift` の配線（`handoffBusy`、チャイム、`Platform.runMacAction`）は単体で動かせないので、`tests/url-actions/run.py` と `tests/parity/run_parity.py` の文字列検査が残っている。これらは CI が無いかぎり、上の表で置き換えられていない判断事項として扱う。

CI は無い。`make check` を流すかどうかを強制するものはなく、この表と報告の引用だけが頼り。
