// tmux pane route: the quoted body of the dictation goes to one tmux pane only when a closed catalog label hits.
// Jev (TypeSafe) may confirm, narrow, or veto that hit; it never names a pane the catalog did not.
import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Closed catalog row from `tmux list-panes`. Addressed only by pane id (`%0`), never by index.
struct PaneLabel {
    var id: String
    var window: String
    var title: String
    var command: String
}

/// Allowlisted agent command names. focusbm walks WSL `/proc/*/environ` for `TMUX_PANE`
/// because `pane_current_command` is often `node` or `python`, and `comm` is truncated
/// (TmuxProvider: "pane_current_command は comm 16文字制限").
let agentCommandNames = ["claude", "aider", "gemini", "copilot", "codex", "devin", "hermes", "opencode", "pi", "grok", "cursor-agent"]
let commNameLimit = 16
let agentDaemonMarkers = [" app-server", " mcp-server", " --chrome-native-host", "opencode serve", " bg-pty-host", " bg-spare", " daemon run"]

/// First 16 characters, then the allowlist. A `grok-` prefix is `grok` (versioned comm).
func canonicalAgentName(_ raw: String) -> String? {
    let name = String(raw.prefix(commNameLimit))
    if name.isEmpty { return nil }
    if agentCommandNames.contains(name) { return name }
    if name.hasPrefix("grok-") { return "grok" }
    return nil
}

/// `%` plus ASCII digits. Rejects indexes and anything else.
func isPaneID(_ id: String) -> Bool {
    guard id.utf8.first == UInt8(ascii: "%") else { return false }
    let digits = id.utf8.dropFirst()
    return !digits.isEmpty && digits.allSatisfy { $0 >= UInt8(ascii: "0") && $0 <= UInt8(ascii: "9") }
}

func parsePanes(_ text: String) -> [PaneLabel]? {
    var panes: [PaneLabel] = []
    for raw in text.split(whereSeparator: \.isNewline) {
        let fields = raw.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
        guard fields.count == 4 else { return nil }
        panes.append(PaneLabel(id: fields[0], window: fields[1], title: fields[2], command: fields[3]))
    }
    return panes
}

/// A pane hits when any non-empty label is a case-insensitive substring of the utterance.
/// Labels are the title, the window name, `pane_current_command`, and any allowlisted agent
/// name in `agents` for that pane id. `lowercased()` matches Python `str.casefold` for the proof utterances.
/// `agents` is injected by the caller. On Linux, `discoverAgentNames()` fills it from `/proc`.
/// Mac has no `/proc`, so the map stays empty and only title, window, and current command match
/// until a Mac equivalent exists. No Mac build is claimed.
func matchingPanes(_ utterance: String, _ panes: [PaneLabel], agents: [String: [String]] = [:]) -> [PaneLabel] {
    let folded = utterance.lowercased()
    return panes.filter { pane in
        var labels = [pane.title, pane.window, pane.command]
        labels.append(contentsOf: agents[pane.id] ?? [])
        let usable = labels.filter { !$0.isEmpty }
        return usable.contains { folded.contains($0.lowercased()) }
    }
}

/// Socket of the tmux server `list-panes` just talked to. Does not start a server:
/// called only after list-panes has already succeeded.
func tmuxSocketPath() -> String? {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    p.arguments = ["tmux", "display-message", "-p", "#{socket_path}"]
    let out = Pipe()
    p.standardOutput = out
    p.standardError = Pipe()
    do { try p.run() } catch { return nil }
    p.waitUntilExit()
    guard p.terminationStatus == 0,
          let text = String(data: out.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) else { return nil }
    let path = text.trimmingCharacters(in: .whitespacesAndNewlines)
    return path.isEmpty ? nil : path
}

