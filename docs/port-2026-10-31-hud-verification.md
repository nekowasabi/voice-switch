# 2026-10-31 07:00–11:00 コミットの移植検証項目（Mac → Windows）

対象コミット（committer date 07:00–11:00）:

| commit | 時刻 | 件名 |
|---|---|---|
| 57852c9 | 09:30 | fix(hud): 物音だけでマイク HUD を出さず、アクションのアイコンを後の表示に残さない |
| 3f2430e | 09:30 | test(verify): 物音だけで HUD が出ないことを Mac と Windows で検査するスキルを足す |
| c472ac9 | 09:47 | fix(hud): アクションの録音中もアイコンを出し、Escape でウェイクの録音を中止できるようにする |

Codex（GPT-6-Astra, medium）のレビュー（agmsg review チーム、2026-10-31T10:41Z）の指摘 7 件と補足を、コードで確かめたうえで反映した版。

## 1. 機能一覧と Windows の現状

| ID | 機能（Mac の変更） | Mac の箇所 | Windows の対応箇所 | Windows の現状 |
|---|---|---|---|---|
| F1 | 物音だけでマイク HUD を出さない。`.hearing` を廃止し、自動セッションは「声を聞いたが言葉が未確定」の間 `.idle`。待機中の phrase も `.idle` | `Sources/voice-switch/MacApp.swift` `Listener` の phase 計算（旧 1094 行付近）、`HUD.render` の `(.hearing, nil)` 削除 | `dotnet/VoiceSwitch.Windows/DictationRuntime.cs:975-981` `PublishPhase` は `IsActive && !Confirmed(session)` で `Idle`。`Confirmed`（:984-985）は **`agreedCharacters is null` なら自動セッションでも最初から真**、`CheckWords`（:987-996）は認識器の失敗でも確定にする | **一致認識器が構成され正常に動く場合に限り**同等（`Hearing` は元々無い）。認識器なし・認識器失敗では物音でも `Recording` が出得る（`verify-hud-blip/SKILL.md:79-81` でもモデルの仮定外） |
| F2 | 前のアクションのアイコンを後の表示に残さない。`.waiting`/`.recording` 以外では `actionImage = nil` | `HUD.show`（旧 1900 行付近） | `DictationRuntime.cs:1046` `WaitingFor(action)` は `Waiting` の時だけ呼ぶ。`Tray/DictationHud.cs:55-60` `WaitingIcon(null)` で消去 | `Waiting` 単体では同等（毎回 action か null で上書き）。F3 移植後は Waiting/Recording 以外で消す処理が要る |
| F3 | アクションの録音中（`.recording`）もアイコンを出し、右上に赤点を重ねる。`phaseCont` は全 phase で `action?.icon` を流す | `HUDGlyphView.draw`（1699 行付近）、`Listener` の `phaseCont.yield`（1103 行） | `Tray/DictationHud.cs:323` `OnPaint` は `HudGlyph.Waiting` の時だけ画像を描く。`TrayHost.cs:59` `OnWaitingFor` 経由 | **未移植** |
| F4 | superwhisper が既定の cancel（Escape）を設定に書かないので、未設定なら Escape を cancel にする | `Hotkeys`（613-626 行付近）`escape` フォールバック | `VoiceSwitch.Windows.Core/DictationHotkeys.cs:29` `DefaultCancel = 0x1B`。`Load`（:71-93）は設定なし・JSON 破損・キー値が読めない時に既定へ戻る。`OnKey`（:115-150）は Finish を先に照合。`DictationRuntime.cs:424,838` で `Begin`、`845` で Cancel 処理 | 同等。要検証のみ |
| F5 | TLA+ 検査スキル `verify-hud-blip`（Mac/Windows 両 cfg）と `verify-hud-noise` の更新 | `.claude/skills/verify-hud-blip/` | 同スキルの `HudBlip-windows.cfg` | 製品機能ではない。F1 の設計検査の手段 |

Mac と Windows の phase の違い（期待値を書くときの前提）:
- Windows の `DictationPhase`（`DictationCore.cs:61-68`）は `Idle/Waiting/Recording/Ended/Followup` のみ。Mac の `.stopping` は無く、停止語の仮確定は `provisionalStop ? Ended`（`DictationRuntime.cs:978`）で表す。
- 一息ウェイク（ウェイク+本文）は Mac も Windows も `Waiting` を経ずに `Recording`（`DictationRuntime.cs:978`、:1038 のコメント）。

