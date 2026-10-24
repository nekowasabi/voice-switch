import Foundation

enum WakeRule: String, Codable { case strict, relaxedD2 = "relaxed_d2", followup, autoB = "auto_b" }

struct WakeOrigin: Equatable {
    let rule: WakeRule
    let inputIdleSeconds: Double?
    var isAutomatic: Bool { rule != .strict }
    var fields: String {
        let value = inputIdleSeconds.flatMap { $0.isFinite && $0 >= 0 ? $0 : nil }
        let idle = value.map { String(format: "%.3f", locale: Locale(identifier: "en_US_POSIX"), $0) } ?? "-1"
        return "rule=\(rule.rawValue) inputIdleSeconds=\(idle)"
    }
}

func sidField(_ id: UUID?) -> String {
    guard let id else { return "sid=-" }
    return "sid=\(id.uuidString.lowercased())"
}

/// `pauseShortcut` names, the same KeyboardEvent.code form Windows DictationHotkeys.Parse reads ("Control+Shift+F7").
/// Returns a Carbon virtual key code and Carbon modifier mask. Meta/Super/Win is Command. Pure Foundation for the parity harness.
enum PauseShortcut {
    private static let modifiers: [String: UInt32] = ["control": 0x1000, "ctrl": 0x1000, "shift": 0x200, "alt": 0x800,
                                                      "meta": 0x100, "super": 0x100, "win": 0x100]
    // kVK_* values; letters, digits and F-keys are not sequential on Mac.
    private static let keys: [String: UInt32] = {
        var keys: [String: UInt32] = ["Space": 0x31, "Escape": 0x35, "Enter": 0x24, "Tab": 0x30, "Backspace": 0x33]
        let letters: [UInt32] = [0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E,
                                 0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06]
        for (i, code) in letters.enumerated() { keys["Key\(Character(UnicodeScalar(65 + i)!))"] = code }
        for (i, code) in [0x1D, 0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19].enumerated() { keys["Digit\(i)"] = UInt32(code) }
        for (i, code) in [0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F].enumerated() { keys["F\(i + 1)"] = UInt32(code) }
        return keys
    }()

    static func parse(_ name: String) -> (keyCode: UInt32, modifiers: UInt32)? {
        let parts = name.split(separator: "+", omittingEmptySubsequences: false).map(String.init)
        var mask: UInt32 = 0
        for part in parts.dropLast() {
            guard let bit = modifiers[part.lowercased()] else { return nil }
            mask |= bit
        }
        return keys[parts.last ?? ""].map { ($0, mask) }
    }
}

/// One MISS label per observed recognition, pressed via Ctrl+Shift+F8 within 10 s.
/// Pure Foundation so the Linux parity harness can test it; the hotkey itself stays
/// a RegisterEventHotKey (no CGEventTap, no new permissions).
final class WakeMissLabel {
    static let shared = WakeMissLabel()
    private let lock = NSLock()
    private var latest: (id: Int, origin: WakeOrigin, at: Date)?

    func observe(id: Int, origin: WakeOrigin, at: Date = Date()) {
        lock.lock()
        latest = (id, origin, at)
        lock.unlock()
    }

    func record(now: Date = Date()) -> String? {
        lock.lock()
        defer { lock.unlock() }
        guard let value = latest else { return nil }
        let elapsed = now.timeIntervalSince(value.at)
        guard elapsed >= 0, elapsed <= 10 else { return nil }
        latest = nil
        return "wake label: MISS id=\(value.id) \(value.origin.fields)"
    }
}

/// Maps Mac end-of-dictation prose reasons to Windows FinishReason names for the log.
func finishReasonToken(_ reason: String) -> String {
    switch reason {
    case "hotkey", "FinishCommand": return "FinishCommand"
    case "maxSeconds", "MaximumDuration": return "MaximumDuration"
    case "stop word", "StandaloneStop": return "StandaloneStop"
    case "Silence": return "Silence"
    default:
        if reason.contains("silence") { return "Silence" }
        return reason.replacingOccurrences(of: " ", with: "_")
    }
}

struct AutomaticTranscriptPolicy: Equatable {
    /// Whole transcripts an automatic recording is dropped for, after `token` trims it.
    var ignored: Set<String>

    static let disabled = AutomaticTranscriptPolicy(ignored: [])
    static let filterInterjections = AutomaticTranscriptPolicy(ignored: ["あっ", "ああ"])

    init(ignored: Set<String>) { self.ignored = ignored }

    init(_ options: NoWakeWordConfig?) {
        let listed = Set((options?.ignoredTranscripts ?? []).map(Self.token))
        ignored = options?.filterInterjections == true ? listed.union(Self.filterInterjections.ignored) : listed
    }

