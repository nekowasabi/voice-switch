// Swift side of the triage truth table: TriageClassifier.decide must give every case of
// tests/parity/fixtures/triage_decide.json its "want", reading answers the way TriageService reads Jev's reply
// and thresholds through TriageClassifier.readThresholds (the same file Python's read_thresholds reads).
// Usage: run.sh (builds this with Sources/voice-switch/TriageClassifier.swift and TriageExecution.swift)
import Foundation

let args = CommandLine.arguments
let fixture = try! JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: args[1]))) as! [String: Any]
let work = URL(fileURLWithPath: args[3])
var failures = 0
func fail(_ s: String) { failures += 1; if failures <= 10 { print("  " + s) } }

// Jev's reply as TriageService decodes it (TriageService.swift: decode Response, keep the 6 questions, take noul).
func answers(_ raw: Any?) -> [String: Double]? {
    guard let raw, !(raw is NSNull) else { return nil }  // the request failed
    let body: Any = (raw as? [String: Any]).map { $0.mapValues { ["noul": $0] } } ?? raw
    let bytes = try! JSONSerialization.data(withJSONObject: ["answers": body], options: [.fragmentsAllowed])
    guard let response = try? JSONDecoder().decode(TriageClassifier.Response.self, from: bytes) else { return nil }
    return response.answers.filter { TriageClassifier.questionIDs.contains($0.key) }.mapValues(\.noul)
}

func bands(_ json: Data) -> [String: TriageClassifier.Band]? {
    let file = work.appendingPathComponent("thresholds.json")
    try! json.write(to: file)
    return try? TriageClassifier.readThresholds(file)
}

let cases = fixture["cases"] as! [[String: Any]]
for c in cases {
    var thresholds: [String: TriageClassifier.Band] = [:]
    if let t = c["thresholds"] {
        guard let read = bands(try! JSONSerialization.data(withJSONObject: t)) else {
            fail("\(c["name"]!): readThresholds refused the case's thresholds"); continue
        }
        thresholds = read
    }
    let got = TriageClassifier.decide(answers(c["answers"]), thresholds: thresholds)
    let want = c["want"] as! [String]
    if got != want { fail("\(c["name"]!): want \(want), got \(got)") }
}
print(failures == 0 ? "PASS decide \(cases.count) cases of triage_decide.json" : "FAIL decide (\(failures) cases)")
let decideFailures = failures

// thresholds.json: each file below was read by Python's read_thresholds (run.sh); Swift must agree on
// refusing it, and on the band every question gets when it is accepted.
let expected = try! JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: args[2]))) as! [[String: Any]]
for e in expected {
    let name = e["name"] as! String
    let read: [String: TriageClassifier.Band]?
    if let text = e["text"] as? String { read = bands(Data(text.utf8)) }
    else { read = try? TriageClassifier.readThresholds(work.appendingPathComponent("missing.json")) }
    guard let want = e["bands"] as? [String: [String: Double]] else {
        if read != nil { fail("thresholds \(name): Python refuses it, Swift accepts it") }
        continue
    }
    guard let read else { fail("thresholds \(name): Python accepts it, Swift refuses it"); continue }
    for (q, band) in want {
        let b = read[q] ?? .standard
        if b.no != band["no"]! || b.yes != band["yes"]! { fail("thresholds \(name): \(q) Python \(band), Swift no=\(b.no) yes=\(b.yes)") }
    }
}
print(failures == decideFailures ? "PASS thresholds \(expected.count) files read as Python reads them"
                                  : "FAIL thresholds (\(failures - decideFailures) files)")
