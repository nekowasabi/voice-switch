// App-side triage pieces that need the app sources. Usage: run.sh (builds this with them).
// 1. The Jev request (docs/journal-triage-design.md, Jev に渡す状態): TriageRequest builds the same state as
//    scripts/journal_triage.py from tests/parity/fixtures/triage_state.json, sends the seven classifier questions in one
//    request, sends only verify_route with the route for a heavy route, and keys the cache by the hash Python computes.
// 2. dictation.triage.routes: only the five kinds load; any other key fails the load, and a reload keeps the previous
//    config with one log line (the rule ConfigFile.reloadIfChanged already follows for a broken file). A kind takes one
//    object (one candidate, the form before 2026-11-22) or an array of candidates, the first being the default.
// 3. The exception rules 4 and 5 (the first 5 sends, a repeat within 60 s) count per destination, not per kind.
import Foundation

let args = CommandLine.arguments  // fixture, questions file, hashes from Python
var failures = 0
func fail(_ s: String) { failures += 1; print("FAIL " + s) }
func json(_ data: Data) -> NSDictionary { try! JSONSerialization.jsonObject(with: data) as! NSDictionary }
func load(_ path: String) -> Any { try! JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: path))) }

let fixture = load(args[1]) as! [String: Any]
let device = fixture["device"] as! String
let hidden = fixture["hidden"] as! [String]
let questions = try! JSONDecoder().decode([String: TriageClassifier.Question].self,
                                          from: Data(contentsOf: URL(fileURLWithPath: args[2])))
let questionsJSON = load(args[2]) as! [String: Any]
let stamp = ISO8601DateFormatter()
stamp.formatOptions = [.withInternetDateTime, .withFractionalSeconds]

func origin(_ label: String) -> (WakeOrigin, Bool) {
    switch label {
    case "自動": return (WakeOrigin(rule: .autoB, inputIdleSeconds: nil), false)
    case "手動": return (WakeOrigin(rule: .strict, inputIdleSeconds: nil), true)
    default: return (WakeOrigin(rule: .strict, inputIdleSeconds: nil), false)
    }
}

func leaks(_ body: Data) -> [String] {
    let text = String(decoding: body, as: UTF8.self)
    return hidden.filter { text.contains($0) }
}

var built = 0
for c in fixture["cases"] as! [[String: Any]] {
    let name = c["name"] as! String
    let spoken = c["utterance"] as! [String: Any]
    let at = spoken["at"] as! String
    let created = stamp.date(from: at)!
    let (wake, manual) = origin(spoken["origin"] as! String)
    let entry = DictationHistoryEntry(id: UUID(), text: spoken["text"] as! String, origin: wake, createdAt: created)
    let focus = FocusApp(name: spoken["app"] as? String, bundleId: "BundleSentinel", exe: "ExeSentinel", pid: 987654)
    let note = JournalNote(endReason: "Silence", recordingSeconds: 1, manual: manual, focus: focus, delivery: nil)
    let history = (c["rows"] as! [[String: Any]]).map { row -> TriageRequest.HistoryRow in
        let raw = row["delivery"] as? String
        let delivery = raw == nil || raw == "absent" ? nil : JournalDelivery(rawValue: raw!)!
        let when = created.addingTimeInterval(-(row["seconds_before"] as! Double))
        return TriageRequest.HistoryRow(device: row["device"] as! String, entry: DictationHistoryEntry(
            id: UUID(), text: row["text"] as! String, origin: WakeOrigin(rule: .strict, inputIdleSeconds: nil),
            createdAt: when, delivery: delivery))
    }
    // The utterance's own offset, as Python reads the journal line; the app passes TimeZone.current.
    let offset = String(at.suffix(6))
    let seconds = (Int(offset.dropFirst().prefix(2))! * 3600 + Int(offset.suffix(2))! * 60) * (offset.hasPrefix("-") ? -1 : 1)
    let state = TriageRequest.State(entry: entry, note: note, task: spoken["task"] as? String, device: device,
                                    history: history, timeZone: TimeZone(secondsFromGMT: seconds)!)
    let want = c["want"] as! NSDictionary

    guard let request = TriageRequest.classification(questions, state: state), let body = try? request.body() else {
        fail("classification \(name): no request"); continue
    }
    let sent = json(body)
    if sent["state"] as? NSDictionary != want { fail("state \(name): sent \(sent["state"] ?? "nil"), want \(want)") }
    if Set(sent.allKeys as! [String]) != ["model", "questions", "state"] || sent["model"] as? String != "jev-latest" {
        fail("classification \(name): keys \(sent.allKeys), model \(sent["model"] ?? "nil")")
    }
    // The seven table questions plus the two gates (needs_local, keep_note), and nothing else (not verify_route).
    let seven = questionsJSON.filter { TriageClassifier.requestIDs.contains($0.key) }
    if seven.count != 9 || seven["kind_delegate"] == nil || seven["keep_note"] == nil
        || sent["questions"] as? NSDictionary != seven as NSDictionary {
        fail("classification \(name): questions \(((sent["questions"] as? NSDictionary)?.allKeys ?? []))")
    }
    if !leaks(body).isEmpty { fail("classification \(name): sent \(leaks(body))") }

    let route = TriageRequest.Route(kind: .computerChange, destination: "agent-sentinel")
    guard let check = TriageRequest.verification(questions, state: state, route: route), let checkBody = try? check.body() else {
        fail("verification \(name): no request"); continue
    }
    let verify = json(checkBody)
    let withRoute = want.mutableCopy() as! NSMutableDictionary
    withRoute["route"] = ["kind": "computer_change", "destination": "agent-sentinel"]
    if verify["state"] as? NSDictionary != withRoute { fail("verification \(name): state \(verify["state"] ?? "nil")") }
    if verify["questions"] as? NSDictionary != ["verify_route": questionsJSON["verify_route"]!] as NSDictionary
        || verify["model"] as? String != "jev-latest" {
        fail("verification \(name): questions \(((verify["questions"] as? NSDictionary)?.allKeys ?? []))")
    }
    if !leaks(checkBody).isEmpty { fail("verification \(name): sent \(leaks(checkBody))") }
    built += 1
}