/// Walk `/proc/*/environ` for `TMUX_PANE=%N` on this tmux socket. The process command is `comm`
/// and argv0's basename, each cut to 16 characters, and only the allowlist is kept.
/// Returns pane id -> agent names. Empty when `/proc` is absent (Mac).
func discoverAgentNames() -> [String: [String]] {
    let proc = "/proc"
    var isDir: ObjCBool = false
    guard FileManager.default.fileExists(atPath: proc, isDirectory: &isDir), isDir.boolValue else { return [:] }
    guard let socket = tmuxSocketPath() else { return [:] }
    guard let pids = try? FileManager.default.contentsOfDirectory(atPath: proc) else { return [:] }
    var found: [String: Set<String>] = [:]
    for pid in pids where pid.allSatisfy({ $0.isNumber }) {
        let dir = "\(proc)/\(pid)"
        guard let envData = try? Data(contentsOf: URL(fileURLWithPath: "\(dir)/environ")) else { continue }
        var tmux = ""
        var pane = ""
        var field = Data()
        func take(_ data: Data) {
            guard let text = String(data: data, encoding: .utf8), let eq = text.firstIndex(of: "=") else { return }
            let key = String(text[..<eq])
            let value = String(text[text.index(after: eq)...])
            if key == "TMUX" { tmux = value }
            else if key == "TMUX_PANE" { pane = value }
        }
        for byte in envData {
            if byte == 0 {
                take(field)
                field.removeAll(keepingCapacity: true)
            } else {
                field.append(byte)
            }
        }
        if !field.isEmpty { take(field) }
        guard !tmux.isEmpty, !pane.isEmpty else { continue }
        guard tmux.split(separator: ",", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) == socket else { continue }
        guard isPaneID(pane) else { continue }
        let cmdline = (try? Data(contentsOf: URL(fileURLWithPath: "\(dir)/cmdline"))) ?? Data()
        let parts = cmdline.split(separator: 0).compactMap { String(data: Data($0), encoding: .utf8) }
        let joined = parts.joined(separator: " ")
        if agentDaemonMarkers.contains(where: { joined.contains($0) }) { continue }
        let comm = (try? String(contentsOfFile: "\(dir)/comm", encoding: .utf8))?
            .replacingOccurrences(of: "\0", with: "")
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let argv0 = parts.first.map { URL(fileURLWithPath: $0).lastPathComponent } ?? ""
        var names = Set<String>()
        for raw in [comm, argv0] {
            if let canon = canonicalAgentName(raw) { names.insert(canon) }
        }
        guard !names.isEmpty else { continue }
        var namesForPane = found[pane] ?? Set<String>()
        namesForPane.formUnion(names)
        found[pane] = namesForPane
    }
    return found.mapValues { $0.sorted() }
}

