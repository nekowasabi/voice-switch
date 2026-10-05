// tmux pane route: the dictation string goes to one tmux pane only when a closed catalog label hits.
import Foundation

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

/// Unique label hit sends the dictation text literally. Miss, ambiguity, or a bad id sends nothing.
func routeDictation(_ text: String) {
    guard let panes = fetchPanes() else { return }
    let agents = discoverAgentNames()
    let hits = matchingPanes(text, panes, agents: agents)
    guard hits.count == 1 else {
        log(hits.isEmpty ? "tmux: no pane matched; nothing sent" : "tmux: \(hits.count) panes matched; nothing sent")
        return
    }
    let id = hits[0].id
    guard isPaneID(id) else {
        log("tmux: pane id rejected; nothing sent")
        return
    }
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    p.arguments = ["tmux", "send-keys", "-t", id, "-l", "--", text]
    p.terminationHandler = { proc in
        if proc.terminationStatus != 0 { log("tmux: send-keys exited \(proc.terminationStatus)") }
    }
    do { try p.run() } catch { log("tmux: send-keys failed to start: \(error)") }
}
