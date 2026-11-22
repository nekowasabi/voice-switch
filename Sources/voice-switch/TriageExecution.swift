import Foundation

public enum TriageKind: String, CaseIterable, Codable {
    case selfTask = "self_task"
    case lookUp = "look_up"
    case writeText = "write_text"
    case computerChange = "computer_change"
    case delegate
}

public enum TriageExecution {
    public enum Stage: String, Codable, CaseIterable {
        case said, pending, review, approved, executing, ran, done, dismissed, listed, hidden, unknown, failed
    }

    enum Verdict: Equatable {
        case kind(TriageKind)
        case thought, filler, unjudged
    }

    struct Row: Equatable {
        var stage: Stage = .said
        var persisted: Stage = .said
        var verdict: Verdict?
        var verified = false
        var runs = 0
        var runKind: TriageKind?
        var routeIndex: Int?
        var runRouteIndex: Int?
        var graced = false
        var userOK = false
        var retries = 0
        var dismissed = false
        var dRuns = 0

        var kind: TriageKind? {
            if case .kind(let kind) = verdict { return kind }
            return nil
        }
    }

    struct State: Equatable {
        var rows: [String: Row] = [:]
        var alive = true
        var crashes = 0
    }

    struct Rules {
        let routes: [TriageKind: Int]
        let heavy: [TriageKind: Set<Int>]
        var maxCrashes = 1

        func contains(_ kind: TriageKind, at index: Int) -> Bool {
            index >= 0 && index < (routes[kind] ?? 0)
        }

        func isHeavy(_ kind: TriageKind, at index: Int) -> Bool {
            heavy[kind]?.contains(index) == true
        }
    }

    enum Event {
        case classify(String, Verdict, verified: Bool, flagged: Bool)
        case graceExpire(String)
        case sendNow(String, TriageKind?, routeIndex: Int = 0)
        case fix(String, TriageKind?, routeIndex: Int = 0)
        case switchDestination(String, Int, verified: Bool, flagged: Bool)
        case dismiss(String)
        case start(String)
        case run(String)
        case fail(String)
        case record(String)
        case retry(String, TriageKind?)
        case confirmDone(String)
        case crash, restart, done
    }

    enum Effect: Equatable {
        case persist(String, Stage)
        case schedule(String)
        case start(String)
        case execute(String, TriageKind, Int)
        case record(String)
        case restoredUnknown(String)
    }

    static func restore(_ persisted: Stage) -> Stage {
        persisted == .executing ? .unknown : persisted
    }

    static func reduce(_ state: State, _ event: Event, rules: Rules) -> (state: State, effects: [Effect])? {
        var next = state
        switch event {
        case .crash:
            guard state.alive, state.crashes < rules.maxCrashes else { return nil }
            next.alive = false
            next.crashes += 1
            return (next, [])
        case .restart:
            guard !state.alive else { return nil }
            next.alive = true
            var effects: [Effect] = []
            for id in state.rows.keys.sorted() {
                let stage = restore(state.rows[id]!.persisted)
                next.rows[id]?.stage = stage
                if stage == .pending { effects.append(.schedule(id)) }
                if stage == .unknown { effects.append(.restoredUnknown(id)) }
            }
            return (next, effects)
        case .done:
            guard state.alive, state.rows.values.allSatisfy({
                [.done, .dismissed, .listed, .hidden].contains($0.stage)
            }) else { return nil }
            return (state, [])
        default:
            guard state.alive, let (id, transition) = rowTransition(state, event, rules: rules) else { return nil }
            next.rows[id] = transition.row
            return (next, transition.effects)
        }
    }

    private struct Transition {
        var row: Row
        var effects: [Effect] = []

        mutating func persist(_ id: String, _ stage: Stage) {
            row.stage = stage
            row.persisted = stage
            effects.append(.persist(id, stage))
        }

        mutating func approve(_ id: String) {
            row.stage = .approved
            effects.append(.start(id))
        }
    }