/// Does not start a tmux server. Failure or no server returns nil and sends nothing.
func fetchPanes() -> [PaneLabel]? {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    p.arguments = ["tmux", "list-panes", "-a", "-F",
                   "#{pane_id}\t#{window_name}\t#{pane_title}\t#{pane_current_command}"]
    let out = Pipe()
    let err = Pipe()
    p.standardOutput = out
    p.standardError = err
    do { try p.run() } catch {
        log("tmux: list-panes failed to start: \(error)")
        return nil
    }
    p.waitUntilExit()
    let errText = (String(data: err.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? "")
        .trimmingCharacters(in: .whitespacesAndNewlines)
    if p.terminationStatus != 0 {
        let detail = errText.isEmpty ? "" : ": \(errText)"
        log("tmux: list-panes exited \(p.terminationStatus)\(detail); nothing sent")
        return nil
    }
    guard let text = String(data: out.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8),
          let panes = parsePanes(text) else {
        log("tmux: list-panes output was not a four-field catalog; nothing sent")
        return nil
    }
    return panes
}

// MARK: jev correction

/// Jev's answer to "which listed pane does the speaker address". `pane` nil is the "none" choice.
struct JevPick: Equatable {
    var pane: String?
    var confidence: Double
}

/// Probe (scripts/pane_jev_probe.py, 8 cases): wrong answers came back at 0.49 and 0.52, right ones at
/// 0.80 to 1.00. Recalibrate from the `tmux:` log lines once real dictations accumulate.
let jevConfidenceFloor = 0.8

/// Same table on Windows (PaneRoute.cs) and in tests/parity/fixtures/pane_route.json.
/// Without a confident pick the PR #4 rule stands: send iff exactly one label hit.
func decideRoute(_ hits: [PaneLabel], _ pick: JevPick?) -> (pane: String?, reason: String) {
    let confident = pick.map { $0.confidence >= jevConfidenceFloor } ?? false
    switch hits.count {
    case 0:
        if confident, let suggested = pick?.pane { return (nil, "no pane matched; jev suggests \(suggested)") }
        return (nil, "no pane matched")
    case 1:
        if confident, pick?.pane != hits[0].id { return (nil, "jev rejected") }
        return (hits[0].id, "unique hit")
    default:
        if confident, let chosen = pick?.pane, hits.contains(where: { $0.id == chosen }) { return (chosen, "jev narrowed") }
        return (nil, "\(hits.count) panes matched")
    }
}

/// Question shape shared with scripts/pane_jev_probe.py: criteria keyed by pane id plus "none".
func jevRequestBody(_ dictation: String, _ panes: [PaneLabel], agents: [String: [String]]) -> Data? {
    var criteria: [String: Any] = [:]
    for pane in panes {
        let row: [String: Any] = ["window": pane.window, "title": pane.title, "command": pane.command, "agents": agents[pane.id] ?? []]
        criteria[pane.id] = row
    }
    criteria["none"] = "The dictation does not name or clearly address any one of the listed panes."
    let question: [String: Any] = [
        "type": "choice",
        "instructions": "`dictation` is speech-to-text output, so pane names may be misheard or written in katakana. "
            + "Which listed tmux pane does the speaker address by name (window, title, command, or agent)?",
        "criteria": criteria,
    ]
    let body: [String: Any] = ["state": ["dictation": dictation], "model": "jev-latest", "questions": ["pane": question]]
    return try? JSONSerialization.data(withJSONObject: body)
}

/// `answers.pane` must be a choice answer naming a catalog id or "none"; anything else is nil (treated as no pick).
func parseJevPick(_ data: Data, catalog: [String]) -> JevPick? {
    guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let answers = root["answers"] as? [String: Any],
          let answer = answers["pane"] as? [String: Any],
          answer["type"] as? String == "choice",
          let choice = answer["choice"] as? String,
          let confidence = answer["confidence"] as? Double else { return nil }
    if choice == "none" { return JevPick(pane: nil, confidence: confidence) }
    guard catalog.contains(choice) else { return nil }
    return JevPick(pane: choice, confidence: confidence)
}

enum JevOutcome {
    case off
    case error
    case pick(JevPick)
}

/// Key from TYPESAFE_API_KEY, else JEV_API_KEY. No key means no request. The key and the dictation are never logged.
func jevPick(_ dictation: String, _ panes: [PaneLabel], agents: [String: [String]]) async -> JevOutcome {
    let env = ProcessInfo.processInfo.environment
    guard let key = ["TYPESAFE_API_KEY", "JEV_API_KEY"].compactMap({ env[$0] }).first(where: { !$0.isEmpty }) else { return .off }
    guard let body = jevRequestBody(dictation, panes, agents: agents) else { return .error }
    var request = URLRequest(url: URL(string: "https://api.typesafe.ai/v1/systemone")!)
    request.httpMethod = "POST"
    request.timeoutInterval = 5
    request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
    request.setValue("application/json", forHTTPHeaderField: "Content-Type")
    request.httpBody = body
    let reply: (Data, URLResponse)
    do {
        reply = try await URLSession.shared.data(for: request)
    } catch {
        log("jev: request failed or timed out")
        return .error
    }
    let (data, response) = reply
    let status = (response as? HTTPURLResponse)?.statusCode ?? 0
    guard status == 200 else {
        log("jev: http \(status)")
        return .error
    }
    guard let pick = parseJevPick(data, catalog: panes.map { $0.id }) else {
        log("jev: answer was not a catalog choice")
        return .error
    }
    return .pick(pick)
}

// MARK: send body

/// Quote pairs tried in order: every 「…」 first, then every 『…』.
let sendBodyQuotes: [(open: Character, close: Character)] = [("「", "」"), ("『", "』")]

/// The text that goes to the pane: the interior of the first balanced, non-empty `「…」`,
/// else of the first balanced, non-empty `『…』`, trimmed. nil when there is none.
/// Callers must not fall back to the full dictation on nil. Same rule as
/// `PaneRoute.ExtractSendBody` (Windows) and the `extract` rows of tests/parity/fixtures/pane_route.json.
func extractSendBody(_ dictation: String) -> String? {
    let chars = Array(dictation)
    for quote in sendBodyQuotes {
        var depth = 0
        var start = 0
        for (index, ch) in chars.enumerated() {
            if ch == quote.open {
                if depth == 0 { start = index + 1 }
                depth += 1
            } else if ch == quote.close, depth > 0 {
                depth -= 1
                if depth == 0 {
                    let body = String(chars[start..<index]).trimmingCharacters(in: .whitespacesAndNewlines)
                    if !body.isEmpty { return body }
                }
            }
        }
    }
    return nil
}

/// Trailing cues stripped before building unquoted body candidates (longest first).
let sendBodyCues: [String] = [
    "を送信して", "と送信して", "を入力して", "と入力して",
    "を送って", "と送って", "に送って", "を貼って", "と貼って",
].sorted { $0.count > $1.count }

/// Routing particles looked for on the cue-stripped string (longest first when scanning ends).
let sendBodyParticles: [String] = ["の pane に", "に", "へ"]

/// Deterministic unquoted body candidates. Never includes the full (trimmed) dictation.
/// Labels are hit / catalog strings present in the dictation. No Jev. Same rule as
/// `PaneRoute.SendBodyCandidates` (Windows) and the `candidates` rows of pane_route.json.
func sendBodyCandidates(_ dictation: String, labels: [String]) -> [String] {
    let full = dictation.trimmingCharacters(in: .whitespacesAndNewlines)
    guard !full.isEmpty else { return [] }
    var out: [String] = []
    var seen = Set<String>()
    func add(_ raw: String) {
        let t = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty, t != full, !seen.contains(t) else { return }
        seen.insert(t)
        out.append(t)
    }

    var bestRange: Range<String.Index>?
    for label in labels where !label.isEmpty {
        guard let range = dictation.range(of: label, options: .caseInsensitive) else { continue }
        if let best = bestRange {
            if range.lowerBound < best.lowerBound
                || (range.lowerBound == best.lowerBound && range.upperBound > best.upperBound)
            {
                bestRange = range
            }
        } else {
            bestRange = range
        }
    }
    if let range = bestRange {
        add(String(dictation[range.upperBound...]))
    }

    var cueStripped: String?
    for cue in sendBodyCues where full.hasSuffix(cue) {
        let cut = String(full.dropLast(cue.count)).trimmingCharacters(in: .whitespacesAndNewlines)
        cueStripped = cut
        add(cut)
        break
    }

    if let stripped = cueStripped {
        var bestEnd: String.Index?
        for particle in sendBodyParticles {
            var search = stripped.startIndex
            while search < stripped.endIndex,
                  let range = stripped.range(of: particle, range: search..<stripped.endIndex)
            {
                if bestEnd == nil || range.upperBound > bestEnd! {
                    bestEnd = range.upperBound
                }
                search = stripped.index(after: range.lowerBound)
            }
        }
        if let end = bestEnd, end > stripped.startIndex {
            add(String(stripped[end...]))
        }
    }

    return out
}

/// After decideRoute: a chosen pane with no body is skipped (SuppressFallback / SkippedNoBody).
func requireSendBody(pane: String?, reason: String, body: String?) -> (pane: String?, reason: String, suppressFallback: Bool) {
    if let pane, body == nil {
        return (nil, "no send body for \(pane); not sending", true)
    }
    return (pane, reason, false)
}

func routeDisposition(pane: String?, suppressFallback: Bool, sent: Bool) -> RouteDisposition {
    if sent { return .sent }
    if suppressFallback { return .skippedNoBody }
    if pane != nil { return .sendFailed }
    return .notRouted
}

/// Waits for tmux send-keys. false when start fails or exit status is non-zero.
func sendKeysToPane(_ id: String, _ body: String) -> Bool {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    p.arguments = ["tmux", "send-keys", "-t", id, "-l", "--", body]
    do { try p.run() } catch {
        log("tmux: send-keys failed to start: \(error)")
        return false
    }
    p.waitUntilExit()
    if p.terminationStatus != 0 {
        log("tmux: send-keys exited \(p.terminationStatus)")
        return false
    }
    return true
}

/// One log line per dictation, e.g. `tmux: hits=2 jev=%2@0.87 -> send %2 (jev narrowed)`.
/// Returns the same disposition Windows `TmuxPaneRouter.RouteAsync` uses for paste Decide.
func routeDictation(_ text: String) async -> RouteDisposition {
    guard let panes = fetchPanes() else { return .notRouted }
    let agents = discoverAgentNames()
    let hits = matchingPanes(text, panes, agents: agents)
    let pick: JevPick?
    let jevField: String
    switch await jevPick(text, panes, agents: agents) {
    case .off:
        pick = nil
        jevField = "off"
    case .error:
        pick = nil
        jevField = "error"
    case .pick(let found):
        pick = found
        jevField = "\(found.pane ?? "none")@\(String(format: "%.2f", found.confidence))"
    }
    var decision = decideRoute(hits, pick)
    if let id = decision.pane, !isPaneID(id) {
        decision = (nil, "pane id rejected")
    }
    let body = extractSendBody(text)
    let required = requireSendBody(pane: decision.pane, reason: decision.reason, body: body)
    let prefix = "tmux: hits=\(hits.count) jev=\(jevField)"
    if let id = required.pane, let body {
        log("\(prefix) -> send \(id) (\(required.reason))")
        let sent = sendKeysToPane(id, body)
        return routeDisposition(pane: id, suppressFallback: false, sent: sent)
    }
    log("\(prefix) -> skip (\(required.reason))")
    return routeDisposition(pane: required.pane, suppressFallback: required.suppressFallback, sent: false)
}
