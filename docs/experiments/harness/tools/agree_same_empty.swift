import Foundation
// For app_eval.py. stdin: one Whisper text per line.
// stdout: "<passes if Apple==Whisper> <passes if Apple is empty>" (1/0), using main's agreedCharacters.
while let l = readLine() { print(NoWakePolicy.agreedCharacters(l, l) >= NoWakePolicy.minAgreedCharacters ? 1 : 0, NoWakePolicy.agreedCharacters("", l) >= NoWakePolicy.minAgreedCharacters ? 1 : 0) }