// Without one of the six, or without verify_route, there is no request at all.
let anyState = TriageRequest.State(entry: DictationHistoryEntry(id: UUID(), text: "x", origin: WakeOrigin(rule: .strict, inputIdleSeconds: nil),
                                                             createdAt: Date()),
                                   note: JournalNote(manual: false), task: nil, device: device, history: [], timeZone: .current)
var five = questions
five["kind_look_up"] = nil
if TriageRequest.classification(five, state: anyState) != nil { fail("classification without kind_look_up still builds") }
var noCheck = questions
noCheck["verify_route"] = nil
if TriageRequest.verification(noCheck, state: anyState, route: .init(kind: .lookUp, destination: "d")) != nil {
    fail("verification without verify_route still builds")
}
print(failures == 0 ? "PASS request \(built) cases of triage_state.json, 7 questions in one request, verify_route with its route"
                    : "FAIL request (\(failures))")
let requestFailures = failures

// The cache key: Swift's hash of what classification sends equals Python's question_hash of the same questions,
// for the real wording and for a changed one (non-ASCII, a slash and a quote, which json.dumps escapes).
for variant in load(args[3]) as! [[String: Any]] {
    let set = try! JSONDecoder().decode([String: TriageClassifier.Question].self,
                                        from: JSONSerialization.data(withJSONObject: variant["questions"]!))
    guard let request = TriageRequest.classification(set, state: anyState),
          let hash = try? TriageFiles.questionHash(request.questions) else { fail("hash \(variant["name"]!): no request"); continue }
    if hash != variant["hash"] as! String { fail("hash \(variant["name"]!): Swift \(hash), Python \(variant["hash"]!)") }
}
print(failures == requestFailures ? "PASS question hash equals Python's question_hash" : "FAIL question hash")
let hashFailures = failures

let work = args[4]
let configPath = work + "/config.json"
var bump = 0.0
func writeConfig(wake: String, routes: [String]) {
    let body = routes.map { "\"\($0)\": {\"command\": \"true\", \"heavy\": false}" }.joined(separator: ", ")
    let text = "{\"wakeWords\": [\"\(wake)\"], \"command\": \"true\", \"dictation\": {\"triage\": {\"routes\": {\(body)}}}}"
    try! text.write(toFile: configPath, atomically: true, encoding: .utf8)
    bump += 60  // reloadIfChanged looks at the modification date
    try! FileManager.default.setAttributes([.modificationDate: Date().addingTimeInterval(bump)], ofItemAtPath: configPath)
}
// log() prints to stdout; a reload's lines are read back from a file.
func logged(_ body: () -> Void) -> [String] {
    let file = work + "/log-\(bump).txt"
    FileManager.default.createFile(atPath: file, contents: nil)
    fflush(stdout)
    let saved = dup(1), fd = open(file, O_WRONLY)
    dup2(fd, 1)
    body()
    fflush(stdout)
    dup2(saved, 1)
    close(fd); close(saved)
    return (try! String(contentsOfFile: file, encoding: .utf8)).split(separator: "\n").map(String.init)
}
func routes(_ config: ConfigFile) -> [String] { (config.cfg.dictation?.triage?.routes.keys).map { $0.sorted() } ?? [] }

