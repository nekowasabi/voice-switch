import Foundation

func checkWakeHistory() {
    let recovery = ModeRestore(previous: "before", temporary: "temporary")
    precondition(recovery.decide("temporary") == .restore)
    precondition(recovery.decide("user-choice") == .clear)
    precondition(recovery.decide(nil) == .unknown)
    let safeMode = #"{"key":"safe","name":"voice","autoPaste":false}"#
    precondition(superwhisperAutoPasteDisabled("voice", modeJsons: [safeMode]))
    precondition(!superwhisperAutoPasteDisabled("safe", modeJsons: [safeMode, safeMode]))
    precondition(!superwhisperAutoPasteDisabled("safe", modeJsons: [#"{"key":"safe","autoPaste":0}"#]))
    precondition(NoWakePolicy.belowMinHandoff(sampleCount: 15999, rate: 16000, options: nil))
    precondition(!NoWakePolicy.belowMinHandoff(sampleCount: 16000, rate: 16000, options: nil))
    precondition(NoWakePolicy.belowMinHandoff(sampleCount: 7999, rate: 16000, options: NoWakeWordConfig(minHandoffMs: 500)))
    precondition(!NoWakePolicy.belowMinHandoff(sampleCount: 0, rate: 16000, options: NoWakeWordConfig(minHandoffMs: 0)))
    func runs(_ text: String) -> Transcript { Transcript(runs: text.enumerated().map { (String($1), Double($0) * 0.5, nil) }) }
    precondition(stopWordStart(runs("思っています入力ストップ"), stopWords: ["入力ストップ"]) == 3.0)
    precondition(stopWordStart(runs("入力ストップ。"), stopWords: ["入力ストップ"]) == 0)
    precondition(stopWordStart(runs("入力ストップです"), stopWords: ["入力ストップ"]) == nil)
    precondition(stopWordStart(runs("今日はST"), stopWords: ["st"]) == 1.5)
    precondition(NoWakePolicy.onSpeech(NoWakeWordConfig(autoIdle: true), idleSeconds: 3, inFollowupWindow: false)?.rule == .autoB)
    precondition(NoWakePolicy.onSpeech(NoWakeWordConfig(autoIdle: true), idleSeconds: nil, inFollowupWindow: false) == nil)
    precondition(NoWakePolicy.onSpeech(NoWakeWordConfig(autoIdle: true), idleSeconds: 2.999, inFollowupWindow: false) == nil)
    precondition(NoWakePolicy.onSpeech(NoWakeWordConfig(followup: true, autoIdle: true), idleSeconds: 4, inFollowupWindow: true)?.rule == .followup)
    precondition(!NoWakePolicy.relaxedWake("hexxo", readings: ["hello"]))
    precondition(NoWakePolicy.relaxedWake("ほうせいにはいる", readings: ["おんせいにはいる"]))
    precondition(!NoWakePolicy.relaxedWake("ほうせい", readings: ["おんせい"]))
    precondition(!NoWakePolicy.relaxedWake("ほうせいにはいる本文", readings: ["おんせいにはいる"]))
    for mask in 0..<32 {
        let safety = PasteSafety(targetFocused: mask & 1 != 0, plainTextField: mask & 2 != 0,
            compositionClear: mask & 4 != 0, clipboardRestorable: mask & 8 != 0, inputUnchanged: mask & 16 != 0)
        precondition(safety.allowsPaste == (mask == 31))
    }
    var time = Date(timeIntervalSince1970: 0)
    let history = DictationHistory(now: { time }, capacity: 2, retention: 30)
    let first = UUID(), second = UUID(), third = UUID()
    let origin = WakeOrigin(rule: .autoB, inputIdleSeconds: 4)
    precondition(history.add(id: first, text: "first", origin: origin))
    precondition(history.add(id: second, text: "second", origin: origin))
    precondition(!history.add(id: second, text: "replaced", origin: origin))
    history.add(id: third, text: "third", origin: origin)
    precondition(history.snapshot().map(\.text) == ["third", "second"])
    precondition(history.select(first) == nil)
    time = Date(timeIntervalSince1970: 30)
    precondition(history.select(third) == nil)
    history.add(id: first, text: "new", origin: origin)
    let generation = history.generation
    history.clear()
    precondition(history.snapshot().isEmpty)
    precondition(!history.add(id: first, text: "old completion", origin: origin, expectedGeneration: generation))
    precondition(history.add(id: first, text: "new completion", origin: origin))
    time = Date(timeIntervalSince1970: 29)
    precondition(history.select(first) == nil)
    precondition(!history.add(id: first, text: String(repeating: "x", count: 100_001), origin: origin))
    precondition(WakeOrigin(rule: .relaxedD2, inputIdleSeconds: .nan).fields == "rule=relaxed_d2 inputIdleSeconds=-1")
}
checkWakeHistory()

struct TextFixture: Decodable {
    struct Normalization: Decodable {
        var name: String
        var input: String
        var expected: String
    }

    var normalization: [Normalization]
}

struct SegmenterFixture: Decodable {
    struct Spec: Decodable {
        var quietFramesBefore: Int
        var loudFrames: Int
        var quietFramesAfter: Int?
        var expectedKind: String
        var minSamples: Int
    }

    var selftest: Spec
    var head: Spec
}

struct PaneRouteFixture: Decodable {
    struct Pane: Decodable {
        var id: String
        var window: String
        var title: String
        var command: String
    }

    struct Match: Decodable {
        var name: String
        var dictation: String
        var agents: [String: [String]]
        var hits: [String]
    }

    struct Pick: Decodable {
        var pane: String?
        var confidence: Double
    }

    struct Policy: Decodable {
        var name: String
        var hits: [String]
        var pick: Pick?
        var send: String?
        var reason: String
    }

    struct Response: Decodable {
        var name: String
        var catalog: [String]
        var body: String
        var pick: Pick?
    }

    struct Extract: Decodable {
        var name: String
        var dictation: String
        var body: String?
    }

    struct Candidates: Decodable {
        var name: String
        var dictation: String
        var labels: [String]
        var candidates: [String]
    }

    struct BodyPick: Decodable {
        var choice: String?
        var confidence: Double
    }

    struct BodyResponse: Decodable {
        var name: String
        var catalog: [String]
        var body: String
        var pick: BodyPick?
    }

    struct ResolveBody: Decodable {
        var name: String
        var dictation: String
        var labels: [String]
        var pick: BodyPick?
        var body: String?
        var reason: String
    }

    var catalog: [Pane]
    var match: [Match]
    var policy: [Policy]
    var jev_responses: [Response]
    var extract: [Extract]
    var candidates: [Candidates]
    var body_jev_responses: [BodyResponse]
    var resolve_body: [ResolveBody]
}

var failures: [String] = []
let args = CommandLine.arguments
guard args.count == 4 else {
    FileHandle.standardError.write(Data("usage: swift-parity text_matching.json segmenter.json pane_route.json\n".utf8))
    exit(2)
}

let decoder = JSONDecoder()
let textFixture = try decoder.decode(TextFixture.self, from: Data(contentsOf: URL(fileURLWithPath: args[1])))
let segmenterFixture = try decoder.decode(SegmenterFixture.self, from: Data(contentsOf: URL(fileURLWithPath: args[2])))
let paneFixture = try decoder.decode(PaneRouteFixture.self, from: Data(contentsOf: URL(fileURLWithPath: args[3])))

for item in textFixture.normalization {
    let actual = normalize(item.input)
    check(actual == item.expected, "normalize \(item.name) expected=\(item.expected) actual=\(actual)")
}

let segmenterConfig = Config(
    wakeWords: ["test"],
    command: "true",
    maxSeconds: 2.5,
    hangoverMs: 300,
    prerollMs: 300,
    minSpeechMs: 300,
    vadRatio: 3.0,
    vadMinRMS: 0.005
)
checkSegmenter(segmenterFixture.selftest, config: segmenterConfig)
checkSegmenter(segmenterFixture.head, config: segmenterConfig)

let panes = paneFixture.catalog.map { PaneLabel(id: $0.id, window: $0.window, title: $0.title, command: $0.command) }
for item in paneFixture.match {
    let actual = matchingPanes(item.dictation, panes, agents: item.agents).map { $0.id }
    check(actual == item.hits, "pane match \(item.name) expected=\(item.hits) actual=\(actual)")
}
for item in paneFixture.policy {
    let hits = item.hits.map { PaneLabel(id: $0, window: "", title: "", command: "") }
    let pick = item.pick.map { JevPick(pane: $0.pane, confidence: $0.confidence) }
    let decision = decideRoute(hits, pick)
    check(decision.pane == item.send && decision.reason == item.reason,
          "pane policy \(item.name) expected=\(item.send ?? "nil") (\(item.reason)) actual=\(decision.pane ?? "nil") (\(decision.reason))")
}
for item in paneFixture.jev_responses {
    let actual = parseJevPick(Data(item.body.utf8), catalog: item.catalog)
    let expected = item.pick.map { JevPick(pane: $0.pane, confidence: $0.confidence) }
    check(actual == expected, "jev parse \(item.name)")
}
for item in paneFixture.extract {
    let actual = extractSendBody(item.dictation)
    check(actual == item.body, "pane extract \(item.name) expected=\(item.body ?? "nil") actual=\(actual ?? "nil")")
}
for item in paneFixture.candidates {
    let actual = sendBodyCandidates(item.dictation, labels: item.labels)
    check(actual == item.candidates, "pane candidates \(item.name) expected=\(item.candidates) actual=\(actual)")
}
for item in paneFixture.body_jev_responses {
    let actual = parseJevBodyPick(Data(item.body.utf8), catalog: item.catalog)
    let expected = item.pick.map { JevBodyPick(choice: $0.choice, confidence: $0.confidence) }
    check(actual == expected, "body jev parse \(item.name)")
}
for item in paneFixture.resolve_body {
    let pick = item.pick.map { JevBodyPick(choice: $0.choice, confidence: $0.confidence) }
    let actual = resolveSendBody(item.dictation, labels: item.labels, bodyPick: pick)
    check(actual.body == item.body && actual.reason == item.reason,
          "resolve body \(item.name) expected=\(item.body ?? "nil") (\(item.reason)) actual=\(actual.body ?? "nil") (\(actual.reason))")
}

if failures.isEmpty {
    print("PASS behavior fixtures")
    print("PASS pane route fixtures")
    print("PASS pane extract fixtures")
    print("PASS pane candidates fixtures")
    print("PASS pane body resolve fixtures")
} else {
    for failure in failures {
        FileHandle.standardError.write(Data("FAIL \(failure)\n".utf8))
    }
    exit(1)
}

func check(_ condition: Bool, _ message: String) {
    if !condition {
        failures.append(message)
    }
}

func checkSegmenter(_ spec: SegmenterFixture.Spec, config: Config) {
    var segmenter = Segmenter(cfg: config)
    let quiet = [Float](repeating: 0.0001, count: frameLen)
    let loud = (0..<frameLen).map { i in Float(0.2 * sin(Float(i) * 0.5)) }
    var seen: Segmenter.Event?
    for _ in 0..<spec.quietFramesBefore {
        _ = segmenter.push(quiet)
    }
    for _ in 0..<spec.loudFrames {
        if seen == nil {
            seen = segmenter.push(loud)
        }
    }
    for _ in 0..<(spec.quietFramesAfter ?? 0) {
        if seen == nil {
            seen = segmenter.push(quiet)
        }
    }
    let actualKind: String
    let sampleCount: Int
    switch seen {
    case .utterance(let samples):
        actualKind = "utterance"
        sampleCount = samples.count
    case .head(let samples):
        actualKind = "head"
        sampleCount = samples.count
    case .tail(let samples):
        actualKind = "tail"
        sampleCount = samples.count
    case nil:
        actualKind = "<none>"
        sampleCount = 0
    }
    check(actualKind == spec.expectedKind, "segmenter expected \(spec.expectedKind) actual \(actualKind)")
    check(sampleCount >= spec.minSamples, "segmenter sample count for \(spec.expectedKind)")
}
