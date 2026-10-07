// Pure Superwhisper mode helpers shared with Windows SuperwhisperModes.cs.
// Resolve / Decide need no AppKit; MacApp wires switch + paste around them.
import Foundation

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