let known = ["computer_change", "delegate", "look_up", "self_task", "write_text"]
writeConfig(wake: "音声入力", routes: known)
if let config = try? ConfigFile(path: configPath), routes(config) == known { } else { fail("routes: the five kinds do not load") }
for bad in ["lookup", "Self_task", "memo", ""] {
    writeConfig(wake: "音声入力", routes: ["self_task", bad])
    if (try? ConfigFile(path: configPath)) != nil { fail("routes: \(bad.isEmpty ? "an empty key" : bad) loads") }
}
writeConfig(wake: "音声入力", routes: ["self_task"])
let config = try! ConfigFile(path: configPath)
writeConfig(wake: "別の言葉", routes: ["self_task", "lookup"])
let lines = logged { config.reloadIfChanged() }
if config.cfg.wakeWords != ["音声入力"] || routes(config) != ["self_task"] {
    fail("routes: a reload with lookup replaced the config (wakeWords \(config.cfg.wakeWords), routes \(routes(config)))")
}
let kept = lines.filter { $0.contains("keeping previous") }
if kept.count != 1 || lines.count != 1 || !kept[0].contains("routes") {
    fail("routes: want one log line naming routes and keeping previous, got \(lines)")
}
writeConfig(wake: "別の言葉", routes: ["self_task", "look_up"])
_ = logged { config.reloadIfChanged() }
if config.cfg.wakeWords != ["別の言葉"] || routes(config) != ["look_up", "self_task"] {
    fail("routes: a valid reload after the bad one did not apply")
}
do {
    let text = "{\"wakeWords\": [\"音声入力\"], \"command\": \"true\", \"dictation\": {\"triage\": {\"routes\": {\"look_up\": {\"command\": \"true\", \"name\": \"  Grok Bot（調べものを代行する AI）  \"}, \"self_task\": {\"command\": \"true\", \"name\": \" \"}}}}}"
    try text.write(toFile: configPath, atomically: true, encoding: .utf8)
    let named = try ConfigFile(path: configPath).cfg.dictation?.triage?.routes
    if named?["look_up"]?.count != 1 || named?["self_task"]?.count != 1 {
        fail("routes: one object is not read as one candidate (\(named?["look_up"]?.count ?? 0), \(named?["self_task"]?.count ?? 0))")
    }
    if named?["look_up"]?.first?.name != "Grok Bot（調べものを代行する AI）" || named?["self_task"]?.first?.name != nil {
        fail("routes: name is not read trimmed, or a blank name is kept (\(String(describing: named?["look_up"]?.first?.name)), \(String(describing: named?["self_task"]?.first?.name)))")
    }
} catch { fail("routes: a route with a name does not load: \(error)") }
func command(_ route: TriageSettings.Route) -> String? {
    if case .command(let c) = route.action.delivery { return c }
    return nil
}
do {
    let text = """
    {"wakeWords": ["音声入力"], "command": "true", "dictation": {"triage": {"routes": {
      "delegate": [{"name": "Muse（メールで頼む）", "command": "mail-send muse", "heavy": false, "graceSeconds": 10},
                   {"name": "Grok Bot", "command": "grok-send"}],
      "look_up": {"command": "grok-send", "heavy": false}}}}}
    """
    try text.write(toFile: configPath, atomically: true, encoding: .utf8)
    let listed = try ConfigFile(path: configPath).cfg.dictation?.triage?.routes
    let delegate = listed?["delegate"] ?? []
    if delegate.map(command) != ["mail-send muse", "grok-send"] || delegate.map(\.name) != ["Muse（メールで頼む）", "Grok Bot"]
        || delegate.map(\.heavy) != [false, true] || delegate.map(\.graceSeconds) != [10, nil] {
        fail("routes: an array is not read as its candidates in order (\(delegate.map(command)), \(delegate.map(\.name)), \(delegate.map(\.heavy)), \(delegate.map(\.graceSeconds)))")
    }
    if listed?["look_up"]?.map(command) != ["grok-send"] { fail("routes: an object beside an array is not one candidate") }
} catch { fail("routes: an array of candidates does not load: \(error)") }
for (name, bad) in [("a candidate without a delivery", #"[{"command": "mail-send muse"}, {"name": "x"}]"#),
                    ("a candidate that is not an object", #"[{"command": "mail-send muse"}, "grok-send"]"#)] {
    let text = "{\"wakeWords\": [\"音声入力\"], \"command\": \"true\", \"dictation\": {\"triage\": {\"routes\": {\"delegate\": \(bad)}}}}"
    try! text.write(toFile: configPath, atomically: true, encoding: .utf8)
    if (try? ConfigFile(path: configPath)) != nil { fail("routes: \(name) loads") }
}
print(failures == hashFailures ? "PASS routes load only the five kinds, one object or an array of candidates; a bad reload keeps the previous config and logs one line"
                               : "FAIL routes")
let routeFailures = failures

// Rules 4 and 5 of 例外になる条件, counted for the proposal's own destination (routeID) among the others.
let t0 = Date(timeIntervalSince1970: 1_800_000_000)
func proposal(_ id: String, route: String, at seconds: Double, sent: TriageProposal.Outcome? = nil) -> TriageProposal {
    var p = TriageProposal(id: id, text: id, createdAt: t0.addingTimeInterval(seconds), updatedAt: t0)
    p.routeID = route
    p.outcome = sent
    return p
}
let warm = "送り先の最初の5件", repeated = "60秒以内に同じ送り先"
let fiveToMuse = (0..<5).map { proposal("m\($0)", route: "muse", at: Double($0) * -600, sent: .sent) }
let exceptionCases: [(String, TriageProposal, [TriageProposal], [String])] = [
    ("five sent to Muse: Muse is past its warm-up", proposal("new", route: "muse", at: 0), fiveToMuse, []),
    ("five sent to Muse: Grok Bot is not", proposal("new", route: "grok", at: 0), fiveToMuse, [warm]),
    ("four sent and one failed: still warming up", proposal("new", route: "muse", at: 0),
     Array(fiveToMuse.prefix(4)) + [proposal("f", route: "muse", at: -3000, sent: .failed)], [warm]),
    ("the proposal itself is not one of the five", proposal("m0", route: "muse", at: 0), fiveToMuse, [warm]),
    ("Muse 30 s earlier: a repeat for Muse", proposal("new", route: "muse", at: 0),
     fiveToMuse + [proposal("r", route: "muse", at: -30)], [repeated]),
    ("Muse 30 s earlier: no repeat for Grok Bot", proposal("new", route: "grok", at: 0),
     fiveToMuse + [proposal("r", route: "muse", at: -30)], [warm]),
    ("exactly 60 s earlier is a repeat", proposal("new", route: "muse", at: 0),
     fiveToMuse + [proposal("r", route: "muse", at: -60)], [repeated]),
    ("61 s earlier is not", proposal("new", route: "muse", at: 0), fiveToMuse + [proposal("r", route: "muse", at: -61)], []),
    ("a later one is not", proposal("new", route: "muse", at: 0), fiveToMuse + [proposal("r", route: "muse", at: 30)], []),
]
for (name, p, history, want) in exceptionCases {
    let got = p.routeExceptions(in: history)
    if got != want { fail("exceptions: \(name): \(got), want \(want)") }
}
do {
    let text = "{\"wakeWords\": [\"音声入力\"], \"command\": \"true\", \"dictation\": {\"triage\": {\"memo\": {\"command\": \"true\", \"name\": \"メモ\"}}}}"
    try text.write(toFile: configPath, atomically: true, encoding: .utf8)
    if try ConfigFile(path: configPath).cfg.dictation?.triage?.memo?.name != "メモ" { fail("routes: dictation.triage.memo is not read") }
} catch { fail("routes: a config with memo does not load: \(error)") }
// A light route (heavy: false) has no warm-up, but a 60 s repeat still holds it.
if proposal("new", route: "memo", at: 0).routeExceptions(in: [], warmUp: false) != [] { fail("exceptions: a light route still warms up") }
if proposal("new", route: "memo", at: 0).routeExceptions(in: [proposal("r", route: "memo", at: -30)], warmUp: false) != [repeated] {
    fail("exceptions: a light route lost its 60 s repeat check")
}
print(failures == routeFailures ? "PASS exceptions count the first 5 sends and a 60 s repeat per destination (\(exceptionCases.count) cases)"
                                : "FAIL exceptions")
let exceptionFailures = failures

// The chosen candidate survives a relaunch: proposals.jsonl keeps its index and name (the last line wins).
let home = URL(fileURLWithPath: work + "/home")
let files = TriageFiles(journalDir: nil, home: home)
var chosen = proposal("chosen", route: "grok", at: 0)
chosen.kind = .delegate
chosen.stage = .pending
chosen.routeIndex = 0
chosen.routeName = "Muse"
try! files.append(chosen)
chosen.routeIndex = 1
chosen.routeName = "Grok Bot"
try! files.append(chosen)
let reloaded = try! files.loadProposals()["chosen"]
if reloaded?.routeIndex != 1 || reloaded?.routeName != "Grok Bot" || reloaded?.kind != .delegate || reloaded?.routeID != "grok" {
    fail("proposals: the chosen candidate is not read back (\(String(describing: reloaded?.routeIndex)), \(String(describing: reloaded?.routeName)))")
}
print(failures == exceptionFailures ? "PASS proposals.jsonl keeps the chosen candidate's index and name" : "FAIL proposals")
exit(failures == 0 ? 0 : 1)
