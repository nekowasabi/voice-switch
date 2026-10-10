# v2: prefix wake words fire after 150 ms of silence

v1 train: every 音声入力 case passes via early-wake, but 音声に入る (prefix of 音声に入るよ) and おんせい (STT -> 音声, prefix of 音声入力) never fire early: the prefix guard blocks them and they fall back to hangover (337-408 ms, 29 lone/pause cases).
Change: a transcript that is a whole wake word but also the prefix of a longer one fires as a bare wake after 150 ms of tail silence (probe also runs at that frame). 150 ms is longer than the 90 ms gaps inside 音声入力, so 音声 + 150 ms silence cannot be mid-word.
Expected: lone/pause cases for w1/w2 drop to ~150 ms + STT. Risk: a real speaker pausing >150 ms inside 音声入力 fires as 音声 (then the rest becomes dictation text).
