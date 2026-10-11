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

    @discardableResult
    func add(id: UUID, text: String, origin: WakeOrigin) -> Bool {
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return false }
        lock.lock()
        prune()
        guard !entries.contains(where: { $0.id == id }) else { lock.unlock(); return false }
        entries.append(DictationHistoryEntry(id: id, text: text, origin: origin, createdAt: now()))
        if entries.count > capacity { entries.removeFirst(entries.count - capacity) }
        lock.unlock()
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
        lock.unlock()
        onChange?()
    }

    private func prune() { entries.removeAll { now().timeIntervalSince($0.createdAt) >= retention } }
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
    var idleMs: Int?
    var enabled: Bool { followup == true || autoIdle == true || relaxedReadingDistance2 == true }
    var valid: Bool { (1000...30000).contains(followupMs ?? 8000) && (1000...60000).contains(idleMs ?? 3000) }
}

enum NoWakePolicy {
    static func onSpeech(_ options: NoWakeWordConfig?, idleSeconds: Double?, inFollowupWindow: Bool) -> WakeOrigin? {
        guard let options, options.valid, let idleSeconds, idleSeconds.isFinite, idleSeconds >= 0 else { return nil }
        if options.followup == true && inFollowupWindow { return WakeOrigin(rule: .followup, inputIdleSeconds: idleSeconds) }
        if options.autoIdle == true && idleSeconds * 1000 >= Double(options.idleMs ?? 3000) {
            return WakeOrigin(rule: .autoB, inputIdleSeconds: idleSeconds)
        }
        return nil
    }

    static func relaxedWake(_ text: String, readings: [String]) -> Bool {
        let heard = normalizedReading(text)
        return readings.map(normalizedReading).contains { (5...9).contains($0.count) && editDistance(heard, $0) == 2 }
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
