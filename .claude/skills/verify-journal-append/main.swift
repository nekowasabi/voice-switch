// Bounded-exhaustive check of the macOS journal append path, run against the real
// Sources/voice-switch/DictationJournal.swift. See SKILL.md for the invariants J1-J4.
import Foundation

let args = CommandLine.arguments
let root = args[1]
let maxLen = args.count > 2 ? (Int(args[2]) ?? 4) : 4
let fm = FileManager.default
let origin = WakeOrigin(rule: .strict, inputIdleSeconds: nil)
let dayA = ISO8601DateFormatter().date(from: "2026-11-20T03:00:00Z")!
let dayB = dayA.addingTimeInterval(2 * 86400)
// R: record on day A. S: record on day B. F: a crash leaves a cut line at the end of the last written file.
// B: the configured location breaks (its parent becomes a file). U: it is repaired. D: deleteEntry(oldest live id).
let alphabet: [Character] = ["R", "S", "F", "B", "U", "D"]
// Each record takes the next delivery in turn, so every value and null reach disk in every order (J3 reads it back).
let deliveries: [JournalDelivery?] = [.paste, .sticky, .pane, .action, .history, nil]

struct Failure: Error { let id: String; let message: String }

func scan(_ dirs: [(real: String, logical: String)]) -> [String: Data] {
    var out: [String: Data] = [:]
    for (real, logical) in dirs {
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: real, isDirectory: &isDir), isDir.boolValue else { continue }
        let walker = fm.enumerator(atPath: real)
        while let rel = walker?.nextObject() as? String {
            guard rel.hasSuffix(".jsonl") else { continue }
            if let data = fm.contents(atPath: real + "/" + rel) { out[logical + "/" + rel] = data }
        }
    }
    return out
}

