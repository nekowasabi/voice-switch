#if os(macOS)
import Foundation

@MainActor
final class TriageService {
    struct Simulation: Decodable {
        enum Action: String, Decodable { case send, cancel, `switch` }
        let answers: [String: Double]
        let verification: Double?
        let action: Action?
        /// The candidate the simulated user picks: [<name> に送る] for send, 送り先を変更 for switch (default 1).
        let routeIndex: Int?
        let dictations: [String]?
        let seconds: Double?
    }

    private let settings: TriageSettings
    private let files: TriageFiles
    private let simulation: Simulation?
    private let routes: [TriageKind: [TriageSettings.Route]]
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
        rules = TriageExecution.Rules(routes: routes.mapValues(\.count), heavy: routes.mapValues { candidates in
            Set(candidates.indices.filter { candidates[$0].heavy })
        })
    }

    func start() {
        do {
            proposals = try files.loadProposals()
            proposals = proposals.mapValues { saved in
                guard saved.routeIndex == nil, let kind = saved.kind, let destination = savedRoute(saved) else { return saved }
                var proposal = saved
                select(destination, kind: kind, index: 0, for: &proposal)
                return proposal
            }
            state.rows = proposals.mapValues { proposal in
                var row = TriageExecution.Row()
                row.persisted = proposal.stage == .pending && !canAutoSend(proposal) ? .review : proposal.stage
                row.verdict = proposal.kind.map(TriageExecution.Verdict.kind) ?? .unjudged
                row.routeIndex = proposal.routeIndex ?? (proposal.routeID == nil ? nil : 0)
                row.verified = proposal.verified == true
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
            return [.review, .unknown, .failed].contains(stage) || (stage == .pending && !canAutoSend($0))
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

    func routeNames(_ kind: TriageKind) -> [String] {
        (routes[kind] ?? []).map { routeName($0) ?? kind.rawValue }
    }

    func routeName(_ proposal: TriageProposal) -> String? {
        proposal.routeName ?? proposal.kind.flatMap { kind in
            route(kind, at: proposal.routeIndex ?? 0).flatMap(routeName)
        }
    }

    private func route(_ kind: TriageKind, at index: Int) -> TriageSettings.Route? {
        guard let candidates = routes[kind], candidates.indices.contains(index) else { return nil }
        return candidates[index]
    }

    private func routeName(_ route: TriageSettings.Route) -> String? {
        if let name = route.name { return name }
        switch route.action.delivery {
        case .command(let command):
            let executable = command.split(whereSeparator: \.isWhitespace).first.map(String.init) ?? ""
            let name = (executable as NSString).lastPathComponent
            if name == "memo-inbox-add" { return "changelogmemo（取り込み待ち）" }
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

    func canRetry(_ proposal: TriageProposal) -> Bool { savedRoute(proposal) != nil }

    func retry(_ id: String) {
        guard let proposal = proposals[id], let kind = proposal.kind, canRetry(proposal) else { return }
        _ = apply(.retry(id, kind))
    }

    func send(_ id: String, as chosen: TriageKind? = nil, routeIndex: Int? = nil) {
        guard var proposal = proposals[id], let kind = chosen ?? proposal.kind else { return }
        let index = routeIndex ?? (kind == proposal.kind ? proposal.routeIndex : nil) ?? 0
        guard let destination = route(kind, at: index) else { return }
        let event: TriageExecution.Event = proposal.kind == kind
            ? .sendNow(id, kind, routeIndex: index) : .fix(id, kind, routeIndex: index)
        select(destination, kind: kind, index: index, for: &proposal)
        if apply(event, updated: proposal), chosen != nil { saveLabel(proposal, kind: kind) }
    }

    func switchDestination(_ id: String, to index: Int) {
        guard var proposal = proposals[id], let kind = proposal.kind, let destination = route(kind, at: index),
              canAutoSend(proposal), let deadline = proposal.deadline, deadline > Date() else { return }
        select(destination, kind: kind, index: index, for: &proposal)
        proposal.reasons = proposal.routeExceptions(in: proposals.values, warmUp: destination.heavy)
        if destination.heavy { proposal.reasons.append("重い送り先は確認が必要") }
        proposal.deadline = proposal.reasons.isEmpty
            ? Date().addingTimeInterval(destination.graceSeconds ?? settings.graceSeconds) : nil
        _ = apply(.switchDestination(id, index, verified: false, flagged: !proposal.reasons.isEmpty), updated: proposal)
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
        var keep = false
        var verified = false
        do {
            let bands = try TriageClassifier.readThresholds(files.directory.appendingPathComponent("thresholds.json"))
            let result = await probabilities(for: proposal, context: context)
            proposal.answers = result.answers
            proposal.questionHash = result.hash
            proposal.destinations = TriageClassifier.decide(result.answers, thresholds: bands)
            if result.off { proposal.destinations = [TriageClassifier.localDestination(entry.text)] }
            // Unsure whether it is a request, or whether it has content: it is kept as a memo, not held for review.
            if proposal.destinations == ["unjudged:intent"] || proposal.destinations == ["unjudged:content"] {
                proposal.destinations = ["thought"]
            }
            let kinds = proposal.destinations.compactMap { destination in
                destination.hasPrefix("action:") ? TriageKind(rawValue: String(destination.dropFirst("action:".count))) : nil
            }
            let session = !result.off && proposal.destinations != ["filler"]
                && TriageClassifier.sessionTalk(result.answers, text: entry.text)
            keep = !session && TriageClassifier.worthKeeping(result.answers)
            if session {
                verdict = .thought  // already heard by the session on screen: kept in the journal only
                log("triage: \(id) session")
            } else if proposal.destinations == ["thought"] { verdict = .thought }
            else if proposal.destinations == ["filler"] { verdict = .filler }
            else if proposal.destinations == ["unjudged:kind"] && keep { verdict = .thought }  // kept as a memo: no review
            else if kinds.count != 1 {
                verdict = .unjudged
                proposal.reasons = [kinds.isEmpty ? reason(for: proposal.destinations.first) : "種類が複数"]
            } else {
                let kind = kinds[0]
                verdict = .kind(kind)
                proposal.kind = kind
                if result.off { proposal.reasons.append("Jev のキーなし") }
                if kind == .lookUp || kind == .delegate, TriageClassifier.maybeSessionTalk(result.answers) {
                    proposal.reasons.append("画面の作業への話かも")
                }
                if let route = route(kind, at: 0) {
                    // A light route (heavy: false) is cheap to take back, so a second kind in the unsure band and the
                    // warm-up do not hold it; only a heavy route waits for them.
                    if route.heavy, let answers = result.answers, TriageClassifier.hasUnsureAnswer(answers, thresholds: bands) {
                        proposal.reasons.append("判定に迷い")
                    }
                    select(route, kind: kind, index: 0, for: &proposal)
                    if route.heavy {
                        let probability = await verify(kind, destination: proposal.routeName ?? kind.rawValue, context: context)
                        log("triage: \(id) verify \(kind.rawValue) \(probability.map { String($0) } ?? "unavailable")")
                        verified = (probability ?? 0) >= 0.7
                        proposal.verified = verified
                        if !verified { proposal.reasons.append("重い送り先の確認不足") }
                    }
                    proposal.reasons += proposal.routeExceptions(in: proposals.values, warmUp: route.heavy)
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
        if keep, verdict != .filler, verdict != .kind(.selfTask), verdict != .kind(.writeText) { memo(proposal) }
        if let action = simulation?.action {
            switch action {
            case .send: send(id, routeIndex: simulation?.routeIndex)
            case .cancel: dismiss(id)
            case .switch: switchDestination(id, to: simulation?.routeIndex ?? 1)
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
            let answers = response.answers.filter { TriageClassifier.requestIDs.contains($0.key) }.mapValues(\.noul)
            guard TriageClassifier.valid(answers) else { return (nil, nil, false) }
            if simulation == nil {
                do { try files.cache(answers, id: proposal.id, at: proposal.createdAt, questionHash: hash) }
                catch { log("triage: \(proposal.id) cache write failed") }
            }
            return (answers, hash, false)
        }
    }

    private func verify(_ kind: TriageKind, destination: String, context: TriageRequest.State) async -> Double? {
        let route = TriageRequest.Route(kind: kind, destination: destination)
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

    private func select(_ route: TriageSettings.Route, kind: TriageKind, index: Int, for proposal: inout TriageProposal) {
        let id = identity(route)
        if proposal.kind != kind || proposal.routeID != id || (proposal.routeIndex ?? 0) != index {
            proposal.verified = nil
        }
        proposal.kind = kind
        proposal.routeIndex = index
        proposal.routeID = id
        proposal.routeName = routeName(route) ?? kind.rawValue
    }

    private func savedRoute(_ proposal: TriageProposal) -> TriageSettings.Route? {
        guard let kind = proposal.kind, let route = route(kind, at: proposal.routeIndex ?? 0),
              proposal.routeID == identity(route),
              proposal.routeName == nil || proposal.routeName == (routeName(route) ?? kind.rawValue) else { return nil }
        return route
    }

    private func canAutoSend(_ proposal: TriageProposal) -> Bool {
        guard let route = savedRoute(proposal), proposal.deadline != nil else { return false }
        return !route.heavy || proposal.verified == true
    }

    @discardableResult
    private func apply(_ event: TriageExecution.Event, updated: TriageProposal? = nil) -> Bool {
        guard !storeError, let transition = TriageExecution.reduce(state, event, rules: rules) else { return false }
        for effect in transition.effects {
            guard case .persist(let id, let stage) = effect, var proposal = updated ?? proposals[id] else { continue }
            proposal.stage = stage
            proposal.updatedAt = Date()
            if stage == .executing {
                proposal.outcome = nil
                proposal.failureReason = nil
            }
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
            case .failed:
                if let kind = proposal.kind, let reason = proposal.failureReason {
                    log("triage: \(id) failed \(kind.rawValue) \(reason)")
                }
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
            case .execute(let id, let kind, let index): execute(id, kind: kind, routeIndex: index)
            case .record(let id): _ = apply(.record(id))
            case .restoredUnknown(let id): log("triage: \(id) restored unknown")
            }
        }
        armTimer()
        onChange?()
        return true
    }

    private func execute(_ id: String, kind: TriageKind, routeIndex: Int) {
        guard let proposal = proposals[id], (proposal.routeIndex ?? 0) == routeIndex,
              let route = savedRoute(proposal) else { return }
        log("triage: \(id) sending \(kind.rawValue)")
        Task {
            var finished = proposal
            let sent = await executeWakeAction(route.action, text: proposal.text, app: proposal.focusBundleID,
                                               wait: true, quiet: true) { finished.failureReason = $0 }
            finished.outcome = sent ? .sent : .failed
            _ = apply(sent ? .run(id) : .fail(id), updated: finished)
        }
    }

    /// Appends the text to the memo route at once. No proposal row: a memo is a record, not a send to review.
    private func memo(_ proposal: TriageProposal) {
        guard let route = settings.memo else { return }
        Task {
            var reason: String?
            let ok = await executeWakeAction(route.action, text: proposal.text, app: proposal.focusBundleID,
                                             wait: true, quiet: true) { reason = $0 }
            log(ok ? "triage: \(proposal.id) memo" : "triage: \(proposal.id) memo failed \(reason ?? "unknown")")
        }
    }

    private func armTimer() {
        timer?.invalidate()
        guard !paused, !storeError, let proposal = pending.first, let deadline = proposal.deadline else { return }
        let delay = max(0.001, min(deadline.timeIntervalSinceNow, 3600))
        timer = Timer.scheduledTimer(withTimeInterval: delay, repeats: false) { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self, !self.paused else { return }
                if deadline <= Date(), self.pending.contains(where: { $0.id == proposal.id && $0.deadline == deadline }) {
                    _ = self.apply(.graceExpire(proposal.id))
                } else { self.armTimer() }
            }
        }
    }
}
#endif
