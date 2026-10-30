// Append-only JSONL of utterances DictationHistory accepted.
// A bad path is logged and the platform default directory is used. Text only.
import Foundation

struct JournalNote {
    var endReason: String?
    var recordingSeconds: Double?
    var manual: Bool
    var focus: FocusApp?
}

/// The app in front when the dictation session target was chosen, not the app in front at append time.
/// Mac fills `name` from `localizedName` and `bundleId` from `bundleIdentifier`. Windows fills
/// `name` and `exe` from `Process.ProcessName` and leaves `bundleId` null.
struct FocusApp {
    var name: String?
    var bundleId: String?
    var exe: String?
    var pid: Int?
    var isEmpty: Bool { name == nil && bundleId == nil && exe == nil && (pid == nil || pid! <= 0) }
}

final class DictationJournal {
    static let schemaVersion = 1
    static let taskFileCap = 4096
    private static let notesLock = NSLock()
    private static var notes: [UUID: JournalNote] = [:]
    static var active: DictationJournal?

    private let lock = NSLock()
    private let log: (String) -> Void
    private let fallback: String
    private let journalTaskFile: String?
    private var target: Target

    private struct Target {
        var path: String
        var single: Bool
    }

    private init(target: Target, fallback: String, journalTaskFile: String?, log: @escaping (String) -> Void) {
        self.target = target
        self.fallback = fallback
        self.journalTaskFile = journalTaskFile
        self.log = log
    }

    static func bind(_ configured: String?, journalTaskFile: String? = nil) {
        active = open(configured, journalTaskFile: journalTaskFile) { log($0) }
    }

    static func defaultDirectory() -> String {
        #if os(Windows)
        return Platform.expandPath(#"%LOCALAPPDATA%\voice-switch\journal"#)
        #else
        return Platform.expandPath("~/Library/Application Support/voice-switch/journal")
        #endif
    }

    static func originLabel(_ origin: WakeOrigin, manual: Bool) -> String {
        if manual { return "手動" }
        return origin.isAutomatic ? "自動" : "ウェイク"
    }

    static func note(_ id: UUID, endReason: String? = nil, recordingSeconds: Double? = nil, manual: Bool? = nil, focus: FocusApp? = nil) {
        notesLock.lock()
        defer { notesLock.unlock() }
        var existing = notes[id] ?? JournalNote(endReason: nil, recordingSeconds: nil, manual: false)
        if let endReason { existing.endReason = endReason }
        if let recordingSeconds { existing.recordingSeconds = recordingSeconds }
        if let manual { existing.manual = manual }
        if let focus { existing.focus = focus }
        notes[id] = existing
    }

    static func take(_ id: UUID) -> JournalNote {
        notesLock.lock()
        defer { notesLock.unlock() }
        if let note = notes.removeValue(forKey: id) { return note }
        return JournalNote(endReason: nil, recordingSeconds: nil, manual: false)
    }

    static func open(_ configured: String?, fallback explicit: String? = nil, journalTaskFile: String? = nil, log: @escaping (String) -> Void) -> DictationJournal {
        let fallback = (explicit?.trimmingCharacters(in: .whitespacesAndNewlines)).flatMap { $0.isEmpty ? nil : $0 } ?? defaultDirectory()
        if configured == nil {
            return DictationJournal(target: Target(path: fallback, single: false), fallback: fallback, journalTaskFile: journalTaskFile, log: log)
        }
        let raw = configured ?? ""
        if raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            log("dictation journal: path unusable (empty); using default \(fallback)")
            return DictationJournal(target: Target(path: fallback, single: false), fallback: fallback, journalTaskFile: journalTaskFile, log: log)
        }
        let expanded = Platform.expandPath(raw.trimmingCharacters(in: .whitespacesAndNewlines))
        if let problem = problem(expanded) {
            log("dictation journal: path unusable (\(problem)); using default \(fallback)")
            return DictationJournal(target: Target(path: fallback, single: false), fallback: fallback, journalTaskFile: journalTaskFile, log: log)
        }
        let single = expanded.lowercased().hasSuffix(".jsonl")
        return DictationJournal(target: Target(path: expanded, single: single), fallback: fallback, journalTaskFile: journalTaskFile, log: log)
    }

