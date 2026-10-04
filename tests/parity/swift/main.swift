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

var failures: [String] = []
let args = CommandLine.arguments
guard args.count == 3 else {
    FileHandle.standardError.write(Data("usage: swift-parity text_matching.json segmenter.json\n".utf8))
    exit(2)
}

let decoder = JSONDecoder()
let textFixture = try decoder.decode(TextFixture.self, from: Data(contentsOf: URL(fileURLWithPath: args[1])))
let segmenterFixture = try decoder.decode(SegmenterFixture.self, from: Data(contentsOf: URL(fileURLWithPath: args[2])))

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

if failures.isEmpty {
    print("PASS behavior fixtures")
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
