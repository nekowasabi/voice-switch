// Word enrollment: turn mis-transcriptions of a spoken wake/stop word into aliases.
//
// A user records the word a few times, each clip goes through the production
// transcriber, and the normalized results become alias candidates. This file is
// pure Foundation (no Speech framework) so the Linux swiftc harness can test it;
// both platforms implement the same rules (see WordEnrollment.cs on Windows and
// tests/parity/fixtures/enroll_words.json).
import Foundation

/// One enrollment clip after the filter.
public struct WordEnrollmentResult: Equatable {
    public let transcript: String
    public let normalized: String
    public let verdict: WordEnrollmentVerdict
}

/// Filter outcome: the clip is already detected, is a new alias, or is rejected with a reason.
public enum WordEnrollmentVerdict: Equatable {
    case alreadyMatches
    case alias
    case rejected(String)

    public var label: String {
        switch self {
        case .alreadyMatches: return "already matches"
        case .alias: return "new alias candidate"
        case .rejected(let reason): return "rejected: \(reason)"
        }
    }
}

public struct WordEnrollmentError: Error, CustomStringConvertible {
    public let description: String
    public init(_ description: String) { self.description = description }
}

public enum WordEnrollment {
    /// Normalized candidates shorter than this are rejected (they would false-trigger).
    public static let minAliasLength = 3
    /// A candidate heard in this many past dictation journal entries is rejected.
    public static let journalHitThreshold = 3
    /// Normalized aliases rejected for --stop: a stop word matches at the END of a
    /// transcript, so an alias that is a common sentence ending would stop ordinary speech.
    public static let stopSuffixRisky = ["です", "ます", "でした", "ました", "した", "ない", "ある", "いる", "する", "こと"]

    /// Classify one normalized transcript. existingWords is the configured list plus
    /// the enrolled word itself; journalTexts are raw past dictation texts.
    public static func classify(candidate: String, existingWords: [String], journalTexts: [String], isStop: Bool) -> WordEnrollmentResult {
        let norm = normalize(candidate)
        if norm.isEmpty {
            return WordEnrollmentResult(transcript: candidate, normalized: norm, verdict: .rejected("empty transcript"))
        }
        if Set(existingWords.map(normalize)).contains(norm) {
            return WordEnrollmentResult(transcript: candidate, normalized: norm, verdict: .alreadyMatches)
        }
        if norm.count < minAliasLength {
            return WordEnrollmentResult(
                transcript: candidate, normalized: norm,
                verdict: .rejected("too short (\(norm.count) chars, minimum \(minAliasLength))"))
        }
        let hits = journalTexts.filter { normalize($0).contains(norm) }.count
        if hits >= journalHitThreshold {
            return WordEnrollmentResult(
                transcript: candidate, normalized: norm,
                verdict: .rejected("heard in \(hits) past dictations (limit \(journalHitThreshold))"))
        }
        if isStop, stopSuffixRisky.contains(norm) {
            return WordEnrollmentResult(
                transcript: candidate, normalized: norm,
                verdict: .rejected("common sentence ending; it would stop ordinary speech"))
        }
        return WordEnrollmentResult(transcript: candidate, normalized: norm, verdict: .alias)
    }

    /// Classify every clip transcript; keeps one entry per clip, in order.
    public static func candidates(transcripts: [String], existingWords: [String], journalTexts: [String], isStop: Bool) -> [WordEnrollmentResult] {
        transcripts.map { classify(candidate: $0, existingWords: existingWords, journalTexts: journalTexts, isStop: isStop) }
    }

    /// Append aliases to the configured list without reordering or duplicating.
    /// Returns the merged list and the entries actually added.
    public static func mergedWordList(existing: [String], aliases: [String]) -> (merged: [String], added: [String]) {
        var merged = existing
        var seen = Set(existing.map(normalize))
        var added: [String] = []
        for alias in aliases {
            let norm = normalize(alias)
            guard !norm.isEmpty, !seen.contains(norm) else { continue }
            seen.insert(norm)
            merged.append(alias)
            added.append(alias)
        }
        return (merged, added)
    }

    /// The "text" of one journal JSONL line, or nil when the line is not a dictation record.
    public static func journalLineText(_ line: String) -> String? {
        guard let data = line.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let text = obj["text"] as? String
        else { return nil }
        return text
    }

    /// Past dictation texts for the false-trigger check. Best effort: missing or
    /// unreadable files contribute nothing. Handles both a directory of daily
    /// `yyyy-MM-dd.jsonl` files and a single `.jsonl` file.
    public static func loadJournalTexts(journalDir: String?) -> [String] {
        let raw = (journalDir?.trimmingCharacters(in: .whitespacesAndNewlines)).flatMap { $0.isEmpty ? nil : $0 }
            ?? DictationJournal.defaultDirectory()
        let expanded = Platform.expandPath(raw)
        var files: [String] = []
        if expanded.lowercased().hasSuffix(".jsonl") {
            files = [expanded]
        } else {
            files = (try? FileManager.default.contentsOfDirectory(atPath: expanded))?.filter { $0.hasSuffix(".jsonl") }.sorted()
                .map { (expanded as NSString).appendingPathComponent($0) } ?? []
        }
        var texts: [String] = []
        for file in files {
            guard let data = FileManager.default.contents(atPath: file),
                  let content = String(data: data, encoding: .utf8)
            else { continue }
            for line in content.split(separator: "\n", omittingEmptySubsequences: true) {
                if let text = journalLineText(String(line)) { texts.append(text) }
                if texts.count >= 50_000 { return texts }
            }
        }
        return texts
    }

    /// Append aliases to the `key` array in the user config file, preserving every
    /// other key and value. Makes a `config.json.bak` backup first. --dry-run
    /// returns what would be added without writing. Returns the entries added.
    @discardableResult
    public static func appendWordAliases(configPath: String, key: String, aliases: [String], dryRun: Bool) throws -> [String] {
        let url = URL(fileURLWithPath: configPath)
        let data = try Data(contentsOf: url)
        guard var dict = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw WordEnrollmentError("config is not a JSON object: \(configPath)")
        }
        if dict[key] != nil, !(dict[key] is [String]) {
            throw WordEnrollmentError("config key \(key) is not a word list")
        }
        let existing = (dict[key] as? [String]) ?? []
        let (merged, added) = mergedWordList(existing: existing, aliases: aliases)
        guard !added.isEmpty else { return [] }
        if dryRun { return added }
        let backup = configPath + ".bak"
        try? FileManager.default.removeItem(atPath: backup)
        try FileManager.default.copyItem(atPath: configPath, toPath: backup)
        dict[key] = merged
        let out = try JSONSerialization.data(withJSONObject: dict, options: [.prettyPrinted, .sortedKeys])
        try out.write(to: url, options: .atomic)
        return added
    }
}
