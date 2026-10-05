import Foundation

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

    var catalog: [Pane]
    var match: [Match]
    var policy: [Policy]
    var jev_responses: [Response]
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

if failures.isEmpty {
    print("PASS behavior fixtures")
    print("PASS pane route fixtures")
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
    case nil:
        actualKind = "<none>"
        sampleCount = 0
    }
    check(actualKind == spec.expectedKind, "segmenter expected \(spec.expectedKind) actual \(actualKind)")
    check(sampleCount >= spec.minSamples, "segmenter sample count for \(spec.expectedKind)")
}