    private static func shown(_ verdict: Verdict, verified: Bool, flagged: Bool, rules: Rules) -> Stage {
        switch verdict {
        case .filler: return .hidden
        case .thought: return .listed
        case .unjudged: return .review
        case .kind(let kind):
            return (!rules.contains(kind, at: 0) || (rules.isHeavy(kind, at: 0) && !verified) || flagged)
                ? .review : .pending
        }
    }

    private static func rowTransition(_ state: State, _ event: Event, rules: Rules) -> (String, Transition)? {
        let id: String
        switch event {
        case .classify(let value, _, _, _), .graceExpire(let value), .sendNow(let value, _, _),
             .fix(let value, _, _), .switchDestination(let value, _, _, _),
             .dismiss(let value), .start(let value), .run(let value), .fail(let value),
             .record(let value), .retry(let value, _), .confirmDone(let value):
            id = value
        case .crash, .restart, .done:
            return nil
        }
        guard let row = state.rows[id] else { return nil }
        var next = Transition(row: row)
        switch (row.stage, event) {
        case (.said, .classify(_, let verdict, let verified, let flagged)):
            next.row.verdict = verdict
            next.row.verified = next.row.kind.map { rules.isHeavy($0, at: 0) && verified } ?? false
            if let kind = next.row.kind, rules.contains(kind, at: 0) { next.row.routeIndex = 0 }
            let stage = shown(verdict, verified: verified, flagged: flagged, rules: rules)
            next.persist(id, stage)
            if stage == .pending { next.effects.append(.schedule(id)) }
        case (.pending, .graceExpire):
            next.row.graced = true
            next.approve(id)
        case (.pending, .switchDestination(_, let index, let verified, let flagged)):
            guard let kind = row.kind, index != row.routeIndex, rules.contains(kind, at: index) else { return nil }
            next.row.routeIndex = index
            let heavy = rules.isHeavy(kind, at: index)
            next.row.verified = heavy && verified
            next.row.graced = false
            next.row.userOK = false
            let stage = flagged || (heavy && !verified) ? Stage.review : .pending
            next.persist(id, stage)
            if stage == .pending { next.effects.append(.schedule(id)) }
        case (.pending, .sendNow(_, let kind, let index)), (.review, .sendNow(_, let kind, let index)):
            guard let kind, row.kind == kind, rules.contains(kind, at: index),
                  row.stage == .review || row.routeIndex == index else { return nil }
            if row.routeIndex != index {
                next.row.routeIndex = index
                next.row.verified = false
                next.persist(id, .review)
            }
            next.row.userOK = true
            next.approve(id)
        case (.review, .fix(_, let kind, let index)):
            guard let kind, row.kind != kind, rules.contains(kind, at: index) else { return nil }
            next.row.verdict = .kind(kind)
            next.row.routeIndex = index
            next.row.verified = false
            next.row.userOK = true
            next.persist(id, .review)
            next.approve(id)
        case (.pending, .dismiss), (.review, .dismiss), (.unknown, .dismiss), (.failed, .dismiss):
            next.row.dismissed = true
            next.row.dRuns = row.runs
            next.persist(id, .dismissed)
        case (.approved, .start):
            guard let kind = row.kind, let index = row.routeIndex else { return nil }
            next.persist(id, .executing)
            next.effects.append(.execute(id, kind, index))
        case (.executing, .run):
            next.row.stage = .ran
            next.row.runs += 1
            next.row.runKind = row.kind
            next.row.runRouteIndex = row.routeIndex
            next.effects.append(.record(id))
        case (.executing, .fail):
            next.row.runs += 1
            next.row.runKind = row.kind
            next.row.runRouteIndex = row.routeIndex
            next.persist(id, .failed)
        case (.ran, .record), (.unknown, .confirmDone):
            next.persist(id, .done)
        case (.unknown, .retry(_, let kind)), (.failed, .retry(_, let kind)):
            guard let kind, row.kind == kind else { return nil }
            next.row.retries += 1
            next.approve(id)
        default:
            return nil
        }
        return (id, next)
    }
}