付随: `tests/parity/contracts/platform_parity.json:338-346` の `wake_action_hud_icon` は Mac 側マーカーが `actionImage` に変わった。Windows 側マーカー（`WaitingImage`, `ExtractAssociatedIcon`）は F3 移植で名前を変えるなら同時に更新する。ただし `tests/parity/run_parity.py:235-244` は部分文字列の有無しか見ないので、parity 合格は移植の証拠にならない。

## 2. 検証の種類

| 種類 | 何で | どこで |
|---|---|---|
| 形式 | `verify-hud-blip` / `verify-hud-noise` の TLA+ モデル。設計の順序の検査であり、実装の検査ではない | Linux |
| 構造 | `tests/parity/run_parity.py`（マーカーの有無だけ） | Linux |
| Win 単体 | `dotnet/VoiceSwitch.Windows.Tests`（`net8.0-windows`、csproj:4）。製品は Windows Forms（`VoiceSwitch.Windows.csproj:4-5`）、HUD は Win32 `IsWindowVisible`（`DictationHud.cs:42`）を呼ぶ | Windows（WSL から Windows 側の dotnet を起動してよい） |
| Win 実機 | `verify-voice-switch`（Windows 専用 runner）で WAV を流しログ・スクショを取る | Windows |

形式モデルの合格と実装の合格は別々に記録する。片方の合格をもう片方の合格として扱わない。

## 3. 検証項目

### F1 物音で HUD を出さない

前提（F1-1〜F1-3）: noWakeWord 有効、`echoCancellation` が false でない、一致認識器（`agreedCharacters`）が構成され正常に動く。

| No | 条件 | 期待 | 種類 |
|---|---|---|---|
| F1-1a | 前提のもと、咳・キーボード・ドア等の短い非音声で自動セッションが開く（設計） | 不変条件「確定前に HUD を出さない」が pass | 形式: `verify-hud-blip`（Windows cfg） |
| F1-1b | 同条件（実装） | HUD は `Idle` のまま。ログに `dictation phase: Recording` が出ない | Win 実機 |
| F1-2 | スピーカー再生が検出器を一瞬鳴らす | 同上 | Win 実機 |
| F1-3a | 話し続ける（設計） | 声の人にはいずれ HUD が出る（liveness `VoiceShowsHud` が pass）。モデルは次の voice フレームで確定する抽象化（`HudBlip.tla:65-72`）なので、実際の `CheckWords` の進み方は保証しない | 形式 |
| F1-3b | 話し続け、`CheckWords` が一致（実装） | 確定後に `Recording`、ログ `automatic confirmed by agreed words` | Win 実機 |
| F1-4 | 一致認識器が例外・失敗 | 確定扱いで `Recording`、ログ `automatic confirmed by unavailable word agreement`（物音でも表示され得るのは既知の仕様として記録） | Win 単体 |
| F1-5 | 一致認識器が未構成（`agreedCharacters == null`） | 自動セッション開始時から確定扱いで `Recording`。物音でも表示され得る（モデルの仮定外、既知の仕様として記録） | Win 単体 |
| F1-6 | 未確定のまま無音で終わる | `Ended` を出さず `Idle` に戻る。履歴保存時のみ `Saved` | Win 実機ログ |
| F1-7 | followup 期間中 | `Followup` を出す。`verify-hud-blip` の対象外（SKILL.md:105-111）なので形式検査済みとは扱わない | Win 実機 |
| F1-8a | ウェイクワード単独（自動でない） | `Waiting` → 本文を話すと `Recording` | Win 実機 |
| F1-8b | 一息ウェイク（自動でない） | `Waiting` を出さず直接 `Recording`。ウェイク音を鳴らさない（:1036-1039） | Win 実機 |

### F2 前のアイコンを残さない

