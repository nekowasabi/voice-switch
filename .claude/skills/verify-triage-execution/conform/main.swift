// Feeds every transition of transitions.json (TLC, TriageExec.tla + design.cfg) to TriageExecution.reduce.
// accept: each model transition is accepted and lands on the model's next state; proposals.jsonl gets a line
//         exactly when the model's persisted changes, and a route is started only after its 送信中 line.
// reject: from each reachable state, every event the model cannot take there is refused (nil).
// Usage: conform <transitions.json>   (built by conform.sh together with Sources/voice-switch/TriageExecution.swift)
import Foundation

typealias TE = TriageExecution
var failures = 0, shown = 0
func fail(_ s: String) {
    failures += 1; shown += 1
    if shown <= 10 { print("  " + s) }
}

let doc = try! JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1]))) as! [String: Any]
let consts = doc["constants"] as! [String: Any]
let ids = consts["U"] as! [String]
let kinds = consts["Kinds"] as! [String]
let verdicts = consts["Verdicts"] as! [String]
let maxCrashes = consts["MaxCrashes"] as! Int

func kind(_ s: String) -> TriageKind {
    guard let k = TriageKind(rawValue: s) else { print("FAIL fixture kind \(s) is not a TriageKind"); exit(1) }
    return k
}
func verdict(_ s: String) -> TE.Verdict? {
    switch s {
    case "none": return nil
    case "thought": return .thought
    case "filler": return .filler
    case "unjudged": return .unjudged
    default: return .kind(kind(s))
    }
}
func stage(_ s: String) -> TE.Stage {
    guard let st = TE.Stage(rawValue: s) else { print("FAIL fixture stage \(s) is not a Stage"); exit(1) }
    return st
}

let rules = TE.Rules(routes: Set((consts["Routes"] as! [String]).map(kind)),
                     heavy: Set((consts["Heavy"] as! [String]).map(kind)), maxCrashes: maxCrashes)

let rows: [TE.Row] = (doc["rows"] as! [[Any]]).map { f in
    TE.Row(stage: stage(f[0] as! String), persisted: stage(f[1] as! String), verdict: verdict(f[2] as! String),
           verified: f[3] as! Bool, runs: f[4] as! Int,
           runKind: (f[5] as! String) == "none" ? nil : kind(f[5] as! String),
           graced: f[6] as! Bool, userOK: f[7] as! Bool, retries: f[8] as! Int, dismissed: f[9] as! Bool, dRuns: f[10] as! Int)
}
let states: [TE.State] = (doc["states"] as! [[Any]]).map { s in
    var rs: [String: TE.Row] = [:]
    for (i, id) in ids.enumerated() { rs[id] = rows[s[2 + i] as! Int] }
    return TE.State(rows: rs, alive: s[0] as! Bool, crashes: s[1] as! Int)
}

// An event is written as in the fixture: ["classify", "u1", "look_up", true, false]. key() is its lookup string.
func key(_ e: [Any]) -> String {
    e.map { v in if let s = v as? String { return s }; return (v as! Bool) ? "true" : "false" }.joined(separator: "|")
}
func event(_ e: [Any]) -> TE.Event {
    let name = e[0] as! String
    let id = e.count > 1 ? e[1] as! String : ""
    switch name {
    case "classify": return .classify(id, verdict(e[2] as! String)!, verified: e[3] as! Bool, flagged: e[4] as! Bool)
    case "graceExpire": return .graceExpire(id)
    case "sendNow": return .sendNow(id, kind(e[2] as! String))
    case "fix": return .fix(id, kind(e[2] as! String))
    case "dismiss": return .dismiss(id)
    case "start": return .start(id)
    case "run": return .run(id)
    case "record": return .record(id)
    case "retry": return .retry(id, kind(e[2] as! String))
    case "confirmDone": return .confirmDone(id)
    case "crash": return .crash
    case "restart": return .restart
    case "done": return .done
    default: print("FAIL fixture event \(name) has no reducer event"); exit(1)
    }
}
func describe(_ st: TE.State) -> String {
    "alive=\(st.alive) crashes=\(st.crashes) " + ids.map { id in
        let r = st.rows[id]!
        return "\(id)=\(r.stage.rawValue)/\(r.persisted.rawValue)/\(r.verdict.map { "\($0)" } ?? "none")"
    }.joined(separator: " ")
}

