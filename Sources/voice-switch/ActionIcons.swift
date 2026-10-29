import Foundation

// One row per product whose wake action should replace the mic with an image.
// A new icon is another row, or an action's "icon" stem plus a png of that name. No new HUD phase.
struct ActionIconRule {
    var icon: String
    var names: [String]
    var scheme: String?
    var host: String?
    var path: String?
}

enum ActionIcons {
    static let builtin: [ActionIconRule] = [
        ActionIconRule(icon: "grok-bot", names: ["grok", "grokbot"], scheme: "grokbot", host: "app", path: "/v1/prompt"),
    ]

    static func isStem(_ value: String) -> Bool {
        guard let first = value.first, first.isASCII && (first.isLetter || first.isNumber) else { return false }
        return value.count <= 41 && value.allSatisfy { ch in
            ch.isASCII && (ch.isLetter || ch.isNumber || ch == "." || ch == "_" || ch == "-")
        }
    }

    static func resolve(name: String, url: String, configured: String?) -> String? {
        if let configured, isStem(configured) { return configured }
        let compact = name.lowercased().replacingOccurrences(of: " ", with: "").replacingOccurrences(of: "-", with: "").replacingOccurrences(of: "_", with: "")
        for rule in builtin {
            if !compact.isEmpty, rule.names.contains(where: { $0.caseInsensitiveCompare(compact) == .orderedSame }) { return rule.icon }
            if matches(url, rule) { return rule.icon }
        }
        return nil
    }

    private static func matches(_ url: String, _ rule: ActionIconRule) -> Bool {
        guard let scheme = rule.scheme, let parts = URLComponents(string: url),
              parts.scheme?.lowercased() == scheme.lowercased() else { return false }
        if let host = rule.host, parts.host?.lowercased() != host.lowercased() { return false }
        if let path = rule.path, parts.path != path { return false }
        return true
    }
}