func runOne(_ seq: [Character], single: Bool, base: String) throws {
    try? fm.removeItem(atPath: base)
    let cfgRoot = base + "/cfg"
    let away = base + "/cfg.away"
    let fallback = base + "/fb"
    try fm.createDirectory(atPath: cfgRoot, withIntermediateDirectories: true)
    let configured = single ? cfgRoot + "/j.jsonl" : cfgRoot + "/journal"
    var logs: [String] = []
    let journal = DictationJournal.open(configured, fallback: fallback) { logs.append($0) }
    var alive: [(id: UUID, text: String, delivery: String?)] = []
    var fragments: Set<String> = []
    var lastFile: String? = nil
    var broken = false
    var n = 0
    func dirs() -> [(real: String, logical: String)] {
        [(broken ? away : cfgRoot, cfgRoot), (fallback, fallback)]
    }
    func fail(_ id: String, _ step: Int, _ msg: String) -> Failure {
        Failure(id: id, message: "mode=\(single ? "single" : "dir") seq=\(String(seq)) step=\(step + 1)(\(seq[step])): \(msg)")
    }
    defer {
        if broken { try? fm.removeItem(atPath: cfgRoot); try? fm.moveItem(atPath: away, toPath: cfgRoot) }
        try? fm.removeItem(atPath: base)
    }
    for (step, op) in seq.enumerated() {
        let before = scan(dirs())
        n += 1
        switch op {
        case "R", "S":
            let id = UUID()
            let text = "発話\(n) \"q\"\n改行"
            let at = (op == "R" ? dayA : dayB).addingTimeInterval(Double(n))
            let delivery = deliveries[n % deliveries.count]
            journal.record(id: id, at: at, text: text, origin: origin, endReason: "Silence", recordingSeconds: 1, manual: false, delivery: delivery)
            alive.append((id, text, delivery?.rawValue))
            let token = "\"id\":\"\(id.uuidString.lowercased())\""
            lastFile = scan(dirs()).first { String(data: $0.value, encoding: .utf8)?.contains(token) == true }?.key
            if let lf = lastFile, lf.hasPrefix(fallback), !logs.contains(where: { $0.contains("using default") }) {
                throw fail("J4", step, "record went to the fallback with no 'using default' log line")
            }
        case "F":
            if let lf = lastFile {
                let real = broken && lf.hasPrefix(cfgRoot) ? away + lf.dropFirst(cfgRoot.count) : lf
                let frag = "{\"id\":\"frag-\(n)\",\"text\":\"cut"
                // A cut write in the real appendLine still did its newline repair first (DictationJournal.swift appendLine).
                let repair = fm.contents(atPath: real).map { !$0.isEmpty && $0.last != 10 } ?? false
                if let h = FileHandle(forWritingAtPath: real) {
                    h.seekToEndOfFile(); h.write(Data(((repair ? "\n" : "") + frag).utf8)); h.closeFile()
                    fragments.insert(frag)
                }
            }
        case "B":
            if !broken { try fm.moveItem(atPath: cfgRoot, toPath: away); fm.createFile(atPath: cfgRoot, contents: Data("x".utf8)); broken = true }
        case "U":
            if broken { try fm.removeItem(atPath: cfgRoot); try fm.moveItem(atPath: away, toPath: cfgRoot); broken = false }
        case "D":
            if let first = alive.first, journal.deleteEntry(first.id) { alive.removeFirst() }
        default: break
        }
        let after = scan(dirs())
        // J2: bytes already on disk stay a prefix, except where deleteEntry rewrote the file.
        if op != "D" {
            for (path, old) in before {
                guard let new = after[path] else { throw fail("J2", step, "\(path.dropFirst(base.count)) disappeared") }
                if !new.starts(with: old) { throw fail("J2", step, "\(path.dropFirst(base.count)) lost or changed earlier bytes") }
            }
        }
        // J1 + J3: every live record is on disk exactly once with its text and delivery; every other piece is an injected fragment.
        var counts: [String: Int] = [:]
        for (path, data) in after {
            guard let text = String(data: data, encoding: .utf8) else { throw fail("J3", step, "\(path) is not UTF-8") }
            let pieces = text.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
            for (i, piece) in pieces.enumerated() {
                if piece.isEmpty && i == pieces.count - 1 { continue }
                if fragments.contains(piece) { continue }
                guard let obj = try? JSONSerialization.jsonObject(with: Data(piece.utf8)) as? [String: Any],
                      let id = obj["id"] as? String else {
                    throw fail("J3", step, "\(path.dropFirst(base.count)) line \(i + 1) is neither a whole record nor a cut fragment: \(piece.prefix(60))")
                }
                counts[id, default: 0] += 1
                if let want = alive.first(where: { $0.id.uuidString.lowercased() == id }) {
                    if obj["text"] as? String != want.text { throw fail("J3", step, "record \(id.prefix(8)) text changed") }
                    let stored = obj["delivery"]
                    let same = want.delivery.map { stored as? String == $0 } ?? (stored is NSNull)
                    if !same { throw fail("J3", step, "record \(id.prefix(8)) delivery is \(stored.map { "\($0)" } ?? "absent") (want \(want.delivery ?? "null"))") }
                }
            }
        }
        for record in alive {
            let c = counts[record.id.uuidString.lowercased()] ?? 0
            if c != 1 { throw fail("J1", step, "record \(record.id.uuidString.prefix(8)) is on disk \(c) times (want 1)") }
        }
    }
}

var total = 0
var failed: [String: String] = [:]
for single in [false, true] {
    var idx = [Int](repeating: 0, count: maxLen)
    while true {
        let seq = idx.map { alphabet[$0] }
        total += 1
        do { try runOne(seq, single: single, base: root + "/case") }
        catch let f as Failure { if failed[f.id] == nil { failed[f.id] = f.message } }
        catch { if failed["HARNESS"] == nil { failed["HARNESS"] = "\(error)" } }
        var k = maxLen - 1
        while k >= 0 { idx[k] += 1; if idx[k] < alphabet.count { break }; idx[k] = 0; k -= 1 }
        if k < 0 { break }
    }
}
print("sequences \(total) (length \(maxLen), 2 modes)")
for id in ["J1", "J2", "J3", "J4", "HARNESS"] {
    if let m = failed[id] { print("FAIL \(id) \(m)") } else if id != "HARNESS" { print("PASS \(id)") }
}
exit(failed.isEmpty ? 0 : 1)
