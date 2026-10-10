// Shared config types (macOS + Windows).
import Foundation

func log(_ s: String) { print("\(Date().formatted(.iso8601)) \(s)") }

struct Config: Decodable {
    var wakeWords: [String]
    var locale: String?
    var command: String
    /// macOS-only URL overrides; Windows continues to use command / stopCommand.
    var macOS: MacOSActions?
    var maxSeconds: Double?
    var hangoverMs: Int?
    var prerollMs: Int?
    var minSpeechMs: Int?
    var vadRatio: Float?
    var vadMinRMS: Float?
    /// Experimental: probe the open utterance with STT every this many ms of speech (and at short internal gaps),
    /// firing a wake as soon as the transcript starts with a wake word, before hangoverMs. Absent turns it off.
    var earlyWakeMs: Int?
    /// Bundle IDs / process names whose microphone use blocks firing.
    var skipWhileMicInUseBy: [String]?
    /// Said on their own, these end the current input.
    var stopWords: [String]?
    /// Runs when a stop word is heard while a skipWhileMicInUseBy app is recording.
    var stopCommand: String?
    /// Absent turns one-breath dictation off.
    var dictation: DictationConfig?
    /// Parsed for cross-platform config compatibility; macOS does not process this lane.
    var noiseReduction: NoiseReductionOptions?
    /// Absent disables macrowhisper CLI hooks.
    var macrowhisper: MacrowhisperConfig?

    init(wakeWords: [String], locale: String? = nil, command: String,
         maxSeconds: Double? = nil, hangoverMs: Int? = nil, prerollMs: Int? = nil,
         minSpeechMs: Int? = nil, vadRatio: Float? = nil, vadMinRMS: Float? = nil,
         skipWhileMicInUseBy: [String]? = nil, stopWords: [String]? = nil,
         stopCommand: String? = nil, dictation: DictationConfig? = nil,
         noiseReduction: NoiseReductionOptions? = nil) {
        self.wakeWords = wakeWords
        self.locale = locale
        self.command = command
        self.maxSeconds = maxSeconds
        self.hangoverMs = hangoverMs
        self.prerollMs = prerollMs
        self.minSpeechMs = minSpeechMs
        self.vadRatio = vadRatio
        self.vadMinRMS = vadMinRMS
        self.skipWhileMicInUseBy = skipWhileMicInUseBy
        self.stopWords = stopWords
        self.stopCommand = stopCommand
        self.dictation = dictation
        self.noiseReduction = noiseReduction
    }
}

struct NoiseReductionOptions: Decodable {
    var mode: String?
    var maxAttenuationDb: Double?

    init(mode: String? = nil, maxAttenuationDb: Double? = nil) {
        self.mode = mode
        self.maxAttenuationDb = maxAttenuationDb
    }
}

/// Optional hooks into the macrowhisper CLI (https://github.com/ognistik/macrowhisper).
/// Same surface the Alfred workflow uses: schedule / auto-return / set-active, then Superwhisper.
/// scheduleAction and autoReturn are mutually exclusive in macrowhisper (scheduling cancels auto-return and vice versa).
struct MacrowhisperConfig: Decodable {
    /// Absolute path or bare name on PATH. Default: "macrowhisper".
    var bin: String?
    /// `macrowhisper --schedule-action <name>` before starting Superwhisper (one-shot for the next recording).
    var scheduleAction: String?
    /// `macrowhisper --auto-return true` before starting Superwhisper (one-shot Return after insert).
    var autoReturn: Bool?
    /// `macrowhisper --action <name>` — set the persistent fallback active action (not one-shot).
    var activeAction: String?
    /// Optional `superwhisper://mode?key=` before/with record (Alfred's dictateMode).
    var modeKey: String?
    /// Also run the same prepare step before one-breath dictation handoff to Superwhisper. Default true when any hook is set.
    var onDictationHandoff: Bool?
}

struct DictationConfig: Decodable {
    var recordingsDir: String?
    var endSilenceMs: Int?
    var maxSeconds: Double?
    var excludeBundleIDs: [String]?
    /// How long a lone wake word waits for the text before giving up.
    var startTimeoutMs: Int?
    /// Windows only (executable names); one config file can serve both platforms, and macOS ignores it.
    var excludeProcessNames: [String]?
    /// Superwhisper mode key or display name for voice-switch dictations (Mac + Windows).
    /// Unset keeps auto-paste behavior; set switches mode around handoff and pastes when not routed.
    var superwhisperMode: String?

    init(recordingsDir: String? = nil, endSilenceMs: Int? = nil, maxSeconds: Double? = nil,
         excludeBundleIDs: [String]? = nil, startTimeoutMs: Int? = nil) {
        self.recordingsDir = recordingsDir
        self.endSilenceMs = endSilenceMs
        self.maxSeconds = maxSeconds
        self.excludeBundleIDs = excludeBundleIDs
        self.startTimeoutMs = startTimeoutMs
    }
}


/// Load-time guardrails for timing fields that are easy to mistype 10x. No clamp — warn only.
/// Thresholds match Windows DictationTimingGuard (endSilenceMs > 10000, startTimeoutMs > 15000).
enum DictationTimingGuard {
    static func shouldWarnHighEndSilence(_ ms: Int?) -> Bool {
        guard let ms else { return false }
        return ms > 10000
    }

