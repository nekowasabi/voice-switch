import Foundation

// Word enrollment filter checks: classify, merge, journal parsing, config append.
// Mirrors the Windows tests (EnrollWordsFilter, EnrollWordsConfigAppend) and the
// parity fixture tests/parity/fixtures/enroll_words.json case for case.
let repo = CommandLine.arguments[1]
let scratch = CommandLine.arguments[2]
try FileManager.default.createDirectory(atPath: scratch, withIntermediateDirectories: true)
var failed = 0

func pass(_ id: String) { print("PASS \(id)") }
func fail(_ id: String, _ message: String) {
    failed += 1
    print("FAIL \(id) \(message)")
}

func check(_ id: String, _ condition: Bool, _ message: String = "") {
    if condition { pass(id) } else { fail(id, message) }
}

let existing = ["音声入力", "おんせい"]
let journal = ["今日は会議の資料をまとめます", "おんせいにゅうりょくで送って", "おんせいにゅうりょく開始"]

// already matches: the clip is what the config already detects.
let already = WordEnrollment.classify(candidate: "音声 入力。", existingWords: existing, journalTexts: [], isStop: false)
check("already-matches", already.verdict == .alreadyMatches && already.normalized == "音声入力", "\(already)")

// empty transcript after normalize.
let empty = WordEnrollment.classify(candidate: "、。 ", existingWords: existing, journalTexts: [], isStop: false)
check("empty", empty.verdict == .rejected("empty transcript"), "\(empty.verdict)")

// too short: 2 chars or fewer after normalize.
let short = WordEnrollment.classify(candidate: "おん", existingWords: existing, journalTexts: [], isStop: false)
check("too-short", short.verdict == .rejected("too short (2 chars, minimum 3)"), "\(short.verdict)")

// journal hit: heard in past dictations.
let noisy = WordEnrollment.classify(candidate: "おんせいにゅうりょく", existingWords: existing, journalTexts: journal, isStop: false)
check("journal-below-limit", noisy.verdict == .alias, "\(noisy.verdict)")
let noisyHit = WordEnrollment.classify(
    candidate: "まとめます", existingWords: existing,
    journalTexts: ["まとめます1", "xまとめますy", "まとめます!", "まとめますか"], isStop: false)
check("journal-hit-reject", noisyHit.verdict == .rejected("heard in 4 past dictations (limit 3)"), "\(noisyHit.verdict)")

// stop suffix: a common sentence ending would stop ordinary speech.
// ("です" is already rejected as too short; "でした" reaches the suffix rule.)
let ending = WordEnrollment.classify(candidate: "でした", existingWords: ["入力ストップ"], journalTexts: [], isStop: true)
check("stop-ending", ending.verdict == .rejected("common sentence ending; it would stop ordinary speech"), "\(ending.verdict)")

// the same candidate is fine as a wake alias.
let wakeEnding = WordEnrollment.classify(candidate: "でした", existingWords: existing, journalTexts: [], isStop: false)
check("wake-ending-alias", wakeEnding.verdict == .alias, "\(wakeEnding.verdict)")

// a genuine mis-transcription becomes an alias.
let alias = WordEnrollment.classify(candidate: "音声にゅうりょく", existingWords: existing, journalTexts: [], isStop: false)
check("alias", alias.verdict == .alias && alias.normalized == "音声にゅうりょく", "\(alias)")

// batch keeps one entry per clip, in order.
let batch = WordEnrollment.candidates(
    transcripts: ["音声入力", "音声にゅうりょく", "おん"], existingWords: existing, journalTexts: [], isStop: false)
check(
    "batch",
    batch.map(\.normalized) == ["音声入力", "音声にゅうりょく", "おん"]
        && batch.map(\.verdict) == [.alreadyMatches, .alias, .rejected("too short (2 chars, minimum 3)")],
    "\(batch)")

