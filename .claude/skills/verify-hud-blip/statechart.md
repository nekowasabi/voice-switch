# 音を拾ってから判定までのステートチャート

コード（`cf89a2f` の作業ツリー、A 案を反映・未コミット）から書き起こした。`HudBlip.tla` はこの図を縮めたもので、HUD に関わる部分だけを残している。各遷移のログ文字列は図の下の表にまとめた。

## macOS（`Listener.consume`, Sources/voice-switch/MacApp.swift）

```mermaid
stateDiagram-v2
    [*] --> Idle
    state "Idle（HUD なし）" as Idle
    state "Phrase（声らしい音が続く、マイク HUD なし）" as Phrase
    state "Recog（発話の文字起こし）" as Recog
    state "WakeWaiting（HUD waiting、アクションならアイコン）" as WakeWaiting
    state "WakeRecording（HUD recording）" as WakeRec
    state "AutoUnconfirmed（本文後の自動セッション未確認、HUD なし）" as AutoU
    state "AutoConfirmed（HUD recording）" as AutoC
    state "Handoff（superwhisper または POST）" as Handoff

    Idle --> Phrase: T1 isSpeech
    Phrase --> Idle: T2 hangover の無音
    Phrase --> AutoU: T3 自動セッションの条件を満たす
    Phrase --> Recog: T4 utterance が閉じる
    Recog --> WakeWaiting: T5 ウェイクワード一致
    Recog --> Idle: T6 一致なし
    WakeWaiting --> WakeRec: T7 本文の声
    WakeWaiting --> Idle: T8 startTimeout
    WakeRec --> Handoff: T9 終了条件
    AutoU --> AutoC: T10 wordCheck 一致
    AutoU --> WakeWaiting: T11 ウェイクワードで置き換え
    AutoU --> Idle: T12 未確認のまま終了
    AutoC --> Handoff: T13 終了条件
    Handoff --> Idle
```

| 遷移 | 条件 | ログ |
|---|---|---|
| T1 | `Segmenter.isSpeech`（rms がしきい値超え、または HAL） | なし。待機中のフレーズだけではマイク HUD を表示しない |
| T2 | `hangoverMs` の無音 | `voice: phrase ... -> shorter than minSpeechMs` / `automatic wake blocked: no voice activity` |
| T3 | `longEnough`、`NoWakePolicy.onSpeech`、`automaticWakeAllowed()`（HAL が 1.5 s 以上、またはその終了後 1.5 s 以内） | `dictation session: automatic`、`-> session opened` |
| T4 | Segmenter が `.utterance` を出す | `dictation recognition: complete` |
| T5 | `wakeWords` か `macOS.actions` に一致 | `dictation started ... -> action X` |
| T8 | `startTimeoutMs` | `dictation cancelled: nothing said after the wake word` |
| T9, T13 | `endSilenceMs`、ストップワード、`maxSeconds` | `dictation ended by ...` / `dictation finished by stop word` |
| T10 | Apple Speech と Whisper が語で一致（1 s ごと） | `dictation session: automatic confirmed by agreed words` |
| T11 | 自動セッション中のウェイクワード | `dictation session: automatic replaced by wake word` |

## Windows（`DictationRuntime`, dotnet/VoiceSwitch.Windows/DictationRuntime.cs）

```mermaid
stateDiagram-v2
    [*] --> Idle
    state "Idle（HUD なし、声らしい音の途中も Idle）" as Idle
    state "AwaitingBody（Waiting）" as Waiting
    state "AwaitingBody と本文の声（Recording）" as WRec
    state "未確認の自動セッション（Idle、HUD なし）" as AutoU
    state "Confirmed の自動セッション（Recording）" as AutoC
    state "Handoff" as Handoff

    Idle --> Waiting: LeadingWake または RejectedWake
    Idle --> AutoU: NoWakePolicy と SpeechGate
    Waiting --> WRec: HeardAfterWake
    WRec --> Handoff
    AutoU --> AutoC: CheckWords で一致
    AutoU --> Idle: 未確認のまま終了
    AutoC --> Handoff
    Handoff --> Idle
```

`relaxedD2` の本文待ちは WakeWaiting に対応させる。本文後でも未確認なら AutoU と同じく表示しない。`heardSpeech` / `stopHeard` は形式モデル外で、本文待ちの停止表示は従来どおり `.stopping` を維持する。`.ended`、`.followup`、配信表示は図から省略している。

## 修正した点滅の 2 つの経路（Mac）

1. Idle で雑音が HAL を鳴らして `phraseHAL > 0` になっても、Phrase では `.idle` のままでマイク HUD は表示しない。追従待機の既存条件を満たす場合の `.followup` は変更していない。
2. 1.5 s 以上話した直後の 1.5 s は `voiceRecentlyActive()` が真なので、雑音で T3 の自動セッションが開くことはある。ただし AutoUnconfirmed は `.idle` のままで、T10 の語の一致確認後にマイク HUD を表示する。確認後の `.recording` / `.stopping` は維持する。

Windows はどちらの場合も Idle のままで、HUD は出ない。ただし `agreedCharacters` が null の構成では、自動セッションがすぐ Confirmed 扱いになる。