    static func shouldWarnHighStartTimeout(_ ms: Int?) -> Bool {
        guard let ms else { return false }
        return ms > 15000
    }

    static func warnIfNeeded(_ cfg: Config) {
        if shouldWarnHighEndSilence(cfg.dictation?.endSilenceMs),
           let ms = cfg.dictation?.endSilenceMs {
            log("dictation timing warn: endSilenceMs unusually high (\(ms)); example is 2400")
        }
        if shouldWarnHighStartTimeout(cfg.dictation?.startTimeoutMs),
           let ms = cfg.dictation?.startTimeoutMs {
            log("dictation timing warn: startTimeoutMs unusually high (\(ms)); example is 3000")
        }
    }
}

final class ConfigFile {
    let path: String
    private var mtime: Date?
    private(set) var cfg: Config

    init(path: String) throws {
        self.path = path
        cfg = try Self.load(path)
        mtime = Self.modified(path)
        DictationTimingGuard.warnIfNeeded(cfg)
    }

    // Checked between utterances so wake words can be edited without a restart.
    func reloadIfChanged() {
        let m = Self.modified(path)
        guard m != mtime else { return }
        mtime = m
        do {
            cfg = try Self.load(path)
            log("config reloaded: \(cfg.wakeWords)")
            DictationTimingGuard.warnIfNeeded(cfg)
        } catch {
            log("config reload failed, keeping previous: \(error)")
        }
    }

    private static func load(_ path: String) throws -> Config {
        try JSONDecoder().decode(Config.self, from: Data(contentsOf: URL(fileURLWithPath: path)))
    }

    private static func modified(_ path: String) -> Date? {
        try? FileManager.default.attributesOfItem(atPath: path)[.modificationDate] as? Date
    }
}

/// URL actions are decoded at the config boundary; execution never repairs or shell-quotes them.
struct MacOSActions: Decodable {
    var wakeURL: ActionURL?
    var stopURL: ActionURL?
    var actions: [WakeAction]?
}

/// A wake phrase that opens its own URL with the dictated text appended as `input=`.
struct WakeAction: Decodable {
    let name: String
    let wakeWords: [String]
    let url: ActionURL
    /// Superwhisper mode (key or name) used for this action's dictation, typically one with auto-paste off
    /// so the text reaches only the URL. Absent falls back to `dictation.superwhisperMode`.
    let superwhisperMode: String?

    private enum CodingKeys: String, CodingKey { case name, wakeWords, url, superwhisperMode }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        name = try c.decode(String.self, forKey: .name)
        wakeWords = try c.decode([String].self, forKey: .wakeWords)
        url = try c.decode(ActionURL.self, forKey: .url)
        superwhisperMode = try c.decodeIfPresent(String.self, forKey: .superwhisperMode)
        guard !wakeWords.isEmpty else {
            throw DecodingError.dataCorruptedError(forKey: .wakeWords, in: c,
                debugDescription: "Wake action needs at least one wake word.")
        }
    }

    /// `open` arguments with the URL's `input` query replaced by `input` (one item, percent-encoded).
    func openArguments(input: String) -> [String] {
        guard var parts = URLComponents(string: url.rawValue) else { return url.openArguments }
        let unreserved = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
        let value = input.addingPercentEncoding(withAllowedCharacters: unreserved) ?? ""
        var items = (parts.percentEncodedQueryItems ?? []).filter { $0.name != "input" }
        items.append(URLQueryItem(name: "input", value: value))
        parts.percentEncodedQueryItems = items
        return ["-g", "--", parts.string ?? url.rawValue]
    }
}

struct ActionURL: Decodable {
    let rawValue: String
    var openArguments: [String] { ["-g", "--", rawValue] }

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let raw = try container.decode(String.self)
        func invalid() -> DecodingError {
            .dataCorruptedError(in: container, debugDescription:
                "URL action must be an absolute application URL (for example superwhisper://record); " +
                "encode spaces as %20, use valid percent escapes, and do not use file/data/javascript URLs.")
        }
        guard !raw.isEmpty,
              raw.rangeOfCharacter(from: .whitespacesAndNewlines.union(.controlCharacters)) == nil,
              let colon = raw.firstIndex(of: ":") else { throw invalid() }
        let scheme = String(raw[..<colon])
        guard scheme.range(of: "^[A-Za-z][A-Za-z0-9+.-]*$", options: .regularExpression) != nil,
              !["file", "data", "javascript"].contains(scheme.lowercased()) else { throw invalid() }
        let destination = raw[raw.index(after: colon)...]
        guard !destination.isEmpty, destination != "//" else { throw invalid() }
        let bytes = Array(raw.utf8)
        func hex(_ byte: UInt8) -> Bool {
            (48...57).contains(byte) || (65...70).contains(byte) || (97...102).contains(byte)
        }
        for i in bytes.indices where bytes[i] == 37 {
            guard i + 2 < bytes.count, hex(bytes[i + 1]), hex(bytes[i + 2]) else { throw invalid() }
        }
        guard let parsed = URLComponents(string: raw), parsed.scheme != nil else { throw invalid() }
        rawValue = raw
    }
}