// merge appends without reordering or duplicating.
let (merged, added) = WordEnrollment.mergedWordList(existing: existing, aliases: ["音声にゅうりょく", "音声入力", "おんせいにゅうりょく"])
check("merge", merged == ["音声入力", "おんせい", "音声にゅうりょく", "おんせいにゅうりょく"] && added == ["音声にゅうりょく", "おんせいにゅうりょく"], "\(merged) \(added)")

// journal line parsing skips non-records.
check("journal-line", WordEnrollment.journalLineText("{\"text\":\"こんにちは\",\"origin\":\"ウェイク\"}") == "こんにちは", "text")
check("journal-line-skip", WordEnrollment.journalLineText("PARTIAL") == nil && WordEnrollment.journalLineText("{\"origin\":\"ウェイク\"}") == nil, "skip")

// journal directory reading: daily files and a lone .jsonl both work.
let journalDir = scratch + "/journal"
try FileManager.default.createDirectory(atPath: journalDir, withIntermediateDirectories: true)
let day = "{\"text\":\"おはよう\",\"origin\":\"ウェイク\"}\n{\"text\":\"おやすみ\",\"origin\":\"自動\"}\n"
try day.write(toFile: journalDir + "/2026-11-24.jsonl", atomically: true, encoding: .utf8)
try "broken\n".write(toFile: journalDir + "/2026-11-23.jsonl", atomically: true, encoding: .utf8)
check("journal-dir", WordEnrollment.loadJournalTexts(journalDir: journalDir) == ["おやすみ", "おはよう"] || WordEnrollment.loadJournalTexts(journalDir: journalDir).sorted() == ["おはよう", "おやすみ"], "dir")
let lone = scratch + "/lone.jsonl"
try day.write(toFile: lone, atomically: true, encoding: .utf8)
check("journal-file", WordEnrollment.loadJournalTexts(journalDir: lone).sorted() == ["おはよう", "おやすみ"], "file")
check("journal-missing", WordEnrollment.loadJournalTexts(journalDir: scratch + "/nope").isEmpty, "missing")

// config append preserves other keys, dedupes, backs up, and dry-runs.
let configPath = scratch + "/config.json"
let config = "{\"command\":\"open -g superwhisper://record\",\"locale\":\"ja-JP\",\"wakeWords\":[\"音声入力\"],\"stopWords\":[\"入力ストップ\"]}"
try config.write(toFile: configPath, atomically: true, encoding: .utf8)
let dryAdded = try WordEnrollment.appendWordAliases(configPath: configPath, key: "wakeWords", aliases: ["音声にゅうりょく", "音声入力"], dryRun: true)
let untouched = try String(contentsOfFile: configPath)
check("config-dry-run", dryAdded == ["音声にゅうりょく"] && untouched == config, "\(dryAdded)")
let written = try WordEnrollment.appendWordAliases(configPath: configPath, key: "wakeWords", aliases: ["音声にゅうりょく", "音声入力"], dryRun: false)
let reread = try String(contentsOfFile: configPath)
let bak = try String(contentsOfFile: configPath + ".bak")
let kept = reread.contains("\"command\"") && reread.contains("\"locale\"") && reread.contains("\"stopWords\"")
    && reread.contains("音声にゅうりょく") && reread.contains("音声入力")
check("config-append", written == ["音声にゅうりょく"] && kept && bak == config, "\(written)")
let again = try WordEnrollment.appendWordAliases(configPath: configPath, key: "wakeWords", aliases: ["音声にゅうりょく"], dryRun: false)
check("config-dedupe", again.isEmpty, "\(again)")
var badKey = false
do { _ = try WordEnrollment.appendWordAliases(configPath: configPath, key: "command", aliases: ["x"], dryRun: false) } catch { badKey = true }
check("config-bad-key", badKey, "non-list key must fail")

if failed > 0 {
    print("FAIL enroll-words harness: \(failed) check(s)")
    exit(1)
}
print("PASS enroll-words harness")
