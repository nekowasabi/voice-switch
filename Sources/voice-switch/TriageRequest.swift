#if os(macOS)
import Foundation

struct TriageRequest: Encodable {
    struct HistoryRow {
        let device: String
        let entry: DictationHistoryEntry
    }

    struct Route: Encodable {
        let kind: TriageKind
        let destination: String
    }

    struct State: Encodable {
        struct Utterance: Encodable { let text: String; let started: String }
        struct Metadata: Encodable { let app: String?; let task: String?; let time: String }
        struct Previous: Encodable { let text: String; let seconds_before: Int }

        let utterance: Utterance
        let context: Metadata
        let previous: [Previous]?
        fileprivate(set) var route: Route?

        init(entry: DictationHistoryEntry, note: JournalNote, task: String?, device: String,
             history: [HistoryRow], timeZone: TimeZone) {
            let clock = DateFormatter()
            clock.locale = Locale(identifier: "en_US_POSIX")
            clock.timeZone = timeZone
            clock.dateFormat = "HH:mm"
            utterance = Utterance(text: entry.text, started: DictationJournal.originLabel(entry.origin, manual: note.manual))
            context = Metadata(app: note.focus?.name.flatMap { $0.isEmpty ? nil : $0 },
                               task: task.flatMap { $0.isEmpty ? nil : $0 }, time: clock.string(from: entry.createdAt))
            let recent = history.filter { row in
                let age = entry.createdAt.timeIntervalSince(row.entry.createdAt)
                return row.device == device && row.entry.eligibleForTriage && age > 0 && age <= 60
            }.sorted { $0.entry.createdAt < $1.entry.createdAt }.suffix(2)
            previous = recent.isEmpty ? nil : recent.map {
                Previous(text: $0.entry.text, seconds_before: Int(entry.createdAt.timeIntervalSince($0.entry.createdAt)))
            }
        }
    }

    private let model = "jev-latest"
    let questions: [String: TriageClassifier.Question]
    private let state: State

    static func classification(_ questions: [String: TriageClassifier.Question], state: State) -> Self? {
        let batch = questions.filter { TriageClassifier.questionIDs.contains($0.key) }
        guard batch.count == TriageClassifier.questionIDs.count else { return nil }
        return Self(questions: batch, state: state)
    }

    static func verification(_ questions: [String: TriageClassifier.Question], state: State, route: Route) -> Self? {
        guard let question = questions["verify_route"] else { return nil }
        var state = state
        state.route = route
        return Self(questions: ["verify_route": question], state: state)
    }

    func body() throws -> Data { try JSONEncoder().encode(self) }
}
#endif
