import Foundation

enum TriageClassifier {
    struct Band: Decodable {
        let no: Double
        let yes: Double

        static let standard = Band(no: 0.3, yes: 0.7)

        init(no: Double, yes: Double) {
            self.no = no
            self.yes = yes
        }

        init(from decoder: Decoder) throws {
            let values = try [String: Double](from: decoder)
            guard values.count == 2, let no = values["no"], let yes = values["yes"],
                  (0...1).contains(no), (0...1).contains(yes), no < yes else {
                throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Invalid triage band"))
            }
            self.init(no: no, yes: yes)
        }
    }

    struct Question: Codable {
        let type: String
        let instructions: String
        let criteria: [String: String]
    }

    struct Response: Decodable {
        struct Answer: Decodable {
            let noul: Double
        }

        let answers: [String: Answer]
    }

    static let questionIDs = ["wants_action", "is_filler"] + TriageKind.allCases.map { "kind_\($0.rawValue)" }

    static func readThresholds(_ file: URL) throws -> [String: Band] {
        do {
            let bands = try JSONDecoder().decode([String: Band].self, from: Data(contentsOf: file))
            guard bands.keys.allSatisfy(questionIDs.contains) else {
                throw DecodingError.dataCorrupted(.init(codingPath: [], debugDescription: "Unknown triage question"))
            }
            return bands
        } catch let error as CocoaError where error.code == .fileReadNoSuchFile {
            return [:]
        }
    }

    static func valid(_ answers: [String: Double]) -> Bool {
        questionIDs.allSatisfy { id in answers[id].map { (0...1).contains($0) } ?? false }
    }

    static func decide(_ answers: [String: Double]?, thresholds: [String: Band] = [:]) -> [String] {
        guard let answers, valid(answers) else { return ["unjudged:error"] }
        switch bucket("wants_action", answers, thresholds) {
        case .yes:
            let kinds = TriageKind.allCases.filter { bucket("kind_\($0.rawValue)", answers, thresholds) == .yes }
            return kinds.isEmpty ? ["unjudged:kind"] : kinds.map { "action:\($0.rawValue)" }
        case .unsure:
            return ["unjudged:intent"]
        case .no:
            switch bucket("is_filler", answers, thresholds) {
            case .yes: return ["filler"]
            case .unsure: return ["unjudged:content"]
            case .no: return ["thought"]
            }
        }
    }

    static func hasUnsureAnswer(_ answers: [String: Double], thresholds: [String: Band]) -> Bool {
        valid(answers) && questionIDs.contains { bucket($0, answers, thresholds) == .unsure }
    }

    static func localDestination(_ text: String) -> String {
        let markers: [(TriageKind, String)] = [
            (.selfTask, #"\b(?:task|todo)\s*[:：]|(?:タスク|やること)\s*[:：]|タスクに(?:追加|登録)"#),
            (.lookUp, #"\bresearch\s*[:：]|(?:調査|リサーチ)\s*[:：]|調べて"#),
            (.writeText, #"\bdraft\s*[:：]|(?:下書き|ドラフト)(?:\s*[:：]|を(?:書いて|作って))"#),
            (.computerChange, #"\bagent\s*[:：]|エージェント\s*[:：]|(?:エージェント|Claude|Codex|Grok|クロード)(?:に頼|に依頼)"#),
        ]
        let matches = markers.filter { text.range(of: $0.1, options: [.regularExpression, .caseInsensitive]) != nil }
        return matches.count == 1 ? "action:\(matches[0].0.rawValue)" : "unjudged:no-jev"
    }

    private enum Bucket { case no, unsure, yes }

    private static func bucket(_ id: String, _ answers: [String: Double], _ thresholds: [String: Band]) -> Bucket {
        let band = thresholds[id] ?? .standard
        let value = answers[id]!
        if value >= band.yes { return .yes }
        if value <= band.no { return .no }
        return .unsure
    }
}