// --- accept ---
let events = doc["events"] as! [[Any]]
let transitions = doc["transitions"] as! [[Int]]
var valid = Set<String>()
var stagesSeen = Set<String>(), eventsSeen = Set<String>()
for t in transitions {
    let (from, ev, to) = (states[t[0]], events[t[1]], states[t[2]])
    valid.insert("\(t[0])#" + key(ev))
    eventsSeen.insert(ev[0] as! String)
    for r in from.rows.values { stagesSeen.insert(r.stage.rawValue) }
    for r in to.rows.values { stagesSeen.insert(r.stage.rawValue) }
    guard let got = TE.reduce(from, event(ev), rules: rules) else {
        fail("refused \(key(ev)) in \(describe(from)); model goes to \(describe(to))"); continue
    }
    if got.state != to {
        fail("\(key(ev)) in \(describe(from)): model \(describe(to)), reducer \(describe(got.state))"); continue
    }
    for id in ids {
        let written = got.effects.compactMap { e -> TE.Stage? in
            if case .persist(let i, let s) = e, i == id { return s } else { return nil } }
        // A changed persisted needs its line; the model cannot tell an unchanged value from a rewrite of it
        // (start after retry writes 送信中 again), so an unchanged one allows at most one line of the same value.
        let changed = from.rows[id]!.persisted != to.rows[id]!.persisted
        let want = to.rows[id]!.persisted
        if changed ? written != [want] : !(written.isEmpty || written == [want]) { fail("\(key(ev)) in \(describe(from)): proposals.jsonl lines for \(id) \(written), model \(want)") }
    }
    let starts = got.effects.enumerated().filter { if case .execute = $0.element { return true } else { return false } }
    if (ev[0] as! String) == "start" {
        let id = ev[1] as! String
        let sending = got.effects.firstIndex(of: .persist(id, .executing))
        if starts.count != 1 || starts[0].element != .execute(id, from.rows[id]!.kind!) || sending == nil || sending! > starts[0].offset {
            fail("start \(id) in \(describe(from)): want persist(executing) then execute(\(id)), got \(got.effects)")
        }
    } else if !starts.isEmpty {
        fail("\(key(ev)) in \(describe(from)) starts a route: \(got.effects)")
    }
}
print(failures == 0 ? "PASS accept \(transitions.count) transitions" : "FAIL accept (\(failures) problems)")
let acceptFailures = failures
shown = 0

// --- reject ---
var alphabet: [[Any]] = [["crash"], ["restart"], ["done"]]
for id in ids {
    for v in verdicts { for ok in [false, true] { for fl in [false, true] { alphabet.append(["classify", id, v, ok, fl]) } } }
    for name in ["graceExpire", "dismiss", "start", "run", "record", "confirmDone"] { alphabet.append([name, id]) }
    for k in kinds { for name in ["sendNow", "fix", "retry"] { alphabet.append([name, id, k]) } }
}
var refused = 0
let reachable = Set(transitions.map { $0[0] })  // every reachable state has a step (Done or Restart at the end)
for (i, st) in states.enumerated() where reachable.contains(i) {
    for ev in alphabet where !valid.contains("\(i)#" + key(ev)) {
        // The model stops at MaxCrashes only to stay finite; the app has no such rule to enforce.
        if (ev[0] as! String) == "crash" && st.crashes >= maxCrashes { continue }
        if let got = TE.reduce(st, event(ev), rules: rules) {
            fail("accepted \(key(ev)) in \(describe(st)) -> \(describe(got.state)); the model has no such transition")
        } else { refused += 1 }
    }
}
print(failures == acceptFailures ? "PASS reject \(refused) events the model cannot take, in \(states.count) states"
                                  : "FAIL reject (\(failures - acceptFailures) problems)")

// --- coverage: the fixture really holds the whole model ---
let allStages = Set(TE.Stage.allCases.map(\.rawValue))
let allEvents: Set = ["classify", "graceExpire", "sendNow", "fix", "dismiss", "start", "run", "record", "retry", "confirmDone", "crash", "restart", "done"]
if stagesSeen != allStages || eventsSeen != allEvents {
    print("FAIL coverage: stages missing \(allStages.subtracting(stagesSeen).sorted()), events missing \(allEvents.subtracting(eventsSeen).sorted())")
    failures += 1
} else {
    print("PASS coverage \(allStages.count) stages, \(allEvents.count) events")
}
exit(failures == 0 ? 0 : 1)
