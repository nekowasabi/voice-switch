# v3: short wake word needs a pause before one-breath dictation

v2 train: all 8 remaining nonwake failures are n0 音声認識の精度を上げたい: dictationStart reads the wake word 音声 + rest 認識… (early-dictate on 認, then dictate at hangover).
Change: when the matched wake word is also the prefix of a longer wake word (音声 / 音声入力), one-breath dictation needs a pause after it: a 、 run or a >=150 ms gap to the next run. Unambiguous wake words (音声入力) keep starting dictation without a pause.
Expected: n0 false wakes clear on train; compound nonwake on test (音声ファイル, 音声合成) too. Risk: おんせい (STT -> 音声) in one breath with a short comma gap loses its one-breath start and waits.