    /// Outer whitespace and trailing 。.!！?？ only; anything inside the text means it said more than the word.
    static func token(_ text: String) -> String {
        var candidate = text.unicodeScalars.drop(while: { $0.properties.isWhitespace })
        while let last = candidate.last, last.properties.isWhitespace || "。.!！?？".unicodeScalars.contains(last) {
            candidate.removeLast()
        }
        return String(String.UnicodeScalarView(candidate))
    }

    func rejects(_ text: String, origin: WakeOrigin) -> Bool {
        origin.isAutomatic && !ignored.isEmpty && ignored.contains(Self.token(text))
    }
}

struct DictationHistoryEntry {
    let id: UUID
    let text: String
    let origin: WakeOrigin
    let createdAt: Date
}

final class DictationHistory {
    static let shared = DictationHistory()
    private let lock = NSLock()
    private var entries: [DictationHistoryEntry] = []
    private var currentGeneration = 0
    var generation: Int { lock.lock(); defer { lock.unlock() }; return currentGeneration }
    private let now: () -> Date
    private let capacity: Int
    private let retention: TimeInterval
    var onChange: (() -> Void)?

    init(now: @escaping () -> Date = Date.init, capacity: Int = 50, retention: TimeInterval = 1800) {
        precondition(capacity > 0 && retention > 0)
        self.now = now
        self.capacity = capacity
        self.retention = retention
    }

    var journal: DictationJournal?
    @discardableResult
    func add(id: UUID, text: String, origin: WakeOrigin, expectedGeneration: Int? = nil, policy: AutomaticTranscriptPolicy = .disabled, endReason: String? = nil, recordingSeconds: Double? = nil, manual: Bool = false) -> Bool {
        guard text.utf16.count <= 100_000, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !policy.rejects(text, origin: origin) else { return false }
        lock.lock()
        prune()
        guard expectedGeneration == nil || expectedGeneration == currentGeneration else { lock.unlock(); return false }
        guard !entries.contains(where: { $0.id == id }) else { lock.unlock(); return false }
        let created = now()
        entries.append(DictationHistoryEntry(id: id, text: text, origin: origin, createdAt: created))
        if entries.count > capacity { entries.removeFirst(entries.count - capacity) }
        lock.unlock()
        let noted = DictationJournal.take(id)
        let sink = journal ?? (self === DictationHistory.shared ? DictationJournal.active : nil)
        sink?.record(id: id, at: created, text: text, origin: origin, endReason: endReason ?? noted.endReason, recordingSeconds: recordingSeconds ?? noted.recordingSeconds, manual: manual || noted.manual)
        onChange?()
        return true
    }

    func snapshot() -> [DictationHistoryEntry] {
        lock.lock()
        defer { lock.unlock() }
        prune()
        return entries.reversed()
    }

    func select(_ id: UUID) -> String? {
        lock.lock()
        defer { lock.unlock() }
        prune()
        return entries.first(where: { $0.id == id })?.text
    }

    func clear() {
        lock.lock()
        entries.removeAll()
        currentGeneration += 1
        lock.unlock()
        onChange?()
    }

    private func prune() {
        let instant = now()
        entries.removeAll { instant < $0.createdAt || instant.timeIntervalSince($0.createdAt) >= retention }
    }
}

struct PasteSafety {
    let targetFocused: Bool
    let plainTextField: Bool
    let compositionClear: Bool
    let clipboardRestorable: Bool
    let inputUnchanged: Bool
    var allowsPaste: Bool { targetFocused && plainTextField && compositionClear && clipboardRestorable && inputUnchanged }
}

struct NoWakeWordConfig: Decodable {
    var followup: Bool?
    var autoIdle: Bool?
    var relaxedReadingDistance2: Bool?
    var followupMs: Int?
    var filterInterjections: Bool?
    /// Transcripts that are never kept from an automatic recording when they are all it said, e.g. "うん".
    var ignoredTranscripts: [String]?
    /// Automatic recordings shorter than this are a cough or a click, not speech; they never reach Superwhisper (Windows MinHandoffMs).
    var minHandoffMs: Int?
    /// Silence that ends an automatic recording; `dictation.endSilenceMs` when absent. Wake-word dictation keeps
    /// `dictation.endSilenceMs`, so it can wait longer for someone thinking mid-sentence.
    var endSilenceMs: Int?
    var enabled: Bool { followup == true || autoIdle == true || relaxedReadingDistance2 == true }
    var valid: Bool { (1000...30000).contains(followupMs ?? 8000) }
}

