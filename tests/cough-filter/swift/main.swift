import Foundation

struct CoughFixture: Decodable {
    struct Entry: Decodable {
        let name: String
        let text: String
        let automaticExpectedText: String?
    }
    let cases: [Entry]
}

var assertions = 0
func check(_ condition: @autoclosure () -> Bool, _ name: String) {
    precondition(condition(), name)
    assertions += 1
}
precondition(CommandLine.arguments.count == 2, "Pass cough_policy.json.")
let decoder = JSONDecoder()
let fixture = try decoder.decode(CoughFixture.self, from: Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1])))
for item in fixture.cases {
    for rule in [WakeRule.strict, .autoB, .followup, .relaxedD2] {
        for enabled in [false, true] {
            let history = DictationHistory()
            var changes = 0
            history.onChange = { changes += 1 }
            let id = UUID()
            let expected = enabled && rule != .strict ? item.automaticExpectedText : item.text
            let retained = history.add(id: id, text: item.text, origin: WakeOrigin(rule: rule, inputIdleSeconds: 4),
                                       policy: enabled ? .filterInterjections : .disabled)
            let name = "\(item.name) \(rule) \(enabled)"
            check(retained == (expected != nil), name + " admission")
            check(history.select(id) == expected, name + " exact selected text")
            check(history.snapshot().map(\.text) == expected.map { [$0] } ?? [], name + " exact history")
            check(changes == (expected == nil ? 0 : 1), name + " notification")
        }
    }
}

func policy(_ json: String) throws -> AutomaticTranscriptPolicy {
    AutomaticTranscriptPolicy(try decoder.decode(NoWakeWordConfig.self, from: Data(json.utf8)))
}
let automatic = WakeOrigin(rule: .autoB, inputIdleSeconds: 4)
func checkPolicy(_ policy: AutomaticTranscriptPolicy, _ expected: String?) {
    let history = DictationHistory()
    let id = UUID()
    history.add(id: id, text: "あっ", origin: automatic, policy: policy)
    check(history.select(id) == expected, "captured policy admission")
    check(history.snapshot().map(\.text) == expected.map { [$0] } ?? [], "captured policy exact history")
}
checkPolicy(try policy("{}"), "あっ")
checkPolicy(try policy(#"{"filterInterjections":false}"#), "あっ")
var options = try decoder.decode(NoWakeWordConfig.self, from: Data(#"{"autoIdle":true,"filterInterjections":true}"#.utf8))
let captured = AutomaticTranscriptPolicy(options)
options = try decoder.decode(NoWakeWordConfig.self, from: Data(#"{"autoIdle":true,"filterInterjections":false}"#.utf8))
checkPolicy(captured, nil)
checkPolicy(AutomaticTranscriptPolicy(options), "あっ")
check(!NoWakeWordConfig(filterInterjections: true).enabled, "filter alone does not start automatic recording")
let defaultHistory = DictationHistory()
let defaultID = UUID()
check(defaultHistory.add(id: defaultID, text: "あっ", origin: automatic), "default history policy retains")
check(defaultHistory.select(defaultID) == "あっ", "default history exact text")

let guarded = DictationHistory()
let generation = guarded.generation
guarded.clear()
check(!guarded.add(id: UUID(), text: "はい", origin: automatic, expectedGeneration: generation, policy: captured), "clear invalidates retained words")
let duplicate = UUID()
check(guarded.add(id: duplicate, text: "はい", origin: automatic, policy: captured), "retained word enters history")
check(!guarded.add(id: duplicate, text: "うん", origin: automatic, policy: captured), "duplicate cannot replace text")
check(guarded.select(duplicate) == "はい", "duplicate keeps original text")
check(!guarded.add(id: UUID(), text: "  ", origin: automatic, policy: captured), "blank transcript rejected")
check(!guarded.add(id: UUID(), text: String(repeating: "あ", count: 100001), origin: automatic, policy: captured), "oversized transcript rejected")
let listed = try policy(#"{"ignoredTranscripts":["うん。"]}"#)
check(listed.rejects(" うん。", origin: automatic), "listed transcript rejected")
check(!listed.rejects("うん、それで", origin: automatic), "longer speech kept")
check(!listed.rejects("あっ", origin: automatic), "interjections still need filterInterjections")
check(!listed.rejects("うん", origin: WakeOrigin(rule: .strict, inputIdleSeconds: 4)), "wake-word dictation kept")
let combined = try policy(#"{"filterInterjections":true,"ignoredTranscripts":["うん"]}"#)
check(combined.rejects("あっ", origin: automatic) && combined.rejects("うん", origin: automatic), "flag and list combine")
print("PASS Swift policy \(assertions) assertions")