    func record(id: UUID, at: Date, text: String, origin: WakeOrigin, endReason: String?, recordingSeconds: Double?, manual: Bool, focus: FocusApp? = nil) {
        let stamped = Self.line(id: id, at: at, text: text, origin: Self.originLabel(origin, manual: manual), endReason: endReason, recordingSeconds: recordingSeconds, focus: focus, journalTaskFile: journalTaskFile)
        lock.lock()
        defer { lock.unlock() }
        if write(target, line: stamped.text, day: stamped.day) { return }
        if target.path == fallback {
            return
        }
        let next = Target(path: fallback, single: false)
        if write(next, line: stamped.text, day: stamped.day) {
            target = next
        }
    }

    @discardableResult
    func deleteDay(_ day: String) -> Int {
        guard day.count == 10 else { return 0 }
        lock.lock()
        defer { lock.unlock() }
        if target.single {
            return filter(target.path) { Self.dayOf($0) == day } ? 1 : 0
        }
        let path = (target.path as NSString).appendingPathComponent(day + ".jsonl")
        let fm = FileManager.default
        if !fm.fileExists(atPath: path) || Self.isSymlink(path) { return 0 }
        do { try fm.removeItem(atPath: path) } catch { return 0 }
        return 1
    }

    @discardableResult
    func deleteEntry(_ id: UUID) -> Bool {
        let token = "\"id\":\"\(id.uuidString.lowercased())\""
        lock.lock()
        defer { lock.unlock() }
        if target.single { return filter(target.path) { $0.contains(token) } }
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: target.path) else { return false }
        var removed = false
        for name in names where name.hasSuffix(".jsonl") {
            let path = (target.path as NSString).appendingPathComponent(name)
            removed = filter(path) { $0.contains(token) } || removed
        }
        return removed
    }

    private func write(_ place: Target, line: String, day: String) -> Bool {
        let fm = FileManager.default
        if Self.isSymlink(place.path) || Self.ancestorIsFile(place.path) {
            noteFailure(place, JournalWriteError("not writable"))
            return false
        }
        let file: String
        do {
            if place.single {
                var parent = (place.path as NSString).deletingLastPathComponent
                if parent.isEmpty { parent = "." }
                let createdParent = !fm.fileExists(atPath: parent)
                try fm.createDirectory(atPath: parent, withIntermediateDirectories: true)
                if createdParent { try Self.protectDirectory(parent) }
                file = place.path
            } else {
                try fm.createDirectory(atPath: place.path, withIntermediateDirectories: true)
                try Self.protectDirectory(place.path)
                file = (place.path as NSString).appendingPathComponent(day + ".jsonl")
            }
            if Self.isSymlink(file) {
                noteFailure(place, JournalWriteError("symlink"))
                return false
            }
            try Self.appendLine(file, line)
            return true
        } catch {
            noteFailure(place, error)
            return false
        }
    }

    private func noteFailure(_ place: Target, _ error: Error) {
        if place.path == fallback {
            log("dictation journal: default unwritable (\(error))")
        } else {
            log("dictation journal: \(error); using default \(fallback)")
        }
    }

    private static func appendLine(_ file: String, _ line: String) throws {
        let fm = FileManager.default
        if !fm.fileExists(atPath: file) {
            guard fm.createFile(atPath: file, contents: nil, attributes: [.posixPermissions: NSNumber(value: 0o600)]) else {
                throw JournalWriteError("create failed")
            }
        }
        let handle = try FileHandle(forWritingTo: URL(fileURLWithPath: file))
        defer { try? handle.close() }
        let end = try handle.seekToEnd()
        if end > 0 {
            try handle.seek(toOffset: end - 1)
            let last = try handle.read(upToCount: 1)
            try handle.seekToEnd()
            if last?.first != 10 { try handle.write(contentsOf: Data([10])) }
        }
        var payload = Data(line.utf8)
        payload.append(10)
        try handle.write(contentsOf: payload)
        try handle.synchronize()
        try protectFile(file)
    }

    // Copies raw lines. A line that is not removed is not parsed and not reformatted.
    private func filter(_ path: String, remove: (String) -> Bool) -> Bool {
        let fm = FileManager.default
        if !fm.fileExists(atPath: path) || Self.isSymlink(path) { return false }
        guard let raw = fm.contents(atPath: path), let text = String(data: raw, encoding: .utf8) else { return false }
        let pieces = text.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        var keep: [String] = []
        var removed = false
        for (index, piece) in pieces.enumerated() {
            let last = index == pieces.count - 1
            if piece.isEmpty && last { continue }
            if remove(piece) {
                removed = true
                continue
            }
            keep.append(piece)
        }
        if !removed { return false }
        if keep.isEmpty {
            try? fm.removeItem(atPath: path)
            return true
        }
        let next = Data((keep.joined(separator: "\n") + "\n").utf8)
        let temp = path + ".tmp"
        do {
            try next.write(to: URL(fileURLWithPath: temp))
            try Self.protectFile(temp)
            if fm.fileExists(atPath: path) { try fm.removeItem(atPath: path) }
            try fm.moveItem(atPath: temp, toPath: path)
            try Self.protectFile(path)
        } catch {
            return false
        }
        return true
    }

    private static func line(id: UUID, at: Date, text: String, origin: String, endReason: String?, recordingSeconds: Double?, focus: FocusApp?, journalTaskFile: String?) -> (text: String, day: String) {
        let timestamp = stamp(at)
        let day = String(timestamp.prefix(10))
        let seconds = recordingSeconds.map(jsonNumber) ?? "null"
        let reason = endReason.map { jsonString($0) } ?? "null"
        let body = [
            "\"id\":\(jsonString(id.uuidString.lowercased()))",
            "\"timestamp\":\(jsonString(timestamp))",
            "\"text\":\(jsonString(text))",
            "\"origin\":\(jsonString(origin))",
            "\"endReason\":\(reason)",
            "\"recordingSeconds\":\(seconds)",
            "\"schemaVersion\":\(schemaVersion)",
            "\"focusApp\":\(focusJson(focus))",
            "\"taskchute\":\(taskJson(journalTaskFile: journalTaskFile, timestamp: timestamp))",
        ].joined(separator: ",")
        return ("{\(body)}", day)
    }

    private static func focusJson(_ focus: FocusApp?) -> String {
        guard let focus, !focus.isEmpty else { return "null" }
        let pid = (focus.pid ?? 0) <= 0 ? "null" : String(focus.pid!)
        return "{\"name\":\(focus.name.map(jsonString) ?? "null"),\"bundleId\":\(focus.bundleId.map(jsonString) ?? "null"),\"exe\":\(focus.exe.map(jsonString) ?? "null"),\"pid\":\(pid)}"
    }

    private static func taskJson(journalTaskFile: String?, timestamp: String) -> String {
        guard let task = readTask(journalTaskFile) else { return "null" }
        return "{\"name\":\(task.name.map(jsonString) ?? "null"),\"id\":\(task.id.map(jsonString) ?? "null"),\"startedAt\":\(task.startedAt.map(jsonString) ?? "null"),\"source\":\"file\",\"readAt\":\(jsonString(timestamp))}"
    }

    // The task file is an external updater's snapshot, read at append time. Anything unusable is nil, never thrown.
    private static func readTask(_ raw: String?) -> (name: String?, id: String?, startedAt: String?)? {
        guard let trimmed = raw?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty else { return nil }
        let expanded = Platform.expandPath(trimmed)
        if expanded.range(of: "%[A-Za-z0-9_]+%", options: .regularExpression) != nil { return nil }
        guard let data = FileManager.default.contents(atPath: expanded), data.count <= taskFileCap else { return nil }
        var payload = data
        if payload.count >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF {
            payload = payload.subdata(in: 3..<payload.count)
        }
        guard let text = String(data: payload, encoding: .utf8) else { return nil }
        var first: Character?
        for scalar in text.unicodeScalars {
            if CharacterSet.whitespacesAndNewlines.contains(scalar) { continue }
            first = Character(scalar)
            break
        }
        guard let head = first else { return nil }
        if head == "{" { return parseTaskJson(payload) }
        for piece in text.split(separator: "\n", omittingEmptySubsequences: false) {
            let name = piece.trimmingCharacters(in: .whitespacesAndNewlines)
            if !name.isEmpty { return (name, nil, nil) }
        }
        return nil
    }

    private static func parseTaskJson(_ payload: Data) -> (name: String?, id: String?, startedAt: String?)? {
        guard let obj = try? JSONSerialization.jsonObject(with: payload, options: []),
              let dict = obj as? [String: Any] else { return nil }
        var name: String?
        var id: String?
        var startedAt: String?
        if let value = dict["name"] as? String, !value.isEmpty { name = value }
        if let value = dict["id"] as? String, !value.isEmpty { id = value }
        else if dict["id"] is Bool { id = nil }
        else if let number = dict["id"] as? NSNumber { id = number.stringValue }
        if let value = dict["startedAt"] as? String { startedAt = value }
        guard name != nil || id != nil else { return nil }
        return (name, id, startedAt)
    }

    private static func stamp(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.timeZone = TimeZone.current
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }

    private static func dayOf(_ line: String) -> String? {
        let key = "\"timestamp\":\""
        guard let range = line.range(of: key) else { return nil }
        let rest = line[range.upperBound...]
        guard rest.count >= 10 else { return nil }
        return String(rest.prefix(10))
    }

    private static func jsonString(_ value: String) -> String {
        var out = "\""
        for scalar in value.unicodeScalars {
            switch scalar.value {
            case 0x22: out += "\\\""
            case 0x5C: out += "\\\\"
            case 0x08: out += "\\b"
            case 0x0C: out += "\\f"
            case 0x0A: out += "\\n"
            case 0x0D: out += "\\r"
            case 0x09: out += "\\t"
            default:
                if scalar.value < 0x20 {
                    out += String(format: "\\u%04x", scalar.value)
                } else {
                    out.unicodeScalars.append(scalar)
                }
            }
        }
        out += "\""
        return out
    }

    private static func jsonNumber(_ value: Double) -> String {
        if !value.isFinite { return "null" }
        return String(format: "%.6g", locale: Locale(identifier: "en_US_POSIX"), value)
    }

    private static func problem(_ path: String) -> String? {
        if path.unicodeScalars.contains(where: { $0.value == 0 }) { return "invalid character" }
        if path.range(of: "%[A-Za-z0-9_]+%", options: .regularExpression) != nil { return "unexpanded variable" }
        if isSymlink(path) { return "symlink" }
        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: path, isDirectory: &isDir) {
            if isDir.boolValue && path.lowercased().hasSuffix(".jsonl") { return "path is a directory" }
            if !isDir.boolValue && !path.lowercased().hasSuffix(".jsonl") { return "path is a file" }
        }
        if ancestorIsFile(path) { return "not writable" }
        return nil
    }

    private static func ancestorIsFile(_ path: String) -> Bool {
        var parent = (path as NSString).deletingLastPathComponent
        var hops = 0
        while !parent.isEmpty && parent != "/" && parent != "." && hops < 64 {
            hops += 1
            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: parent, isDirectory: &isDir) && !isDir.boolValue {
                return true
            }
            let next = (parent as NSString).deletingLastPathComponent
            if next == parent { break }
            parent = next
        }
        return false
    }

    private static func isSymlink(_ path: String) -> Bool {
        (try? URL(fileURLWithPath: path).resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true
    }

    private static func protectDirectory(_ path: String) throws {
        try FileManager.default.setAttributes([.posixPermissions: NSNumber(value: 0o700)], ofItemAtPath: path)
    }

    private static func protectFile(_ path: String) throws {
        try FileManager.default.setAttributes([.posixPermissions: NSNumber(value: 0o600)], ofItemAtPath: path)
    }
}

#if canImport(AppKit)
import AppKit

extension DictationJournal {
    /// The session-start app for the journal: the frontmost app stored on `Dictation.target` when the
    /// session began. The later frontmost app read for paste is not passed here.
    static func focus(of app: NSRunningApplication) -> FocusApp {
        FocusApp(name: app.localizedName, bundleId: app.bundleIdentifier, exe: nil, pid: Int(app.processIdentifier))
    }
}
#endif

private struct JournalWriteError: Error, CustomStringConvertible {
    let description: String
    init(_ description: String) { self.description = description }
}
