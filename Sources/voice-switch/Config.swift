// Shared config types (macOS + Windows).
import Foundation

func log(_ s: String) { print("\(Date().formatted(.iso8601)) \(s)") }

struct Config: Decodable {
    var wakeWords: [String]
    var locale: String?
    var command: String
    var maxSeconds: Double?
    var hangoverMs: Int?
    var prerollMs: Int?
    var minSpeechMs: Int?
    var vadRatio: Float?
    var vadMinRMS: Float?
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

struct DictationConfig: Decodable {
    var recordingsDir: String?
    var endSilenceMs: Int?
    var maxSeconds: Double?
    var excludeBundleIDs: [String]?
    /// How long a lone wake word waits for the text before giving up.
    var startTimeoutMs: Int?
    /// Windows only (executable names); one config file can serve both platforms, and macOS ignores it.
    var excludeProcessNames: [String]?

    init(recordingsDir: String? = nil, endSilenceMs: Int? = nil, maxSeconds: Double? = nil,
         excludeBundleIDs: [String]? = nil, startTimeoutMs: Int? = nil) {
        self.recordingsDir = recordingsDir
        self.endSilenceMs = endSilenceMs
        self.maxSeconds = maxSeconds
        self.excludeBundleIDs = excludeBundleIDs
        self.startTimeoutMs = startTimeoutMs
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
    }

    // Checked between utterances so wake words can be edited without a restart.
    func reloadIfChanged() {
        let m = Self.modified(path)
        guard m != mtime else { return }
        mtime = m
        do {
            cfg = try Self.load(path)
            log("config reloaded: \(cfg.wakeWords)")
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
