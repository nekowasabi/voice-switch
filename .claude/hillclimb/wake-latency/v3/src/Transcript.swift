// Transcript helpers — pure Swift, shared by macOS and Windows.
import Foundation

let punctuation = Set("、。,.!?！？「」")

func normalize(_ s: String) -> String {
    String(s.filter { !$0.isWhitespace && !punctuation.contains($0) })
}

struct Transcript {
    /// Runs of recognized text (about one character each for ja_JP) with seconds from the start of the audio.
    var runs: [(text: String, start: Double?, end: Double?)]
    var text: String { normalize(runs.map(\.text).joined()) }
}

/// Where to cut the audio so it starts after any leading wake words, plus the normalized text heard after them.
/// nil if the transcript does not start with a wake word or has nothing after it.
func dictationStart(_ t: Transcript, wakeWords: [String]) -> (cutAt: Double, rest: String)? {
    let targets = Set(wakeWords.map(normalize))
    let longest = targets.map(\.count).max() ?? 0
    var next = 0 // first run not yet consumed by a wake word
    var wakeEnd: Double?
    var lastWord = ""
    while true {
        var acc = ""
        var matched: Int?
        var word = ""
        var i = next
        while i < t.runs.count, acc.count <= longest {
            acc += normalize(t.runs[i].text)
            i += 1
            if targets.contains(acc) { matched = i; word = acc }
        }
        guard let matched else { break }
        wakeEnd = t.runs[matched - 1].end ?? wakeEnd
        next = matched
        lastWord = word
    }
    guard next > 0 else { return nil }
    let rest = normalize(t.runs[next...].map(\.text).joined())
    guard !rest.isEmpty else { return nil }
    // A wake word that also starts a longer one ("音声" / "音声入力") is an ordinary noun too: "音声認識" is not a wake.
    // Read it as a wake only when a pause follows it: a "、" run, or a gap of 150 ms or more before the next run.
    if targets.contains(where: { $0.count > lastWord.count && $0.hasPrefix(lastWord) }) {
        let after = t.runs[next]
        let comma = after.text.contains(where: punctuation.contains)
        let gap = (after.start ?? 0) - (wakeEnd ?? .infinity)
        guard comma || gap >= 0.15 else { return nil }
    }
    // The run right after the wake word is often the pause ("、"), so cutting at its start keeps the first syllable whole.
    guard let cutAt = t.runs[next].start ?? wakeEnd.map({ $0 + 0.05 }) else { return nil }
    return (cutAt, rest)
}
