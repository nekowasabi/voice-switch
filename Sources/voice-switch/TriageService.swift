#if os(macOS)
import Foundation

@MainActor
final class TriageService {
    struct Simulation: Decodable {
        enum Action: String, Decodable { case send, cancel }
        let answers: [String: Double]
        let verification: Double?
        let action: Action?
        let dictations: [String]?
        let seconds: Double?
    }

    private let settings: TriageSettings
    private let files: TriageFiles
    private let simulation: Simulation?
    private let routes: [TriageKind: TriageSettings.Route]
    private let rules: TriageExecution.Rules
    private var state = TriageExecution.State(alive: false)
    private var proposals: [String: TriageProposal] = [:]
    private var classificationTail: Task<Void, Never>?
    private var timer: Timer?
    private(set) var paused = false
    private(set) var storeError = false
    var onChange: (() -> Void)?

    init(settings: TriageSettings, journalDir: String?, home: URL = FileManager.default.homeDirectoryForCurrentUser,
         simulation: Simulation? = nil) {
        self.settings = settings
        self.simulation = simulation
        files = TriageFiles(journalDir: journalDir, home: home)
        routes = Dictionary(uniqueKeysWithValues: settings.routes.compactMap { name, route in
            TriageKind(rawValue: name).map { ($0, route) }
        })
        rules = TriageExecution.Rules(routes: Set(routes.keys), heavy: Set(routes.filter { $0.value.heavy }.keys))
    }

    func start() {
        do {
            proposals = try files.loadProposals()
            state.rows = proposals.mapValues { proposal in
                var row = TriageExecution.Row()
                row.persisted = proposal.stage
                row.verdict = proposal.kind.map(TriageExecution.Verdict.kind) ?? .unjudged
                return row
            }
        } catch {
            storeError = true
            log("triage: proposals read failed")
        }
        _ = apply(.restart)
        DictationHistory.shared.onTriageEntry = { [weak self] entry, note, task, history in
            Task { @MainActor [weak self] in self?.enqueue(entry, note: note, task: task, history: history) }
        }
    }

    var review: [TriageProposal] {
        sorted.filter {
            let stage = state.rows[$0.id]?.stage
            return stage == .review || stage == .unknown || (stage == .pending && !canAutoSend($0))
        }
    }

    var pending: [TriageProposal] {
        sorted.filter { state.rows[$0.id]?.stage == .pending && canAutoSend($0) }
            .sorted { ($0.deadline ?? .distantFuture) < ($1.deadline ?? .distantFuture) }
    }

    var today: [TriageProposal] {
        sorted.filter { state.rows[$0.id]?.stage == .done && Calendar.current.isDateInToday($0.updatedAt) }
    }

    private var sorted: [TriageProposal] { proposals.values.sorted { $0.createdAt > $1.createdAt } }

    func stage(of id: String) -> TriageExecution.Stage? { state.rows[id]?.stage }