| No | 条件 | 期待 | 種類 |
|---|---|---|---|
| F2-1 | アイコン付きアクション → 終了 → 通常ウェイク（action なし）の `Waiting` | マイク glyph（アイコンなし） | Win 単体（HUD）/ Win 実機スクショ |
| F2-2 | アイコン付きアクション → 終了 → 自動セッションの `Recording` | 赤点のみ（アイコンなし） | 同上 |
| F2-3 | アクション A → アクション B（別アイコン） | B のアイコン | 同上 |
| F2-4 | `Ended`/`Followup`/`Idle`/Paused・Resumed 通知/`Saved` | アイコンを描かない | 同上 |
| F2-5 | アイコンファイルが読めない | マイク glyph のまま、ログ `hud: action icon ... could not be loaded` | Win 単体 |

### F3 録音中もアクションのアイコン（移植対象）

| No | 条件 | 期待 | 種類 |
|---|---|---|---|
| F3-1 | アクションのウェイクワード単独 → `Waiting` | アイコンを 22px で描く（従来どおり） | Win 実機スクショ |
| F3-2 | F3-1 の後に本文を話し `Recording` | アイコン + 右上に赤点（Mac: 直径 = side×0.32、`Ink.red`、右上端に接する） | Win 実機スクショ（画像で位置と色を判定） |
| F3-3 | アクションの一息ウェイクで `Waiting` を経ずに `Recording` | アイコン + 赤点。現状は `WaitingFor` が `Waiting` でしか呼ばれない（:1046）ので、移植では `Recording` にも action を渡す必要がある | Win 実機スクショ |
| F3-4 | アクションなしの `Recording` | 従来の赤点 glyph（アニメーション含む） | Win 実機 |
| F3-5 | アクション録音中に停止語 → 仮確定（`provisionalStop`） | Windows では `Ended` 表示になり、アイコンを描かない。Mac の `.stopping`→処理中表示とは状態も表示時間も別物として扱う | Win 実機 |
| F3-6 | `HudGlyph.Recording` でアイコン描画中のアニメーション timer | 描画が崩れない／HUD 非表示で timer が止まる | Win 実機 |
| F3-7 | parity 契約 | `wake_action_hud_icon` の Windows マーカーが新実装を指し parity が通る。**構造確認のみ**で、F3-1〜F3-3 の代わりにはならない | 構造 |

### F4 Escape で中止

| No | 条件 | 期待 | 種類 |
|---|---|---|---|
| F4-1 | superwhisper 設定に `cancelRecordingShortcut` 無し、ウェイクで `Waiting` 中に Escape | `dictation cancelled`、送信されない、Escape は他アプリに渡らない | Win 単体（`Tests/Program.cs:3179-3214` 系）/ Win 実機 |
| F4-2 | 同、`Recording` 中に Escape | 同上 | 同上 |
| F4-3 | `cancelRecordingShortcut` が Escape 以外の有効なキー、`toggleRecordingShortcut` も Escape 以外 | 設定キーで中止。Escape は素通し | Win 単体 |
| F4-4 | `cancelRecordingShortcut` が明示的に Escape | Escape で中止 | Win 単体 |
| F4-5 | 設定 JSON が null・壊れている・cancel の値が読めない文字列/非文字列 | 既定 Escape で中止 | Win 単体 |
| F4-6 | `toggleRecordingShortcut` が Escape（finish と cancel が衝突） | Finish が先に照合され（:139-148）、Escape は Finish として扱われる。中止にはならない | Win 単体 |
| F4-7 | 自動（no-wake）セッション中の Escape | 中止せず前面アプリへ渡す（`Begin` しない、:634） | Win 単体 / Win 実機 |
| F4-8 | ディクテーション外で**新たに**押した Escape | 素通し（`End()` 後は armed=null） | Win 単体 |
| F4-9 | Escape で中止して `End()` した直後の、同じ押下のリピート・キーアップ | 引き続き飲み込む（`swallowUp` は `End()` で消えない、:106-113, :119-127）。約 2 秒後の古いリピートは飲み込まない（`DropStale`、テストの `staleRepeat`） | Win 単体 |
| F4-10 | 手動ホットキー開始の録音中に Escape | 中止 | Win 単体 |

### 回帰（Mac 側）

| No | 条件 | 期待 | 種類 |
|---|---|---|---|
| M-1 | `verify-hud-blip`（Mac cfg）・`verify-hud-noise` | pass | 形式 |
| M-2 | `verify-voice-switch-mac` の simulate | wake/stop/自動セッションの既存ログが変わらない | Mac 実機 |