// The reducer, rule by rule (the TLA+ model checks the orderings; this checks the code takes the same steps).
typealias TE = TriageExecution
let reducerFailures = failures
let rules = TE.Rules(routes: [.selfTask: 1, .lookUp: 2], heavy: [.lookUp: [0]])
func step(_ s: TE.State, _ e: TE.Event, _ label: String) -> (state: TE.State, effects: [TE.Effect])? {
    let r = TE.reduce(s, e, rules: rules)
    if r == nil { fail("reducer: \(label) was refused") }
    return r
}
func refused(_ s: TE.State, _ e: TE.Event, _ label: String) { if TE.reduce(s, e, rules: rules) != nil { fail("reducer: \(label) was taken") } }
let fresh = TE.State(rows: ["a": TE.Row()])
// 1. a sure request waits, then goes out once and is recorded
if let p = step(fresh, .classify("a", .kind(.selfTask), verified: false, flagged: false), "classify sure"),
   let g = step(p.state, .graceExpire("a"), "grace"), let s = step(g.state, .start("a"), "start"),
   let r = step(s.state, .run("a"), "run"), let d = step(r.state, .record("a"), "record") {
    if p.state.rows["a"]?.stage != .pending { fail("reducer: a sure request is not pending") }
    if s.effects != [.persist("a", .executing), .execute("a", .selfTask, 0)] { fail("reducer: 送信中 is not written before the route runs: \(s.effects)") }
    if d.state.rows["a"]?.stage != .done || d.state.rows["a"]?.runs != 1 { fail("reducer: a sent request is not done once") }
    refused(d.state, .start("a"), "a second start")
}
// 2. unsure, unrouted and unverified heavy requests wait in 確認が要るもの
for (verdict, verified, label) in [(TE.Verdict.unjudged, false, "unjudged"), (.kind(.writeText), false, "no route"), (.kind(.lookUp), false, "heavy unverified")] {
    if let c = step(fresh, .classify("a", verdict, verified: verified, flagged: false), label), c.state.rows["a"]?.stage != .review {
        fail("reducer: \(label) is not in review")
    }
}
// 3. a cancelled request never goes out
if let p = step(fresh, .classify("a", .kind(.selfTask), verified: false, flagged: false), "classify"),
   let x = step(p.state, .dismiss("a"), "dismiss") {
    refused(x.state, .graceExpire("a"), "grace after 取消"); refused(x.state, .start("a"), "start after 取消")
}
// 4. a crash during a send restores as 送ったか不明 and does not send again on its own
if let p = step(fresh, .classify("a", .kind(.selfTask), verified: false, flagged: false), "classify"),
   let g = step(p.state, .graceExpire("a"), "grace"), let s = step(g.state, .start("a"), "start"),
   let c = step(s.state, .crash, "crash"), let r = step(c.state, .restart, "restart") {
    if r.state.rows["a"]?.stage != .unknown || r.effects != [.restoredUnknown("a")] { fail("reducer: 送信中 is not restored as unknown") }
    refused(r.state, .start("a"), "start after restore")
    if let t = step(r.state, .retry("a", .selfTask), "retry"), t.state.rows["a"]?.retries != 1 { fail("reducer: retry is not counted") }
}
// 5. a failed send stays in review and goes out again only by retry
if let p = step(fresh, .classify("a", .kind(.selfTask), verified: false, flagged: false), "classify"),
   let g = step(p.state, .graceExpire("a"), "grace"), let s = step(g.state, .start("a"), "start"),
   let f = step(s.state, .fail("a"), "fail") {
    if f.state.rows["a"]?.stage != .failed || f.effects != [.persist("a", .failed)] { fail("reducer: a failed send is not filed as failed") }
    refused(f.state, .start("a"), "start after fail")
}
// 6. the user's chosen candidate is the one that runs
if let c = step(fresh, .classify("a", .kind(.lookUp), verified: false, flagged: false), "classify heavy"),
   let n = step(c.state, .sendNow("a", .lookUp, routeIndex: 1), "send to candidate 2"),
   let s = step(n.state, .start("a"), "start"), s.effects.last != .execute("a", .lookUp, 1) {
    fail("reducer: the chosen candidate is not the one sent: \(s.effects)")
}
print(failures == reducerFailures ? "PASS reducer 6 rules (send once, review, cancel, crash, fail, chosen candidate)" : "FAIL reducer")
// Gates: what may leave the journal (sessionTalk, maybeSessionTalk, worthKeeping in TriageClassifier.swift).
let gateFailures = failures
if !TriageClassifier.sessionTalk(["needs_local": 0.7], text: "修正してください") { fail("gate: needs_local 0.7 is not session talk") }
if TriageClassifier.sessionTalk(["needs_local": 0.69], text: "明日の天気を調べて") { fail("gate: needs_local 0.69 is session talk") }
if !TriageClassifier.sessionTalk(nil, text: "アレクサ、今何時?") || !TriageClassifier.sessionTalk([:], text: "OK、グーグル。2時間たったら起こして") {
    fail("gate: a call to another voice assistant is not session talk")
}
if !TriageClassifier.maybeSessionTalk(["needs_local": 0.3]) || TriageClassifier.maybeSessionTalk(["needs_local": 0.29]) {
    fail("gate: the review band for a bot request does not start at 0.3")
}
if !TriageClassifier.worthKeeping(["keep_note": 0.8], automatic: true) || TriageClassifier.worthKeeping(["keep_note": 0.79], automatic: true) {
    fail("gate: an automatic recording needs keep_note 0.8 for a memo")
}
if !TriageClassifier.worthKeeping(["keep_note": 0.7]) || TriageClassifier.worthKeeping(["keep_note": 0.69]) || TriageClassifier.worthKeeping(nil) {
    fail("gate: a memo needs keep_note 0.7 or more, and no answer keeps nothing")
}
let stops = ["ed", "入力ストップ", "ストップ"]
for (said, want) in [("調べてください。ストップ。", "調べてください。"), ("作成してストップ。", "作成して"),
                     ("移動してから、作成して、ストップ。", "移動してから、作成して、"), ("実施してもらいたいためです。ストップ。ストップ。", "実施してもらいたいためです。"),
                     ("自動送信をストップ。", "自動送信をストップ。"), ("ストップ。", "ストップ。"), ("天気を調べて 入力ストップ", "天気を調べて"),
                     ("It is finished", "It is finished"), ("raised. ed", "raised.")] {
    let got = TriageClassifier.withoutStopWords(said, stopWords: stops)
    if got != want { fail("gate: stop word \(said) -> \(got), want \(want)") }
}
print(failures == gateFailures ? "PASS gates (session talk, review band, memo, stop word at the end)" : "FAIL gates")
exit(failures == 0 ? 0 : 1)
