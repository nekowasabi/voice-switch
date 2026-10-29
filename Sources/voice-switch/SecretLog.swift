import Foundation

// When an environment variable is missing, a shell default (`${NAME:-secret}`) or the expanded
// value of a secret-named variable must not reach the log. The variable name is the safe form.
enum SecretLog {
    private static let brace = try! NSRegularExpression(pattern: "\\$\\{([A-Za-z_][A-Za-z0-9_]*)((?::?[+-][^}]*)?)\\}")
    private static let percent = try! NSRegularExpression(pattern: "%([A-Za-z_][A-Za-z0-9_]*)%")
    private static let secretName = try! NSRegularExpression(pattern: "password|passwd|secret|token|api_key|apikey|credential", options: .caseInsensitive)

    static func redact(_ text: String) -> String {
        redact(text, lookup: { ProcessInfo.processInfo.environment[$0] }, variables: ProcessInfo.processInfo.environment)
    }

    static func redact(_ text: String, lookup: (String) -> String?, variables: [String: String]) -> String {
        var text = replace(brace, in: text) { match, whole, ns in
            let name = ns.substring(with: match.range(at: 1))
            let suffix = match.range(at: 2)
            let hasDefault = suffix.location != NSNotFound && suffix.length > 0
            return hasDefault || (lookup(name) ?? "").isEmpty ? "$\(name)" : whole
        }
        text = replace(percent, in: text) { match, whole, ns in
            let name = ns.substring(with: match.range(at: 1))
            return (lookup(name) ?? "").isEmpty ? "$\(name)" : whole
        }
        let pairs = variables
            .filter { secretName.firstMatch(in: $0.key, range: NSRange(location: 0, length: ($0.key as NSString).length)) != nil && $0.value.count >= 8 }
            .sorted { $0.value.count != $1.value.count ? $0.value.count > $1.value.count : $0.key < $1.key }
        for pair in pairs {
            text = text.replacingOccurrences(of: pair.value, with: "$\(pair.key)")
        }
        return text
    }

    private static func replace(_ pattern: NSRegularExpression, in text: String,
                                _ edit: (NSTextCheckingResult, String, NSString) -> String) -> String {
        let ns = text as NSString
        var out = ""
        var cursor = 0
        for match in pattern.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
            if match.range.location > cursor {
                out += ns.substring(with: NSRange(location: cursor, length: match.range.location - cursor))
            }
            out += edit(match, ns.substring(with: match.range), ns)
            cursor = match.range.location + match.range.length
        }
        if cursor < ns.length { out += ns.substring(from: cursor) }
        return out
    }
}
