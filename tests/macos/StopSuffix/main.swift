// Build and run (on macOS):
//   swiftc Sources/voice-switch/Transcript.swift tests/macos/StopSuffix/main.swift -o /tmp/stop-suffix
//   /tmp/stop-suffix; echo "exit=$?"
import Foundation

func check(_ ok: Bool, _ what: String) {
    print((ok ? "PASS " : "FAIL ") + what)
    if !ok { exit(1) }
}

// One run per character, 0.1 s apart, as the recognizer returns for ja_JP.
func transcript(_ text: String) -> Transcript {
    Transcript(runs: text.enumerated().map { i, c in (text: String(c), start: Double(i) * 0.1, end: Double(i) * 0.1 + 0.1) })
}

let stopWords = [normalize("入力ストップ")]

// Criterion 1: a stop word followed by more speech does not stop.
check(stopWordSuffixStart(transcript("今日は入力ストップです"), stopWords: stopWords) == nil,
      "今日は入力ストップです does not stop")

// Criterion 2: the stop word ends the utterance after a comma. The cut is at its first run (index 8, 0.8 s),
// and the body before the cut is the text without the stop word.
do {
    let t = transcript("今日は入力です、入力ストップ")
    let start = stopWordSuffixStart(t, stopWords: stopWords) ?? -1
    check(abs(start - 0.8) < 1e-9, "今日は入力です、入力ストップ stops at the stop word (cut \(start) s)")
    let body = normalize(t.runs.filter { ($0.start ?? 0) < start }.map(\.text).joined())
    check(body == "今日は入力です", "the body drops the stop word (body \(body))")
}

// Criterion 3: punctuation after the stop word still stops. The 。 run normalizes to nothing.
do {
    let t = transcript("入力ストップ。")
    check(t.text == "入力ストップ", "入力ストップ。 normalizes to the stop word")
    check(stopWordSuffixStart(t, stopWords: stopWords) == 0.0, "入力ストップ。 stops, cut at its first run")
}

print("all stop suffix checks passed")
