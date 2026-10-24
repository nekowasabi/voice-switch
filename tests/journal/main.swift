import Foundation

let repo = CommandLine.arguments[1]
let scratch = CommandLine.arguments[2]
try FileManager.default.createDirectory(atPath: scratch, withIntermediateDirectories: true)
let origin = WakeOrigin(rule: .strict, inputIdleSeconds: nil)
var failed = 0

func pass(_ id: String) { print("PASS \(id)") }
func fail(_ id: String, _ message: String) {
    failed += 1
    print("FAIL \(id) \(message)")
}

func mode(_ path: String) -> Int {
    guard let attrs = try? FileManager.default.attributesOfItem(atPath: path),
          let number = attrs[.posixPermissions] as? NSNumber else { return 0 }
    return number.intValue & 0o777
}

func record(_ journal: DictationJournal, _ text: String, id: UUID = UUID(), at: Date = Date()) {
    journal.record(id: id, at: at, text: text, origin: origin, endReason: "Silence", recordingSeconds: 1, manual: false)
}

let tilde = Platform.expandPath("~/Documents/voice-switch/journal")
let apple = NSString(string: "~/Documents/voice-switch/journal").expandingTildeInPath
if tilde == apple && tilde.hasSuffix("Documents/voice-switch/journal") { pass("expand-tilde") }
else { fail("expand-tilde", tilde) }

let platform = (try? String(contentsOfFile: repo + "/Sources/voice-switch/Platform.swift", encoding: .utf8)) ?? ""
if platform.contains("LOCALAPPDATA") && platform.contains("%\\(key)%") { pass("expand-localappdata") }
else { fail("expand-localappdata", "Windows %VAR% replacement missing") }

let appendDir = scratch + "/append"
var appendLogs: [String] = []
let appendJournal = DictationJournal.open(appendDir, fallback: scratch + "/append-fb") { appendLogs.append($0) }
let firstId = UUID()
record(appendJournal, "first", id: firstId)
let dayFormatter = ISO8601DateFormatter()
dayFormatter.timeZone = TimeZone.current
dayFormatter.formatOptions = [.withFullDate]
let day = dayFormatter.string(from: Date())
let appendFile = appendDir + "/" + day + ".jsonl"
let original = (try? Data(contentsOf: URL(fileURLWithPath: appendFile))) ?? Data()
record(appendJournal, "second")
let grown = (try? Data(contentsOf: URL(fileURLWithPath: appendFile))) ?? Data()
let prefixOK = grown.starts(with: original) && grown.count > original.count
let crashed = original + Data("PARTIAL".utf8)
try? crashed.write(to: URL(fileURLWithPath: appendFile))
record(appendJournal, "third")
let after = (try? Data(contentsOf: URL(fileURLWithPath: appendFile))) ?? Data()
let survived = after.starts(with: crashed) && String(data: after, encoding: .utf8)?.split(separator: "\n").first?.contains(firstId.uuidString.lowercased()) == true
if prefixOK && survived { pass("append-only") } else { fail("append-only", "prefix or fragment changed") }

var invalidLogs: [String] = []
let invalidFallback = scratch + "/invalid-fb"
let invalid = DictationJournal.open("", fallback: invalidFallback) { invalidLogs.append($0) }
record(invalid, "bad")
let invalidFile = invalidFallback + "/" + day + ".jsonl"
if FileManager.default.fileExists(atPath: invalidFile)
    && invalidLogs.contains(where: { $0.contains("dictation journal:") && $0.contains("using default") }) {
    pass("invalid-fallback")
} else { fail("invalid-fallback", invalidLogs.joined(separator: " | ")) }

let blockedParent = scratch + "/blocked-parent"
FileManager.default.createFile(atPath: blockedParent, contents: Data("x".utf8))
var blockedLogs: [String] = []
let blocked = DictationJournal.open(blockedParent + "/journal", fallback: scratch + "/blocked-fb") { blockedLogs.append($0) }
record(blocked, "blocked")
if FileManager.default.fileExists(atPath: scratch + "/blocked-fb/" + day + ".jsonl")
    && blockedLogs.contains(where: { $0.contains("using default") }) {
    pass("unwritable-fallback")
} else { fail("unwritable-fallback", blockedLogs.joined(separator: " | ")) }

