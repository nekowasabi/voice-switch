// Pure Superwhisper mode helpers shared with Windows SuperwhisperModes.cs.
// Resolve / Decide need no AppKit; MacApp wires switch + paste around them.
import Foundation
import CoreFoundation

/// How the pane router disposed of a dictation. Same semantics as Windows `RouteDisposition`.
/// SkippedNoBody: pane chosen but no extractable body — must not paste the full text.
/// SendFailed: pane+body known but send-keys failed — paste `RouteResult.body` only when mode is set.
enum RouteDisposition: Equatable {
    case sent
    case skippedNoBody
    case sendFailed
    case notRouted
}

/// What the router did, plus for SendFailed the exact body send-keys tried (Windows `RouteResult`).
/// The handoff pastes that body instead of re-extracting: an unquoted (body Jev) body would otherwise be lost.
struct RouteResult: Equatable {
    let disposition: RouteDisposition
    let body: String?

    static let sent = RouteResult(disposition: .sent, body: nil)
    static let skippedNoBody = RouteResult(disposition: .skippedNoBody, body: nil)
    static let notRouted = RouteResult(disposition: .notRouted, body: nil)
    static func sendFailed(_ body: String?) -> RouteResult { RouteResult(disposition: .sendFailed, body: body) }
}

enum DictationDelivery: Equatable {
    case pane
    case paste
    case superwhisper
}

/// A key match anywhere wins over a name match (Windows SuperwhisperModes.ResolveKey).
func resolveSuperwhisperModeKey(_ wanted: String, modeJsons: [String]) -> String? {
    let modes: [(key: String, name: String?)] = modeJsons.compactMap { json in
        guard let key = jsonStringField(json, "key"), !key.isEmpty else { return nil }
        return (key, jsonStringField(json, "name"))
    }
    if let hit = modes.first(where: { $0.key.caseInsensitiveCompare(wanted) == .orderedSame }) {
        return hit.key
    }
    return modes.first(where: { ($0.name ?? "").caseInsensitiveCompare(wanted) == .orderedSame })?.key
}

func activeSuperwhisperMode(_ preferencesJson: String?) -> String? {
    guard let preferencesJson else { return nil }
    guard let value = jsonStringField(preferencesJson, "activeMode"), !value.isEmpty else { return nil }
    return value
}

/// With a no-auto-paste mode in use, a dictation no pane took would otherwise land nowhere.
/// SkippedNoBody must not Paste (that would be a full-text fallback after extract failure).
/// SendFailed pastes via `.paste`; handoff uses `RouteResult.body` (the body send-keys tried), not the full text.
func decideDictationDelivery(modeRequested: Bool, route: RouteDisposition) -> DictationDelivery {
    switch route {
    case .sent, .skippedNoBody:
        return .pane
    case .sendFailed, .notRouted:
        return modeRequested ? .paste : .superwhisper
    }
}

func jsonStringField(_ json: String, _ name: String) -> String? {
    guard let data = json.data(using: .utf8),
          let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let value = root[name] as? String else { return nil }
    return value
}

func superwhisperModeUsesLLM(_ wanted: String?, modeJsons: [String]) -> Bool {
    guard let wanted, let key = resolveSuperwhisperModeKey(wanted, modeJsons: modeJsons) else { return false }
    return modeJsons.contains { json in
        jsonStringField(json, "key") == key && !(jsonStringField(json, "languageModelID") ?? "").isEmpty
    }
}

func superwhisperAutoPasteDisabled(_ wanted: String?, modeJsons: [String]) -> Bool {
    guard let wanted, let key = resolveSuperwhisperModeKey(wanted, modeJsons: modeJsons) else { return false }
    let matching = modeJsons.compactMap { json -> [String: Any]? in
        guard let object = try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any],
              object["key"] as? String == key else { return nil }
        return object
    }
    guard matching.count == 1, let value = matching[0]["autoPaste"] as? NSNumber,
          CFGetTypeID(value) == CFBooleanGetTypeID() else { return false }
    return !value.boolValue
}

/// Polls superwhisper's recordings folder for the run that started at or after `since` (unix seconds).
/// "" means the run finished without words; nil means no run finished within 30 s.
/// waitForLLM: superwhisper writes `result` first and `llmResult` after its language model; it pastes per the mode active
/// at that second write, so a temporary mode must stay until then (restoring at the first write pasted automatic recordings).
func awaitResult(in dir: String, since: Int, excluding previousRuns: Set<String> = [], waitForLLM: Bool = false) async -> String? {
    var early: String?
    for _ in 0 ..< 300 {
        for (_, run) in superwhisperRuns(in: dir, since: since, excluding: previousRuns) {
            switch run {
            case .unjudged: continue
            case .heardNothing: return ""
            case let .polished(text): return text
            case let .transcribed(text):
                guard waitForLLM else { return text }
                early = text
            }
        }
        try? await Task.sleep(for: .milliseconds(100))
    }
    return early
}

/// One superwhisper run as its meta.json shows it. Superwhisper first writes a placeholder with duration and
/// processingTime 0 and an empty result; only a filled duration says it has judged the audio.
enum SuperwhisperRun: Equatable {
    case unjudged
    case heardNothing
    case transcribed(String)
    case polished(String)

    init(meta: [String: Any]) {
        let judged = ((meta["duration"] ?? meta["processingTime"]) as? NSNumber)?.doubleValue ?? 0 > 0
        let result = meta["result"] as? String ?? ""
        let llm = meta["llmResult"] as? String ?? ""
        if !judged { self = .unjudged }
        else if !llm.isEmpty || (meta["languageModelProcessingTime"] != nil && !result.isEmpty) { self = .polished(llm.isEmpty ? result : llm) }
        else if result.isEmpty { self = .heardNothing }
        else { self = .transcribed(result) }
    }

    /// Still able to paste: unjudged, or transcribed by a mode that has a language model still to run.
    func inFlight(meta: [String: Any]) -> Bool {
        switch self {
        case .unjudged: true
        case .transcribed: meta["languageModelKey"] != nil
        case .heardNothing, .polished: false
        }
    }
}

/// Newest first.
func superwhisperRuns(in dir: String, since: Int, excluding previousRuns: Set<String> = []) -> [(meta: [String: Any], run: SuperwhisperRun)] {
    let names = (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? []
    return names.sorted().reversed().compactMap { name in
        guard !previousRuns.contains(name), let t = Int(name), t >= since,
              let data = FileManager.default.contents(atPath: "\(dir)/\(name)/meta.json"),
              let meta = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        return (meta, SuperwhisperRun(meta: meta))
    }
}

/// A handoff that timed out kept its temporary mode; restoring it while that run still works lets the run paste.
/// Waits up to 30 s for runs of the last `window` seconds to finish; true when none is left in flight.
func awaitRunsSettled(in dir: String, now: Int = Int(Date().timeIntervalSince1970), window: Int = 180) async -> Bool {
    for _ in 0 ..< 300 {
        if !superwhisperRuns(in: dir, since: now - window).contains(where: { $0.run.inFlight(meta: $0.meta) }) { return true }
        try? await Task.sleep(for: .milliseconds(100))
    }
    return false
}
