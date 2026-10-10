# v4: pause threshold for the short wake word 150 ms -> 50 ms

v3 fixed every compound false wake but lost detection on 2 train cases (おんせい、 at a fast rate: STT gives 音声テスト… with no 、 run and a gap under 150 ms).
Hypothesis: STT run timestamps still show a short gap at a fast comma, while a compound (音声認識) has none. Change: threshold 0.05 s.
Expected: train detection back to 201/216 with nonwake still 96/96. Risk: a compound with a slight articulation gap false-wakes again.