let permRoot = scratch + "/perm-root"
try FileManager.default.createDirectory(atPath: permRoot, withIntermediateDirectories: true)
try FileManager.default.setAttributes([.posixPermissions: NSNumber(value: 0o755)], ofItemAtPath: permRoot)
let permDir = permRoot + "/journal"
let perms = DictationJournal.open(permDir, fallback: scratch + "/perm-fb") { _ in }
record(perms, "mode")
let permFile = FileManager.default.contentsOfDirectory(atPath: permDir).first { $0.hasSuffix(".jsonl") } ?? ""
if mode(permDir) == 0o700 && mode(permDir + "/" + permFile) == 0o600 && mode(permRoot) == 0o755 { pass("perms-unix") }
else { fail("perms-unix", "dir \(mode(permDir)) file \(mode(permDir + "/" + permFile)) root \(mode(permRoot))") }

let deleteDir = scratch + "/delete"
let deleter = DictationJournal.open(deleteDir, fallback: scratch + "/delete-fb") { _ in }
let keep = UUID()
let drop = UUID()
record(deleter, "keep", id: keep)
record(deleter, "drop", id: drop)
let deleteFile = deleteDir + "/" + day + ".jsonl"
let keepLine = String(data: Data(contentsOf: URL(fileURLWithPath: deleteFile)), encoding: .utf8)?
    .split(separator: "\n", omittingEmptySubsequences: false)
    .first { $0.contains(keep.uuidString.lowercased()) }
    .map(String.init) ?? ""
if deleter.deleteEntry(drop),
   let left = try? String(contentsOfFile: deleteFile, encoding: .utf8),
   left.split(separator: "\n", omittingEmptySubsequences: true).map(String.init) == [keepLine] {
    pass("delete-entry")
} else { fail("delete-entry", "survivor changed") }

let dayDir = scratch + "/day"
let dayJournal = DictationJournal.open(dayDir, fallback: scratch + "/day-fb") { _ in }
record(dayJournal, "today")
let removed = dayJournal.deleteDay(day)
let gone = !FileManager.default.fileExists(atPath: dayDir + "/" + day + ".jsonl")
if removed == 1 && gone && dayJournal.deleteDay("1999-01-01") == 0 { pass("delete-day") }
else { fail("delete-day", "removed \(removed) gone \(gone)") }

let custom = scratch + "/reload-custom"
let other = scratch + "/reload-other"
let configPath = scratch + "/config.json"
func writeConfig(_ dir: String) throws {
    let json = "{\"wakeWords\":[\"音声入力\"],\"command\":\"true\",\"dictation\":{\"journalDir\":\(jsonString(dir))}}"
    try json.write(toFile: configPath, atomically: true, encoding: .utf8)
    try FileManager.default.setAttributes([.modificationDate: Date().addingTimeInterval(60)], ofItemAtPath: configPath)
}
func jsonString(_ value: String) -> String {
    let data = try! JSONSerialization.data(withJSONObject: [value])
    return String(data: data.dropFirst().dropLast(), encoding: .utf8)!
}
do {
    try writeConfig(custom)
    let config = try ConfigFile(path: configPath)
    DictationJournal.bind(config.cfg.dictation?.journalDir)
    let history = DictationHistory()
    history.journal = DictationJournal.active
    _ = history.add(id: UUID(), text: "first", origin: origin)
    try "{".write(toFile: configPath, atomically: true, encoding: .utf8)
    try FileManager.default.setAttributes([.modificationDate: Date().addingTimeInterval(120)], ofItemAtPath: configPath)
    config.reloadIfChanged()
    DictationJournal.bind(config.cfg.dictation?.journalDir)
    history.journal = DictationJournal.active
    _ = history.add(id: UUID(), text: "second", origin: origin)
    let customFiles = try FileManager.default.contentsOfDirectory(atPath: custom).filter { $0.hasSuffix(".jsonl") }
    let customText = try customFiles.map { try String(contentsOfFile: custom + "/" + $0, encoding: .utf8) }.joined()
    let kept = customText.split(separator: "\n").count == 2
    try writeConfig(other)
    try FileManager.default.setAttributes([.modificationDate: Date().addingTimeInterval(180)], ofItemAtPath: configPath)
    config.reloadIfChanged()
    DictationJournal.bind(config.cfg.dictation?.journalDir)
    history.journal = DictationJournal.active
    _ = history.add(id: UUID(), text: "third", origin: origin)
    let moved = FileManager.default.fileExists(atPath: other) && ((try? FileManager.default.contentsOfDirectory(atPath: other))?.contains { $0.hasSuffix(".jsonl") } == true)
    if kept && moved { pass("reload-keeps-config") } else { fail("reload-keeps-config", "kept \(kept) moved \(moved)") }
} catch {
    fail("reload-keeps-config", "\(error)")
}

exit(failed == 0 ? 0 : 1)