    func routeName(_ kind: TriageKind?) -> String? {
        guard let kind, let route = routes[kind] else { return nil }
        switch route.action.delivery {
        case .command(let command):
            let executable = command.split(whereSeparator: \.isWhitespace).first.map(String.init) ?? ""
            let name = (executable as NSString).lastPathComponent
            if name == "changelogmemo-add" { return "changelogmemo" }
            return name.range(of: #"^[A-Za-z0-9_.-]{1,64}$"#, options: .regularExpression) == nil ? "コマンド" : name
        case .open(let url), .post(let url):
            let parts = URLComponents(string: url.rawValue)
            return parts?.host ?? parts?.scheme
        }
    }

    func togglePause() {
        paused.toggle()
        armTimer()
        onChange?()
    }

    func dismiss(_ id: String) { _ = apply(.dismiss(id)) }

    func confirmDone(_ id: String) {
        guard var proposal = proposals[id] else { return }
        proposal.outcome = .sent
        _ = apply(.confirmDone(id), updated: proposal)
    }

    func canRetry(_ proposal: TriageProposal) -> Bool { sameRoute(proposal) }

    func retry(_ id: String) {
        guard let proposal = proposals[id], let kind = proposal.kind, sameRoute(proposal) else { return }
        _ = apply(.retry(id, kind))
    }

    func send(_ id: String, as chosen: TriageKind? = nil) {
        guard var proposal = proposals[id], let kind = chosen ?? proposal.kind, let route = routes[kind] else { return }
        let event: TriageExecution.Event = proposal.kind == kind ? .sendNow(id, kind) : .fix(id, kind)
        proposal.kind = kind
        proposal.routeID = identity(route)
        if apply(event, updated: proposal), chosen != nil { saveLabel(proposal, kind: kind) }
    }

    func correct(_ id: String, to kind: TriageKind) {
        guard let proposal = proposals[id], state.rows[id]?.stage == .done else { return }
        saveLabel(proposal, kind: kind)
    }

    private func saveLabel(_ proposal: TriageProposal, kind: TriageKind) {
        do { try files.label(proposal, kind: kind) }
        catch { log("triage: \(proposal.id) label write failed") }
    }

    private func enqueue(_ entry: DictationHistoryEntry, note: JournalNote, task: String?, history: [DictationHistoryEntry]) {
        guard settings.enabled == true, !storeError else { return }
        let tail = classificationTail
        classificationTail = Task { [weak self] in
            await tail?.value
            await self?.classify(entry, note: note, task: task, history: history)
        }
    }

    private lazy var questions: [String: TriageClassifier.Question]? = {
        guard let path = Bundle.module.url(forResource: "triage_questions", withExtension: "json"),
              let bytes = try? Data(contentsOf: path) else { return nil }
        return try? JSONDecoder().decode([String: TriageClassifier.Question].self, from: bytes)
    }()

    private func ask(_ request: TriageRequest) async -> JevReply {
        if let simulation {
            struct Answer: Encodable { let noul: Double }
            let values = request.questions["verify_route"] == nil ? simulation.answers : simulation.verification.map { ["verify_route": $0] } ?? [:]
            guard let reply = try? JSONEncoder().encode(["answers": values.mapValues(Answer.init)]) else { return .error }
            return .data(reply)
        }
        return await requestJev(try? request.body(), logPrefix: "triage jev")
    }

    private func classify(_ entry: DictationHistoryEntry, note: JournalNote, task: String?, history: [DictationHistoryEntry]) async {
        let id = entry.id.uuidString.lowercased()
        guard !storeError, state.rows[id] == nil else { return }
        let context = TriageRequest.State(
            entry: entry, note: note, task: task, device: files.device,
            history: history.map { TriageRequest.HistoryRow(device: files.device, entry: $0) }, timeZone: .current
        )
        var proposal = TriageProposal(id: id, text: entry.text, createdAt: entry.createdAt, updatedAt: Date(),
                                      focusBundleID: note.focus?.bundleId)
        let verdict: TriageExecution.Verdict
        var verified = false
        do {
            let bands = try TriageClassifier.readThresholds(files.directory.appendingPathComponent("thresholds.json"))
            let result = await probabilities(for: proposal, context: context)
            proposal.answers = result.answers
            proposal.questionHash = result.hash
            proposal.destinations = TriageClassifier.decide(result.answers, thresholds: bands)
            if result.off { proposal.destinations = [TriageClassifier.localDestination(entry.text)] }
            let kinds = proposal.destinations.compactMap { destination in
                destination.hasPrefix("action:") ? TriageKind(rawValue: String(destination.dropFirst("action:".count))) : nil
            }
            if proposal.destinations == ["thought"] { verdict = .thought }
            else if proposal.destinations == ["filler"] { verdict = .filler }
            else if kinds.count != 1 {
                verdict = .unjudged
                proposal.reasons = [kinds.isEmpty ? reason(for: proposal.destinations.first) : "種類が複数"]
            } else {
                let kind = kinds[0]
                verdict = .kind(kind)
                proposal.kind = kind
                if result.off { proposal.reasons.append("Jev のキーなし") }
                if let answers = result.answers, TriageClassifier.hasUnsureAnswer(answers, thresholds: bands) {
                    proposal.reasons.append("判定に迷い")
                }
                if let route = routes[kind] {
                    let routeID = identity(route)
                    proposal.routeID = routeID
                    if route.heavy {
                        let probability = await verify(kind, context: context)
                        log("triage: \(id) verify \(kind.rawValue) \(probability.map { String($0) } ?? "unavailable")")
                        verified = (probability ?? 0) >= 0.7
                        proposal.verified = verified
                        if !verified { proposal.reasons.append("重い送り先の確認不足") }
                    }
                    proposal.reasons += routeExceptions(routeID, at: entry.createdAt)
                    if proposal.reasons.isEmpty {
                        proposal.deadline = Date().addingTimeInterval(route.graceSeconds ?? settings.graceSeconds)
                    }
                } else {
                    proposal.reasons.append("送り先なし")
                }
            }
        } catch {
            verdict = .unjudged
            proposal.destinations = ["unjudged:error"]
            proposal.reasons = ["しきい値を読めません"]
        }
        state.rows[id] = TriageExecution.Row()
        guard apply(.classify(id, verdict, verified: verified, flagged: !proposal.reasons.isEmpty), updated: proposal) else { return }
        if let action = simulation?.action {
            switch action {
            case .send: send(id)
            case .cancel: dismiss(id)
            }
        }
    }

    private func probabilities(for proposal: TriageProposal, context: TriageRequest.State) async -> (answers: [String: Double]?, hash: String?, off: Bool) {
        guard let questions, let request = TriageRequest.classification(questions, state: context),
              let hash = try? TriageFiles.questionHash(request.questions) else {
            return (nil, nil, false)
        }
        if simulation == nil,
           let cached = files.cached(proposal.id, at: proposal.createdAt, questionHash: hash) {
            return (cached.answers, hash, false)
        }
        switch await ask(request) {
        case .off: return (nil, nil, true)
        case .error: return (nil, nil, false)
        case .data(let bytes):
            guard let response = try? JSONDecoder().decode(TriageClassifier.Response.self, from: bytes) else { return (nil, nil, false) }
            let answers = response.answers.filter { TriageClassifier.questionIDs.contains($0.key) }.mapValues(\.noul)
            guard TriageClassifier.valid(answers) else { return (nil, nil, false) }
            if simulation == nil {
                do { try files.cache(answers, id: proposal.id, at: proposal.createdAt, questionHash: hash) }
                catch { log("triage: \(proposal.id) cache write failed") }
            }
            return (answers, hash, false)
        }
    }

    private func verify(_ kind: TriageKind, context: TriageRequest.State) async -> Double? {
        let route = TriageRequest.Route(kind: kind, destination: routeName(kind) ?? kind.rawValue)
        guard let questions, let request = TriageRequest.verification(questions, state: context, route: route),
              case .data(let bytes) = await ask(request),
              let response = try? JSONDecoder().decode(TriageClassifier.Response.self, from: bytes),
              let probability = response.answers["verify_route"]?.noul, (0...1).contains(probability) else { return nil }
        return probability
    }

    private func reason(for destination: String?) -> String {
        switch destination {
        case "unjudged:intent": return "依頼かどうか迷い"
        case "unjudged:content": return "内容の判定に迷い"
        case "unjudged:kind": return "種類が迷い"
        case "unjudged:no-jev": return "Jev のキーなし"
        default: return "振り分けに失敗"
        }
    }

    private func identity(_ route: TriageSettings.Route) -> String {
        let action = route.action
        let values: [String]
        switch action.delivery {
        case .open(let url): values = ["open", url.rawValue, action.inputParam ?? "input"]
        case .post(let url):
            let fields = (action.fields ?? WakeAction.defaultPostFields).sorted { $0.key < $1.key }
            values = ["post", url.rawValue, action.tokenEnv ?? ""] + fields.flatMap { [$0.key, $0.value] }
        case .command(let command): values = ["command", command]
        }
        return TriageFiles.hash((try? JSONEncoder().encode(values)) ?? Data())
    }

    private func sameRoute(_ proposal: TriageProposal) -> Bool {
        guard let kind = proposal.kind, let route = routes[kind] else { return false }
        return proposal.routeID == identity(route)
    }

    private func canAutoSend(_ proposal: TriageProposal) -> Bool {
        guard sameRoute(proposal), let kind = proposal.kind, let route = routes[kind], proposal.deadline != nil else { return false }
        return !route.heavy || proposal.verified == true
    }

    private func routeExceptions(_ id: String, at date: Date) -> [String] {
        let matching = proposals.values.filter { $0.routeID == id }
        var reasons: [String] = []
        if matching.lazy.filter({ $0.outcome == .sent }).prefix(5).count < 5 {
            reasons.append("送り先の最初の5件")
        }
        if matching.contains(where: { $0.createdAt < date && date.timeIntervalSince($0.createdAt) <= 60 }) {
            reasons.append("60秒以内に同じ送り先")
        }
        return reasons
    }

    @discardableResult
    private func apply(_ event: TriageExecution.Event, updated: TriageProposal? = nil) -> Bool {
        guard !storeError, let transition = TriageExecution.reduce(state, event, rules: rules) else { return false }
        for effect in transition.effects {
            guard case .persist(let id, let stage) = effect, var proposal = updated ?? proposals[id] else { continue }
            proposal.stage = stage
            proposal.updatedAt = Date()
            do { try files.append(proposal) }
            catch {
                storeError = true
                log("triage: \(id) proposals write failed")
                onChange?()
                return false
            }
            proposals[id] = proposal
            switch stage {
            case .pending:
                if let kind = proposal.kind, let deadline = proposal.deadline {
                    log("triage: \(id) pending \(kind.rawValue) until \(deadline.formatted(.iso8601))")
                }
            case .review:
                log("triage: \(id) exception \(proposal.reasons.joined(separator: ", "))")
            default: break
            }
        }
        if let updated, !transition.effects.contains(where: { if case .persist = $0 { return true }; return false }) {
            proposals[updated.id] = updated
        }
        state = transition.state
        for effect in transition.effects {
            switch effect {
            case .persist, .schedule: break
            case .start(let id): _ = apply(.start(id))
            case .execute(let id, let kind): execute(id, kind: kind)
            case .record(let id): _ = apply(.record(id))
            case .restoredUnknown(let id): log("triage: \(id) restored unknown")
            }
        }
        armTimer()
        onChange?()
        return true
    }

    private func execute(_ id: String, kind: TriageKind) {
        guard let proposal = proposals[id], let route = routes[kind] else { return }
        log("triage: \(id) sending \(kind.rawValue)")
        Task {
            let sent = await executeWakeAction(route.action, text: proposal.text, app: proposal.focusBundleID,
                                               wait: true, quiet: true)
            var finished = proposal
            finished.outcome = sent ? .sent : .failed
            _ = apply(.run(id), updated: finished)
        }
    }

    private func armTimer() {
        timer?.invalidate()
        guard !paused, !storeError, let proposal = pending.first, let deadline = proposal.deadline else { return }
        let delay = max(0.001, min(deadline.timeIntervalSinceNow, 3600))
        timer = Timer.scheduledTimer(withTimeInterval: delay, repeats: false) { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self, !self.paused else { return }
                if deadline <= Date(), self.pending.contains(where: { $0.id == proposal.id }) {
                    _ = self.apply(.graceExpire(proposal.id))
                } else { self.armTimer() }
            }
        }
    }
}
#endif
