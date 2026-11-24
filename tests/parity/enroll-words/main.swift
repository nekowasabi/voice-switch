import Foundation

// Parity probe for the word enrollment filter: every case of
// tests/parity/fixtures/enroll_words.json must classify the same on Swift and C#.
struct EnrollFixture: Decodable {
    struct Expect: Decodable { var verdict: String; var reason: String?; var normalized: String }
    struct Case: Decodable {
        var name: String
        var candidate: String
        var existing: [String]
        var journal: [String]
        var isStop: Bool
        var expected: Expect
    }
    var cases: [Case]
}

let args = CommandLine.arguments
guard args.count == 2 else {
    FileHandle.standardError.write(Data("usage: enroll_words_parity enroll_words.json\n".utf8))
    exit(2)
}
let fixture = try JSONDecoder().decode(EnrollFixture.self, from: Data(contentsOf: URL(fileURLWithPath: args[1])))
var failed = 0
for item in fixture.cases {
    let got = WordEnrollment.classify(candidate: item.candidate, existingWords: item.existing, journalTexts: item.journal, isStop: item.isStop)
    let want: WordEnrollmentVerdict =
        item.expected.verdict == "alias" ? .alias
        : item.expected.verdict == "alreadyMatches" ? .alreadyMatches : .rejected(item.expected.reason ?? "")
    if got.normalized == item.expected.normalized, got.verdict == want {
        print("PASS enroll \(item.name)")
    } else {
        failed += 1
        print("FAIL enroll \(item.name): got \(got.normalized) \(got.verdict.label), wanted \(item.expected.normalized) \(item.expected.verdict)/\(item.expected.reason ?? "-")")
    }
}
if failed > 0 { exit(1) }
print("PASS enroll words")
