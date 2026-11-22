#if os(macOS)
import CryptoKit
import Darwin
import Foundation

struct TriageProposal: Codable, Equatable {
    let id: String
    let text: String
    let createdAt: Date
    var updatedAt: Date
    var stage: TriageExecution.Stage = .said
    var kind: TriageKind?
    var routeID: String?
    var routeIndex: Int?
    var routeName: String?
    var deadline: Date?
    var reasons: [String] = []
    var destinations: [String] = []
    var answers: [String: Double]?
    var questionHash: String?
    var verified: Bool?
    var outcome: Outcome?
    var failureReason: String?
    var focusBundleID: String?

    enum Outcome: String, Codable { case sent, failed }

    func routeExceptions(in history: some Sequence<Self>) -> [String] {
        guard let routeID else { return [] }
        let matching = history.filter { $0.id != id && $0.routeID == routeID }
        var reasons: [String] = []
        if matching.lazy.filter({ $0.outcome == .sent }).prefix(5).count < 5 {
            reasons.append("送り先の最初の5件")
        }
        if matching.contains(where: { $0.createdAt < createdAt && createdAt.timeIntervalSince($0.createdAt) <= 60 }) {
            reasons.append("60秒以内に同じ送り先")
        }
        return reasons
    }
}

struct TriageFiles {
    struct Cached: Codable {
        let id: String
        let answers: [String: Double]
        let question_hash: String
        let journal_hash: String
    }

    private struct Label: Encodable {
        let id: String
        let target: [String]
        let observed: [String]
        let answers: [String: Double]?
        let question_hash: String?
        let device: String
        let timestamp: String
    }

    let proposals: URL
    let directory: URL
    let device: String
    private let cache: URL
    private let root: URL
    private let encoder = JSONEncoder()
    private let decoder = JSONDecoder()

    init(journalDir: String?, home: URL) {
        let support = home.appendingPathComponent("Library/Application Support/voice-switch", isDirectory: true)
        let fallback = support.appendingPathComponent("journal", isDirectory: true)
        let configured = journalDir?.trimmingCharacters(in: .whitespacesAndNewlines)
        let journal = configured.flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: Self.expanded($0, home: home)) } ?? fallback
        let single = journal.pathExtension.lowercased() == "jsonl"
        let deviceDir = single ? journal.deletingLastPathComponent() : journal
        root = single || journal.path == fallback.path ? journal : journal.deletingLastPathComponent()
        directory = (single ? deviceDir : root).appendingPathComponent(".triage", isDirectory: true)
        device = [".", "..", "/", ""].contains(deviceDir.lastPathComponent) ? "journal" : deviceDir.lastPathComponent
        cache = home.appendingPathComponent("Library/Caches/voice-switch/triage/\(device)", isDirectory: true)
        proposals = support.appendingPathComponent("proposals.jsonl")
        encoder.dateEncodingStrategy = .millisecondsSince1970
        decoder.dateDecodingStrategy = .millisecondsSince1970
    }

    private static func expanded(_ path: String, home: URL) -> String {
        let pattern = try! NSRegularExpression(pattern: #"\$(\w+|\{[^}]+\})"#)
        var result = path
        for match in pattern.matches(in: path, range: NSRange(path.startIndex..., in: path)).reversed() {
            guard let name = Range(match.range(at: 1), in: path), let span = Range(match.range, in: result) else { continue }
            let key = path[name].trimmingCharacters(in: CharacterSet(charactersIn: "{}"))
            if let value = ProcessInfo.processInfo.environment[key] { result.replaceSubrange(span, with: value) }
        }
        if result == "~" { return home.path }
        if result.hasPrefix("~/") { return home.appendingPathComponent(String(result.dropFirst(2))).path }
        return Platform.expandPath(result)
    }

    func loadProposals() throws -> [String: TriageProposal] {
        var byID: [String: TriageProposal] = [:]
        for var row in try read(TriageProposal.self, at: proposals) {
            if row.stage == .done && row.outcome == .failed { row.stage = .failed }
            byID[row.id] = row
        }
        return byID
    }

    func append(_ proposal: TriageProposal) throws {
        try append(proposal, to: proposals)
    }

    func label(_ proposal: TriageProposal, kind: TriageKind) throws {
        let row = Label(id: proposal.id, target: ["action:\(kind.rawValue)"], observed: proposal.destinations,
                        answers: proposal.answers, question_hash: proposal.questionHash, device: device,
                        timestamp: Date().formatted(.iso8601))
        try append(row, to: directory.appendingPathComponent("labels/\(device)-app.jsonl"))
    }

    func cached(_ id: String, at date: Date, questionHash: String) -> Cached? {
        guard let rows = try? read(Cached.self, at: cacheFile(date)) else { return nil }
        return rows.last { $0.id == id && $0.question_hash == questionHash && TriageClassifier.valid($0.answers) }
    }

    func cache(_ answers: [String: Double], id: String, at date: Date, questionHash: String) throws {
        let journalHash = Self.hash(Data(root.standardizedFileURL.resolvingSymlinksInPath().path.utf8))
        try append(Cached(id: id, answers: answers, question_hash: questionHash, journal_hash: journalHash),
                   to: cacheFile(date))
    }

    private func cacheFile(_ at: Date) -> URL {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd"
        return cache.appendingPathComponent(formatter.string(from: at) + ".jsonl")
    }

    private func read<Row: Decodable>(_ type: Row.Type, at file: URL) throws -> [Row] {
        do {
            return try Data(contentsOf: file).split(separator: 10).compactMap {
                try? decoder.decode(type, from: Data($0))
            }
        } catch let error as CocoaError where error.code == .fileReadNoSuchFile {
            return []
        }
    }

    private func append<Row: Encodable>(_ row: Row, to file: URL) throws {
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let fd = open(file.path, O_RDWR | O_APPEND | O_CREAT | O_NOFOLLOW, 0o600)
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        defer { try? handle.close() }
        guard flock(fd, LOCK_EX) == 0 else { throw POSIXError(.EIO) }
        var payload = try encoder.encode(row)
        let end = try handle.seekToEnd()
        if end > 0 {
            try handle.seek(toOffset: end - 1)
            if try handle.read(upToCount: 1)?.first != 10 { payload.insert(10, at: 0) }
        }
        payload.append(10)
        try handle.write(contentsOf: payload)
        try handle.synchronize()
    }

    static func hash(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    static func questionHash(_ questions: [String: TriageClassifier.Question]) throws -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = .withoutEscapingSlashes
        func quote(_ value: String) throws -> String {
            let quoted = String(decoding: try encoder.encode(value), as: UTF8.self)
            return quoted.utf16.map { $0 < 127 ? String(UnicodeScalar($0)!) : String(format: "\\u%04x", $0) }.joined()
        }
        func object<Value>(_ values: [String: Value], _ encode: (Value) throws -> String) throws -> String {
            let entries = try values.sorted { $0.key < $1.key }.map { try quote($0.key) + ": " + encode($0.value) }
            return "{" + entries.joined(separator: ", ") + "}"
        }
        // Python's cache uses json.dumps(sort_keys=True), including ASCII escaping and default separators.
        let canonical = try object(questions) { question in
            "{\"criteria\": " + (try object(question.criteria, quote)) + ", \"instructions\": " +
                (try quote(question.instructions)) + ", \"type\": " + (try quote(question.type)) + "}"
        }
        return hash(Data(canonical.utf8))
    }
}
#endif