enum NoWakePolicy {
    /// Fewest characters Apple Speech and Whisper must share before an automatic recording reaches Superwhisper.
    /// Noise and coughs make each recognizer invent its own words (Whisper "ご視聴ありがとうございました", Apple nothing),
    /// so they share none; spoken words come out the same from both. Measured 2026-10-14: coughs 0, speech 12 or more.
    static let minAgreedCharacters = 3

    /// Length of the longest common subsequence of the two transcripts after `normalize` and width folding (３ and 3 match).
    static func agreedCharacters(_ a: String, _ b: String) -> Int {
        let x = Array(normalize(a.precomposedStringWithCompatibilityMapping))
        let y = Array(normalize(b.precomposedStringWithCompatibilityMapping))
        var row = [Int](repeating: 0, count: y.count + 1)
        for c in x {
            var diagonal = 0
            for (j, d) in y.enumerated() {
                let above = row[j + 1]
                row[j + 1] = c == d ? diagonal + 1 : max(above, row[j])
                diagonal = above
            }
        }
        return row[y.count]
    }

    /// Whisper text with known filler phrases removed, so the rest still counts toward agreement.
    /// Observed on box whisper.cpp v1.7.5 (app uses b5454): ご視聴ありがとうございました, 音楽. The rest are known whisper fillers, not observed here.
    static func whisperTextForAgreement(_ text: String) -> String {
        var t = text
        for tag in ["(音楽)", "（音楽）", "[音楽]", "【音楽】", "(字幕)", "（字幕）", "[字幕]", "【字幕】", "♪"] {
            t = t.replacingOccurrences(of: tag, with: "")
        }
        let punctuation = Set("。、！!？?.")
        let bare = t.filter { !$0.isWhitespace && !punctuation.contains($0) }
        if ["ご視聴ありがとうございました", "ご視聴ありがとうございます", "音楽", "字幕"].contains(bare) { return "" }
        for tail in ["ご視聴ありがとうございました", "ご視聴ありがとうございます"] {
            var trimmed = t
            while let last = trimmed.last, last.isWhitespace || punctuation.contains(last) { trimmed.removeLast() }
            if trimmed.hasSuffix(tail) { return String(trimmed.dropLast(tail.count)) }
        }
        return t
    }

    /// Characters Apple Speech and Whisper share, after filler phrases are removed from the Whisper side.
    static func agreedWithWhisper(apple: String, whisper: String) -> Int {
        agreedCharacters(apple, whisperTextForAgreement(whisper))
    }

    static func belowMinHandoff(sampleCount: Int, rate: Double, options: NoWakeWordConfig?) -> Bool {
        sampleCount < Int(rate) * (options?.minHandoffMs ?? 1000) / 1000
    }

    static func onSpeech(_ options: NoWakeWordConfig?, idleSeconds: Double?, inFollowupWindow: Bool) -> WakeOrigin? {
        guard let options, options.valid, let idleSeconds, idleSeconds.isFinite, idleSeconds >= 0 else { return nil }
        if options.followup == true && inFollowupWindow { return WakeOrigin(rule: .followup, inputIdleSeconds: idleSeconds) }
        if options.autoIdle == true { return WakeOrigin(rule: .autoB, inputIdleSeconds: idleSeconds) }
        return nil
    }

    static func relaxedWake(_ text: String, readings: [String]) -> Bool {
        let heard = normalizedReading(text)
        func kanaOnly(_ value: String) -> Bool {
            !value.isEmpty && value.unicodeScalars.allSatisfy { (0x3041...0x3096).contains($0.value) || $0.value == 0x30FC }
        }
        guard kanaOnly(heard) else { return false }
        return readings.map(normalizedReading).contains { (5...9).contains($0.count) && kanaOnly($0) && editDistance(heard, $0) == 2 }
    }

    static func normalizedReading(_ text: String) -> String {
        normalize(text.applyingTransform(.hiraganaToKatakana, reverse: true) ?? text)
    }

    private static func editDistance(_ lhs: String, _ rhs: String) -> Int {
        let a = Array(lhs), b = Array(rhs)
        var previous = Array(0...b.count)
        for (i, character) in a.enumerated() {
            var current = [i + 1] + Array(repeating: 0, count: b.count)
            for (j, other) in b.enumerated() {
                current[j + 1] = min(current[j] + 1, previous[j + 1] + 1, previous[j] + (character == other ? 0 : 1))
            }
            previous = current
        }
        return previous[b.count]
    }
}

struct ModeRestore: Codable {
    let previous: String
    let temporary: String
    var valid: Bool { !previous.isEmpty && !temporary.isEmpty && previous != temporary }
    func decide(_ active: String?) -> ModeRecovery {
        guard valid, let active else { return .unknown }
        return active == temporary ? .restore : .clear
    }
}
enum ModeRecovery { case unknown, restore, clear }
