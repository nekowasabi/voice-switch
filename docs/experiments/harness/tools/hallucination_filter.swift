import Foundation
print("#tool hallucination_filter")  // tool_io.py checks this header
// For s1_eval.py. stdin: one Whisper text per line. stdout: 1 if nothing is left after removing hallucination phrases, else 0.
// Needs NoWakePolicy.whisperTextForAgreement (PR #38 and later).
while let l = readLine() { print(NoWakePolicy.whisperTextForAgreement(l).isEmpty ? "1" : "0") }
