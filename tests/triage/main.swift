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
exit(failures == 0 ? 0 : 1)
