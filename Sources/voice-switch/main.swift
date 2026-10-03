// voice-switch: say a wake word on its own -> run a configured command.
//
// mic -> 16 kHz mono -> energy VAD cuts utterances -> short ones go to Apple's
// on-device SpeechTranscriber -> whole-utterance match against config wake words.
// Dictation is long, so saying the word mid-sentence never toggles recording off.
//
// Menu-bar app: pause/resume releases the microphone, so meetings can be made safe.
// On a wake word it runs `command` from the config through /bin/sh.
//
// usage: VoiceSwitch.app                     menu-bar app (config: $VOICE_SWITCH_CONFIG or
//                                            ~/.config/voice-switch/config.json)
//        voice-switch --check a.wav b.wav    feed files through VAD + transcriber, print verdicts
//        voice-switch --simulate a.wav       feed a.wav through the live consumer instead of the mic
//
// One-breath dictation ("音声入力、明日の会議は…"): an utterance that starts with a wake word and goes on
// is recorded until silence, the wake word is cut from the audio, and superwhisper transcribes the file.
// After the result string is read, a unique tmux pane label hit is sent with send-keys -t <pane-id> -l.
import AppKit
import os
import AVFoundation
import Foundation
import ServiceManagement
import Speech

setvbuf(stdout, nil, _IOLBF, 0)

func log(_ s: String) { print("\(Date().formatted(.iso8601)) \(s)") }

// MARK: config

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
    /// Bundle IDs whose microphone use blocks firing (e.g. superwhisper already recording, a meeting app).
    var skipWhileMicInUseBy: [String]?
    /// Said on their own, these end the current input: our dictation, or a recording by a skipWhileMicInUseBy app.
    var stopWords: [String]?
    /// Runs when a stop word is heard while a skipWhileMicInUseBy app is recording.
    var stopCommand: String?
    /// Absent turns one-breath dictation off.
    var dictation: DictationConfig?
}

struct DictationConfig: Decodable {
    var recordingsDir: String?
    var endSilenceMs: Int?
    var maxSeconds: Double?
    var excludeBundleIDs: [String]?
    /// How long a lone wake word waits for the text before giving up.
    var startTimeoutMs: Int?
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

// MARK: segmentation

let rate = 16000.0
let frameLen = 480 // 30 ms
let work = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: rate, channels: 1, interleaved: false)!

func frames(ms: Int) -> Int { max(1, ms * Int(rate) / 1000 / frameLen) }

/// Same state machine as the Python prototype, with an adaptive-floor energy VAD in place of webrtcvad.
struct Segmenter {
    var cfg: Config
    private var floor: Float = 0.001 // typical quiet-room mic level; adapts within a second
    private var ring: [[Float]] = []
    private var utt: [[Float]] = []
    private var silent = 0
    private var skipping = false
    private(set) var lastWasSpeech = false
    /// Off while dictating: quiet syllables classed as non-speech would otherwise pull the floor up toward
    /// the voice itself, so after ~2-3 s of talking the threshold passes the speech and it reads as silence.
    var adaptFloor = true

    enum Event {
        case utterance([Float])
        /// The start of an utterance too long to be a wake word; may be a one-breath dictation.
        case head([Float])
    }

    init(cfg: Config) { self.cfg = cfg }

    private mutating func isSpeech(_ f: [Float]) -> Bool {
        let rms = (f.reduce(0) { $0 + $1 * $1 } / Float(f.count)).squareRoot()
        let speech = rms > max(floor * (cfg.vadRatio ?? 3), cfg.vadMinRMS ?? 0.005)
        // Track the noise floor only while quiet, so speech does not raise it.
        if !speech && adaptFloor { floor = floor * 0.95 + rms * 0.05 }
        return speech
    }

    mutating func push(_ f: [Float]) -> Event? {
        let preroll = frames(ms: cfg.prerollMs ?? 300)
        let hangover = frames(ms: cfg.hangoverMs ?? 300)
        let minFrames = frames(ms: cfg.minSpeechMs ?? 300)
        let maxFrames = Int((cfg.maxSeconds ?? 2.5) * rate) / frameLen
        let speech = isSpeech(f)
        lastWasSpeech = speech
        if utt.isEmpty {
            ring.append(f)
            if ring.count > preroll { ring.removeFirst() }
            if speech { utt = ring; silent = 0 }
            return nil
        }
        silent = speech ? 0 : silent + 1
        // Too long to be a wake word (dictation or steady noise): stop buffering and
        // wait for silence so a tail fragment of the dictation is never judged alone.
        var head: Event?
        if utt.count <= maxFrames + hangover {
            utt.append(f)
        } else if !skipping {
            skipping = true
            // Everything buffered so far, not just maxSeconds, so a dictation continuing from here has no gap.
            head = .head(Array((utt + [f]).joined()))
        }
        guard silent >= hangover else { return head }
        let done = !skipping && utt.count - hangover >= minFrames ? Event.utterance(Array(utt.joined())) : nil
        utt = []; ring = []; skipping = false
        return done ?? head
    }
}

// MARK: transcription

func pcmBuffer(_ samples: [Float]) -> AVAudioPCMBuffer {
    let b = AVAudioPCMBuffer(pcmFormat: work, frameCapacity: AVAudioFrameCount(samples.count))!
    b.frameLength = b.frameCapacity
    samples.withUnsafeBufferPointer { b.floatChannelData![0].update(from: $0.baseAddress!, count: samples.count) }
    return b
}

/// One-shot or streaming conversion. Streaming keeps the resampler state across calls.
func convert(_ input: AVAudioPCMBuffer, with conv: AVAudioConverter, flush: Bool) -> AVAudioPCMBuffer? {
    let ratio = conv.outputFormat.sampleRate / input.format.sampleRate
    let cap = AVAudioFrameCount(Double(input.frameLength) * ratio) + 1024
    guard let out = AVAudioPCMBuffer(pcmFormat: conv.outputFormat, frameCapacity: cap) else { return nil }
    var fed = false
    var err: NSError?
    conv.convert(to: out, error: &err) { _, status in
        if fed { status.pointee = flush ? .endOfStream : .noDataNow; return nil }
        fed = true
        status.pointee = .haveData
        return input
    }
    if let err { log("convert failed: \(err)"); return nil }
    return out
}

let punctuation = Set("、。,.!?！？「」")

func normalize(_ s: String) -> String {
    String(s.filter { !$0.isWhitespace && !punctuation.contains($0) })
}

struct Transcript {
    /// Runs of recognized text (about one character each for ja_JP) with seconds from the start of the audio.
    var runs: [(text: String, start: Double?, end: Double?)]
    var text: String { normalize(runs.map(\.text).joined()) }
}

/// Where to cut the audio so it starts after any leading wake words, plus the normalized text heard after them.
/// nil if the transcript does not start with a wake word or has nothing after it.
func dictationStart(_ t: Transcript, wakeWords: [String]) -> (cutAt: Double, rest: String)? {
    let targets = Set(wakeWords.map(normalize))
    let longest = targets.map(\.count).max() ?? 0
    var next = 0 // first run not yet consumed by a wake word
    var wakeEnd: Double?
    while true {
        var acc = ""
        var matched: Int?
        var i = next
        while i < t.runs.count, acc.count <= longest {
            acc += normalize(t.runs[i].text)
            i += 1
            if targets.contains(acc) { matched = i }
        }
        guard let matched else { break }
        wakeEnd = t.runs[matched - 1].end ?? wakeEnd
        next = matched
    }
    guard next > 0 else { return nil }
    let rest = normalize(t.runs[next...].map(\.text).joined())
    guard !rest.isEmpty else { return nil }
    // The run right after the wake word is often the pause ("、"), so cutting at its start keeps the first syllable whole.
    guard let cutAt = t.runs[next].start ?? wakeEnd.map({ $0 + 0.05 }) else { return nil }
    return (cutAt, rest)
}

func transcribe(_ samples: [Float], locale: Locale) async throws -> Transcript {
    let tr = SpeechTranscriber(locale: locale, transcriptionOptions: [], reportingOptions: [], attributeOptions: [.audioTimeRange])
    let an = SpeechAnalyzer(modules: [tr])
    var input = pcmBuffer(samples)
    if let fmt = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [tr]), fmt != work,
       let conv = AVAudioConverter(from: work, to: fmt), let out = convert(input, with: conv, flush: true) {
        input = out
    }
    let (stream, cont) = AsyncStream<AnalyzerInput>.makeStream()
    cont.yield(AnalyzerInput(buffer: input))
    cont.finish()
    let collect = Task { () throws -> Transcript in
        var t = Transcript(runs: [])
        for try await r in tr.results {
            for run in r.text.runs {
                let range = run.audioTimeRange
                t.runs.append((String(r.text[run.range].characters), range?.start.seconds, range?.end.seconds))
            }
        }
        return t
    }
    if let end = try await an.analyzeSequence(stream) {
        try await an.finalizeAndFinish(through: end)
    } else {
        await an.cancelAndFinishNow()
    }
    return try await collect.value
}

func ensureModel(_ locale: Locale) async throws {
    let tr = SpeechTranscriber(locale: locale, preset: .transcription)
    if let req = try await AssetInventory.assetInstallationRequest(supporting: [tr]) {
        log("downloading speech model for \(locale.identifier)")
        try await req.downloadAndInstall()
    }
}

// MARK: modes

func check(_ paths: [String], cfg: Config) async throws {
    let locale = Locale(identifier: cfg.locale ?? "ja_JP")
    try await ensureModel(locale)
    let targets = Set(cfg.wakeWords.map(normalize))
    for path in paths {
        let samples = try loadSamples(path) + [Float](repeating: 0, count: Int(rate)) // trailing silence ends the utterance
        var seg = Segmenter(cfg: cfg)
        var verdicts: [String] = []
        for i in stride(from: 0, to: samples.count - frameLen + 1, by: frameLen) {
            guard let event = seg.push(Array(samples[i ..< i + frameLen])) else { continue }
            let (u, isHead) = switch event { case let .utterance(u): (u, false); case let .head(u): (u, true) }
            let tr = try await transcribe(u, locale: locale)
            if !isHead, targets.contains(tr.text) {
                verdicts.append("wake")
            } else if let start = dictationStart(tr, wakeWords: cfg.wakeWords) {
                verdicts.append("dictate:\(start.rest)")
            } else {
                verdicts.append(tr.text)
            }
        }
        print("\(path)\t\(verdicts)")
    }
}

/// Any audio file as 16 kHz mono float samples.
func loadSamples(_ path: String) throws -> [Float] {
    let file = try AVAudioFile(forReading: URL(fileURLWithPath: path))
    let raw = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length))!
    try file.read(into: raw)
    let buf = convert(raw, with: AVAudioConverter(from: raw.format, to: work)!, flush: true)!
    return Array(UnsafeBufferPointer(start: buf.floatChannelData![0], count: Int(buf.frameLength)))
}

// MARK: dictation handoff

/// One file at a time, so a result in the recordings folder is never attributed to the wrong handoff.
let handoffBusy = OSAllocatedUnfairLock(initialState: false)

/// superwhisper transcribes the file. Focus is handed back while superwhisper is frontmost, then the
/// result string is read. That string is routed to one tmux pane when the closed catalog has a unique hit.
/// Opening superwhisper and restoring focus are unchanged.
func handoff(_ samples: [Float], cfg: DictationConfig, target: NSRunningApplication?) async {
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent("voice-switch")
    let wav = dir.appendingPathComponent("\(UUID().uuidString).wav")
    defer { try? FileManager.default.removeItem(at: wav) }
    let submitted = Date()
    do {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try writeWAV(samples, to: wav)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = ["-g", "-a", "superwhisper", wav.path]
        try p.run()
    } catch {
        log("dictation: handing off to superwhisper failed: \(error)"); return
    }
    // Opening a file brings superwhisper to the front despite -g, and it skips auto-paste when it is
    // still frontmost at the end, so hand focus back to where the user was dictating.
    for _ in 0 ..< 20 {
        try? await Task.sleep(nanoseconds: 100_000_000)
        if NSWorkspace.shared.frontmostApplication?.bundleIdentifier == "com.superduper.superwhisper" {
            await MainActor.run { _ = target?.activate() }
        }
    }
    let recordings = NSString(string: cfg.recordingsDir ?? "~/Documents/superwhisper/recordings").expandingTildeInPath
    guard let result = await awaitResult(in: recordings, since: Int(submitted.timeIntervalSince1970) - 2) else {
        log("dictation: no superwhisper result within 30 s"); return
    }
    log("dictation: \(result.count) chars in \(Int(Date().timeIntervalSince(submitted) * 1000)) ms")
    routeDictation(result)
}

/// Polls superwhisper's recordings folder for the run that started at or after `since` (unix seconds).
func awaitResult(in dir: String, since: Int) async -> String? {
    for _ in 0 ..< 300 {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? []
        for name in names.sorted().reversed() {
            guard let t = Int(name), t >= since,
                  let data = FileManager.default.contents(atPath: "\(dir)/\(name)/meta.json"),
                  let meta = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { continue }
            for key in ["llmResult", "result"] {
                if let s = meta[key] as? String, !s.isEmpty { return s }
            }
        }
        try? await Task.sleep(for: .milliseconds(100))
    }
    return nil
}

// MARK: tmux pane route

/// Closed catalog row from `tmux list-panes`. Addressed only by pane id (`%0`), never by index.
struct PaneLabel {
    var id: String
    var window: String
    var title: String
    var command: String
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
/// `lowercased()` matches Python `str.casefold` for the six proof utterances.
func matchingPanes(_ utterance: String, _ panes: [PaneLabel]) -> [PaneLabel] {
    let folded = utterance.lowercased()
    return panes.filter { pane in
        let labels = [pane.title, pane.window, pane.command].filter { !$0.isEmpty }
        return labels.contains { folded.contains($0.lowercased()) }
    }
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
    let hits = matchingPanes(text, panes)
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

func writeWAV(_ samples: [Float], to url: URL) throws {
    let settings: [String: Any] = [AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: rate, AVNumberOfChannelsKey: 1,
                                   AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false]
    let file = try AVAudioFile(forWriting: url, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
    try file.write(from: pcmBuffer(samples))
}

/// The samples from `cutAt` seconds on.
func trimmed(_ samples: [Float], cutAt: Double) -> [Float] {
    Array(samples.dropFirst(min(samples.count, Int(cutAt * rate))))
}

// MARK: dictation hotkeys

/// superwhisper's own record/cancel shortcuts, borrowed while a dictation records. Outside that window the
/// tap passes every event through, so superwhisper's shortcuts behave as if voice-switch were not there.
/// The consumer owns the dictation; the tap only leaves a command here for it to pick up on the next frame.
enum Hotkeys {
    enum Command { case finish, cancel }
    struct Shortcut: Equatable { var keyCode: Int64; var flags: CGEventFlags.RawValue }
    struct State {
        var active = false
        var shortcuts: [(Shortcut, Command)] = []
        var pending: Command?
        var swallowUp: Set<Int64> = [] // keyUps of swallowed presses, so superwhisper never sees half a press
        var loggedMissing: Set<String> = []
    }

    static let state = OSAllocatedUnfairLock(initialState: State())
    static let relevantFlags: CGEventFlags = [.maskCommand, .maskShift, .maskAlternate, .maskControl]
    nonisolated(unsafe) static var tap: CFMachPort?
    nonisolated(unsafe) static var triedTap = false

    /// Called when a dictation starts: re-read the shortcuts (the user may have changed them) and arm the tap.
    static func begin() {
        let defaults = UserDefaults(suiteName: "com.superduper.superwhisper")
        let read = [("KeyboardShortcuts_toggleRecording", Command.finish), ("KeyboardShortcuts_cancelRecording", .cancel)].map {
            (key: $0.0, command: $0.1, shortcut: defaults?.string(forKey: $0.0).flatMap(parseShortcut))
        }
        let shortcuts = read.compactMap { r in r.shortcut.map { ($0, r.command) } }
        let missing = read.filter { $0.shortcut == nil }.map(\.key)
        let newlyMissing = state.withLock { st in
            st.shortcuts = shortcuts; st.pending = nil; st.active = true
            defer { st.loggedMissing.formUnion(missing) }
            return missing.filter { !st.loggedMissing.contains($0) }
        }
        for key in newlyMissing { log("hotkey: superwhisper shortcut \(key) missing or unreadable, that key is off") }
        DispatchQueue.main.async(execute: installTap)
    }

    static func end() { state.withLock { $0.active = false; $0.pending = nil } }

    static func take() -> Command? { state.withLock { st in defer { st.pending = nil }; return st.pending } }

    /// {"carbonModifiers":2560,"carbonKeyCode":38}: Carbon key codes equal CGKeyCodes; modifiers need mapping.
    static func parseShortcut(_ json: String) -> Shortcut? {
        guard let obj = try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any],
              let code = obj["carbonKeyCode"] as? Int, let mods = obj["carbonModifiers"] as? Int else { return nil }
        var flags: CGEventFlags = []
        if mods & 256 != 0 { flags.insert(.maskCommand) }
        if mods & 512 != 0 { flags.insert(.maskShift) }
        if mods & 2048 != 0 { flags.insert(.maskAlternate) }
        if mods & 4096 != 0 { flags.insert(.maskControl) }
        return Shortcut(keyCode: Int64(code), flags: flags.rawValue)
    }

    private static var prompted = false

    static func installTap() {
        guard !triedTap else { return }
        triedTap = true
        // The system prompt is modal and steals focus mid-dictation, so show it at most once per launch.
        if !AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue(): !prompted] as CFDictionary) {
            log("hotkey: Accessibility not granted yet")
        }
        prompted = true
        let mask = CGEventMask(1 << CGEventType.keyDown.rawValue) | CGEventMask(1 << CGEventType.keyUp.rawValue)
        guard let port = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
                                           eventsOfInterest: mask, callback: { _, type, event, _ in Hotkeys.handle(type, event) },
                                           userInfo: nil) else {
            // Retried at the next dictation, in case Accessibility was granted in between.
            triedTap = false
            log("hotkey finish unavailable: grant Accessibility"); return
        }
        tap = port
        CFRunLoopAddSource(CFRunLoopGetMain(), CFMachPortCreateRunLoopSource(nil, port, 0), .commonModes)
        log("hotkey: tap installed")
    }

    static func handle(_ type: CGEventType, _ event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            return Unmanaged.passUnretained(event)
        }
        let code = event.getIntegerValueField(.keyboardEventKeycode)
        let pressed = Shortcut(keyCode: code, flags: event.flags.intersection(relevantFlags).rawValue)
        let swallow = state.withLock { st -> Bool in
            if type == .keyUp { return st.swallowUp.remove(code) != nil }
            guard st.active, type == .keyDown, let (_, command) = st.shortcuts.first(where: { $0.0 == pressed }) else { return false }
            st.pending = command
            st.swallowUp.insert(code)
            return true
        }
        return swallow ? nil : Unmanaged.passUnretained(event)
    }
}

func audioDeviceID(uid: String) -> AudioDeviceID? {
    var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyTranslateUIDToDevice,
                                          mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var cfUID = uid as CFString
    var id = AudioDeviceID(0)
    var size = UInt32(MemoryLayout<AudioDeviceID>.size)
    let status = withUnsafePointer(to: &cfUID) {
        AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, UInt32(MemoryLayout<CFString>.size), $0, &size, &id)
    }
    return status == noErr && id != 0 ? id : nil
}

/// Owns the microphone. stop() releases it fully (menu-bar mic indicator goes off).
final class Listener {
    let config: ConfigFile
    private var engine = AVAudioEngine()
    private let cont: AsyncStream<[Float]>.Continuation
    private var pending: [Float] = []
    private(set) var running = false
    /// CoreAudio device UID; nil follows the system default input. Set only while stopped.
    var deviceUID: String? {
        // A pinned input unit never follows the system default again, so start over with a fresh engine.
        didSet { engine = AVAudioEngine() }
    }

    init(config: ConfigFile) {
        self.config = config
        let (stream, cont) = AsyncStream<[Float]>.makeStream()
        self.cont = cont
        Task { await self.consume(stream) }
        // Input device switches stop the tap silently; restart on the engine's own signal.
        NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: nil, queue: .main) { [weak self] note in
            guard let self, self.running, note.object as? AVAudioEngine === self.engine else { return }
            log("audio configuration changed, restarting input")
            self.stop()
            do { try self.start() } catch { log("restart failed: \(error)") }
        }
    }

    func start() throws {
        var deviceName = "system default"
        if let uid = deviceUID {
            if var id = audioDeviceID(uid: uid), let au = engine.inputNode.audioUnit {
                var current = AudioDeviceID(0)
                var size = UInt32(MemoryLayout<AudioDeviceID>.size)
                AudioUnitGetProperty(au, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &current, &size)
                // Setting it posts AVAudioEngineConfigurationChange; skipping a no-op set keeps restarts from looping.
                if current != id {
                    let status = AudioUnitSetProperty(au, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &id, size)
                    if status != noErr { throw NSError(domain: NSOSStatusErrorDomain, code: Int(status)) }
                }
                deviceName = AVCaptureDevice(uniqueID: uid)?.localizedName ?? uid
            } else {
                log("input device \(uid) not found, using system default")
                // The unit may still be pinned to the vanished device.
                engine = AVAudioEngine()
            }
        }
        let node = engine.inputNode
        // outputFormat stays at the previous device's rate after CurrentDevice is switched
        // (48 kHz vs the webcam's 16 kHz), and a tap with that format gets no buffers at all.
        let fmt = node.inputFormat(forBus: 0)
        guard let conv = AVAudioConverter(from: fmt, to: work) else { throw NSError(domain: "convert", code: 1) }
        pending = []
        node.removeTap(onBus: 0)
        node.installTap(onBus: 0, bufferSize: 4096, format: fmt) { [self] buf, _ in
            guard let out = convert(buf, with: conv, flush: false) else { return }
            pending += UnsafeBufferPointer(start: out.floatChannelData![0], count: Int(out.frameLength))
            while pending.count >= frameLen {
                cont.yield(Array(pending.prefix(frameLen)))
                pending.removeFirst(frameLen)
            }
        }
        engine.prepare()
        try engine.start()
        running = true
        log("listening on \(deviceName) at \(fmt.sampleRate) Hz for \(config.cfg.wakeWords)")
    }

    /// realtime paces frames like the mic does, which matters when the consumer awaits mid-stream.
    func feed(_ samples: [Float], realtime: Bool = false) async {
        for i in stride(from: 0, to: samples.count - frameLen + 1, by: frameLen) {
            cont.yield(Array(samples[i ..< i + frameLen]))
            if realtime { try? await Task.sleep(nanoseconds: 30_000_000) }
        }
    }

    func stop() {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        running = false
        cont.yield([]) // tells consume() to drop any half-collected utterance
        log("paused")
    }

    struct Dictation {
        var samples: [Float]
        var silentFrames: Int
        /// False while waiting for the text after a lone wake word; silence then means "nothing came", not "done".
        var heardSpeech: Bool
        /// Frontmost app when the wake word was heard; superwhisper pastes into whatever is frontmost.
        var target = NSWorkspace.shared.frontmostApplication
    }

    private func consume(_ stream: AsyncStream<[Float]>) async {
        var seg = Segmenter(cfg: config.cfg)
        var dictation: Dictation?
        for await f in stream {
            if f.isEmpty { seg = Segmenter(cfg: config.cfg); dictation = nil; Hotkeys.end(); continue }
            seg.adaptFloor = dictation == nil
            let event = seg.push(f)
            if var d = dictation {
                let dc = config.cfg.dictation ?? DictationConfig()
                let key = Hotkeys.take()
                if key == .cancel {
                    dictation = nil; Hotkeys.end()
                    log("dictation cancelled"); continue
                }
                d.samples += f
                d.silentFrames = seg.lastWasSpeech ? 0 : d.silentFrames + 1
                if case let .utterance(u)? = event, await isStopWord(u) {
                    // The stop word arrived as its own utterance; its audio is the tail of the buffer.
                    d.samples.removeLast(min(u.count, d.samples.count))
                    dictation = nil; Hotkeys.end()
                    log("dictation finished by stop word")
                    if d.heardSpeech, !d.samples.isEmpty { submit(d, cfg: dc) }
                    continue
                }
                if !d.heardSpeech && seg.lastWasSpeech {
                    // Drop the wait before the text, keeping a preroll so the first syllable is whole.
                    d.samples = Array(d.samples.suffix(frames(ms: config.cfg.prerollMs ?? 300) * frameLen + frameLen))
                    d.heardSpeech = true
                }
                dictation = d
                if !d.heardSpeech {
                    if d.silentFrames * frameLen * 1000 / Int(rate) >= dc.startTimeoutMs ?? 3000 {
                        dictation = nil; Hotkeys.end()
                        log("dictation cancelled: nothing said after the wake word")
                    }
                    continue
                }
                guard key == .finish || d.silentFrames * frameLen * 1000 / Int(rate) >= dc.endSilenceMs ?? 1200
                    || d.samples.count > Int((dc.maxSeconds ?? 60) * rate) else { continue }
                let reason = key == .finish ? "hotkey" : d.samples.count > Int((dc.maxSeconds ?? 60) * rate) ? "maxSeconds"
                    : "\(d.silentFrames * frameLen * 1000 / Int(rate)) ms silence"
                d.samples.removeLast(d.silentFrames * frameLen)
                dictation = nil; Hotkeys.end()
                log("dictation ended by \(reason) after \(d.samples.count * 1000 / Int(rate)) ms of audio")
                submit(d, cfg: dc)
                continue
            }
            guard let event else { continue }
            let (u, isHead) = switch event { case let .utterance(u): (u, false); case let .head(u): (u, true) }
            config.reloadIfChanged()
            seg.cfg = config.cfg
            let transcript: Transcript
            let began = Date()
            do { transcript = try await transcribe(u, locale: Locale(identifier: config.cfg.locale ?? "ja_JP")) } catch {
                log("transcribe failed: \(error)"); continue
            }
            let t = transcript.text
            // Logged too, so misses that transcribe to nothing are visible when tuning.
            guard !t.isEmpty else { log("heard: (empty, \(u.count * 1000 / Int(rate)) ms)"); continue }
            if !isHead, (config.cfg.stopWords ?? []).map(normalize).contains(t) {
                if let busy = micInUse(by: config.cfg.skipWhileMicInUseBy ?? []) {
                    // Only while it records: superwhisper://record toggles, so this cannot start a recording.
                    log("heard: \(t)  -> stop \(busy)")
                    runCommand(config.cfg.stopCommand ?? "open -g superwhisper://record")
                } else {
                    log("heard: \(t)  (stop word, nothing is recording)")
                }
                continue
            }
            let hit = !isHead && config.cfg.wakeWords.map(normalize).contains(t)
            // Utterance length shows whether the VAD holds on past the word; stt is recognizer time.
            log("heard: \(t)\(hit ? "  -> wake" : "")  [utt \(u.count * 1000 / Int(rate)) ms, stt \(Int(Date().timeIntervalSince(began) * 1000)) ms]")
            let start = hit || config.cfg.dictation == nil ? nil : dictationStart(transcript, wakeWords: config.cfg.wakeWords)
            guard hit || start != nil else { continue }
            if let busy = micInUse(by: config.cfg.skipWhileMicInUseBy ?? []) {
                // superwhisper://record toggles, so firing while it records would stop it; a dictation
                // started then would be the user's speech already being recorded by superwhisper.
                log("skipped: \(busy) is using the microphone"); continue
            }
            // People pause after the wake word ("音声入力、…"), which ends the utterance at the wake word alone.
            // With dictation on, a lone wake word therefore opens a dictation that waits for the text.
            guard start != nil || config.cfg.dictation != nil else { runCommand(config.cfg.command); continue }
            if let id = NSWorkspace.shared.frontmostApplication?.bundleIdentifier,
               config.cfg.dictation?.excludeBundleIDs?.contains(id) == true {
                log("dictation skipped: \(id) is excluded"); continue
            }
            if let start {
                dictation = Dictation(samples: trimmed(u, cutAt: start.cutAt), silentFrames: 0, heardSpeech: true)
                log("dictation started (cut at \(Int(start.cutAt * 1000)) ms)")
            } else {
                dictation = Dictation(samples: [], silentFrames: 0, heardSpeech: false)
                log("dictation started (waiting for text)")
            }
            Hotkeys.begin()
        }
    }

    private func isStopWord(_ u: [Float]) async -> Bool {
        let words = (config.cfg.stopWords ?? []).map(normalize)
        guard !words.isEmpty, let t = try? await transcribe(u, locale: Locale(identifier: config.cfg.locale ?? "ja_JP")).text else { return false }
        log("dictating: \(t)  [utt \(u.count * 1000 / Int(rate)) ms]")
        return words.contains(t)
    }

    private func submit(_ d: Dictation, cfg: DictationConfig) {
        let claimed = handoffBusy.withLock { busy in
            defer { busy = true }
            return !busy
        }
        guard claimed else { log("dictation dropped: previous one still in flight"); return }
        Task {
            await handoff(d.samples, cfg: cfg, target: d.target)
            handoffBusy.withLock { $0 = false }
        }
    }
}

/// First bundle ID in the list whose process currently has audio input running, via CoreAudio's
/// per-process objects. superwhisper keeps input closed while idle and opens it only while recording.
func micInUse(by bundleIDs: [String]) -> String? {
    guard !bundleIDs.isEmpty else { return nil }
    func address(_ sel: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: sel, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    }
    let system = AudioObjectID(kAudioObjectSystemObject)
    var addr = address(kAudioHardwarePropertyProcessObjectList)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(system, &addr, 0, nil, &size) == noErr else { return nil }
    var procs = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
    guard AudioObjectGetPropertyData(system, &addr, 0, nil, &size, &procs) == noErr else { return nil }
    for proc in procs {
        var running: UInt32 = 0
        var runningSize = UInt32(MemoryLayout<UInt32>.size)
        var runAddr = address(kAudioProcessPropertyIsRunningInput)
        guard AudioObjectGetPropertyData(proc, &runAddr, 0, nil, &runningSize, &running) == noErr, running != 0 else { continue }
        var bundle: Unmanaged<CFString>?
        var bundleSize = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        var bundleAddr = address(kAudioProcessPropertyBundleID)
        guard AudioObjectGetPropertyData(proc, &bundleAddr, 0, nil, &bundleSize, &bundle) == noErr,
              let id = bundle?.takeRetainedValue() as String? else { continue }
        if bundleIDs.contains(id) { return id }
    }
    return nil
}

/// Runs the configured action through the shell so users can put any command line in config.
func runCommand(_ command: String) {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/bin/sh")
    p.arguments = ["-c", command]
    p.terminationHandler = { if $0.terminationStatus != 0 { log("command exited \($0.terminationStatus): \(command)") } }
    do { try p.run() } catch { log("command failed to start: \(error)") }
}

// MARK: menu bar app

let deviceKey = "inputDeviceUID"

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    let configPath: String
    var listener: Listener!
    let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    let toggle = NSMenuItem(title: "一時停止", action: #selector(togglePause), keyEquivalent: "p")
    let login = NSMenuItem(title: "ログイン時に起動", action: #selector(toggleLogin), keyEquivalent: "")
    let micMenu = NSMenu()

    init(configPath: String) { self.configPath = configPath }

    func applicationDidFinishLaunching(_: Notification) {
        let menu = NSMenu()
        menu.addItem(toggle)
        menu.addItem(.separator())
        let mic = NSMenuItem(title: "マイク", action: nil, keyEquivalent: "")
        micMenu.delegate = self
        mic.submenu = micMenu
        menu.addItem(mic)
        menu.addItem(NSMenuItem(title: "設定ファイルを開く", action: #selector(openConfig), keyEquivalent: ","))
        menu.addItem(NSMenuItem(title: "ログを開く", action: #selector(openLog), keyEquivalent: "l"))
        menu.addItem(login)
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "終了", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        for i in menu.items where i.action != #selector(NSApplication.terminate(_:)) { i.target = self }
        item.menu = menu
        refresh()

        let config: ConfigFile
        do { config = try ConfigFile(path: configPath) } catch {
            fail("設定ファイルを読めません: \(configPath)\n\(error)"); return
        }
        listener = Listener(config: config)
        listener.deviceUID = UserDefaults.standard.string(forKey: deviceKey)
        Task { @MainActor in
            do {
                let locale = Locale(identifier: config.cfg.locale ?? "ja_JP")
                try await ensureModel(locale)
                _ = try await transcribe([Float](repeating: 0, count: Int(rate)), locale: locale) // warm up
                guard await AVCaptureDevice.requestAccess(for: .audio) else {
                    fail("マイクへのアクセスが許可されていません。システム設定 > プライバシーとセキュリティ > マイク で許可してください。")
                    return
                }
                try listener.start()
            } catch {
                fail("起動に失敗しました: \(error)")
            }
            refresh()
        }
    }

    func refresh() {
        let on = listener?.running ?? false
        item.button?.image = NSImage(systemSymbolName: on ? "mic.fill" : "mic.slash",
                                     accessibilityDescription: on ? "voice-switch: 待ち受け中" : "voice-switch: 停止中")
        toggle.title = on ? "一時停止" : "再開"
        toggle.isEnabled = listener != nil
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
    }

    func fail(_ message: String) {
        log(message)
        let a = NSAlert()
        a.messageText = "voice-switch"
        a.informativeText = message
        a.runModal()
    }

    @objc func togglePause() {
        if listener.running { listener.stop() } else {
            do { try listener.start() } catch { fail("再開に失敗しました: \(error)") }
        }
        refresh()
    }

    // Rebuilt on every open so plugged/unplugged devices show up.
    func menuNeedsUpdate(_ menu: NSMenu) {
        let selected = UserDefaults.standard.string(forKey: deviceKey)
        let devices = AVCaptureDevice.DiscoverySession(deviceTypes: [.microphone, .external], mediaType: .audio, position: .unspecified).devices
        menu.removeAllItems()
        for (title, uid) in [("システムのデフォルト", nil)] + devices.map({ ($0.localizedName, Optional($0.uniqueID)) }) {
            let i = NSMenuItem(title: title, action: #selector(selectDevice), keyEquivalent: "")
            i.target = self
            i.representedObject = uid
            i.state = uid == selected ? .on : .off
            menu.addItem(i)
        }
    }

    @objc func selectDevice(_ sender: NSMenuItem) {
        let uid = sender.representedObject as? String
        UserDefaults.standard.set(uid, forKey: deviceKey)
        guard let listener else { return }
        let wasRunning = listener.running
        if wasRunning { listener.stop() }
        listener.deviceUID = uid
        if wasRunning {
            do { try listener.start() } catch { fail("マイクの切り替えに失敗しました: \(error)") }
        }
        refresh()
    }

    @objc func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() } else { try SMAppService.mainApp.register() }
        } catch { fail("ログイン項目の変更に失敗しました: \(error)") }
        refresh()
    }

    @objc func openConfig() { NSWorkspace.shared.open(URL(fileURLWithPath: configPath)) }
    @objc func openLog() { NSWorkspace.shared.open(logURL) }
}

// MARK: main

let logURL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/voice-switch.log")
var args = Array(CommandLine.arguments.dropFirst())
let mode = args.first.flatMap { ["--check", "--simulate", "--hotkey-test"].contains($0) ? $0 : nil }
if mode != nil { args.removeFirst() }
let configPath = ProcessInfo.processInfo.environment["VOICE_SWITCH_CONFIG"]
    ?? NSString(string: "~/.config/voice-switch/config.json").expandingTildeInPath

if mode == "--check" {
    Task {
        do { try await check(args, cfg: ConfigFile(path: configPath).cfg); exit(0) } catch { log("fatal: \(error)"); exit(1) }
    }
    dispatchMain()
} else if mode == "--hotkey-test" {
    // Hidden: 8 s of pretend dictation with the tap armed, then 8 s with it idle, to test key routing.
    Hotkeys.begin()
    let armedUntil = Date() + 8
    while Date() < armedUntil {
        RunLoop.main.run(until: Date() + 0.03)
        if let c = Hotkeys.take() { log("hotkey-test: dictation \(c == .finish ? "finished" : "cancelled") by hotkey"); break }
    }
    Hotkeys.end()
    log("hotkey-test: tap idle, keys pass through")
    RunLoop.main.run(until: Date() + 8)
    exit(0)
} else if mode == "--simulate" {
    // Feeds a wav through the live consumer (VAD, wake word, dictation, handoff) instead of the mic.
    let listener = Listener(config: try ConfigFile(path: configPath))
    Task {
        do {
            try await ensureModel(Locale(identifier: listener.config.cfg.locale ?? "ja_JP"))
            await listener.feed(try loadSamples(args[0]) + [Float](repeating: 0, count: Int(rate) * 5),
                              realtime: ProcessInfo.processInfo.environment["SIMULATE_REALTIME"] != nil)
        } catch { log("fatal: \(error)"); exit(1) }
    }
    RunLoop.main.run(until: Date() + 40)
    exit(0)
} else {
    // Launched from Finder/login there is no terminal, so send output to the log file.
    if isatty(STDOUT_FILENO) == 0 {
        if !FileManager.default.fileExists(atPath: logURL.path) {
            FileManager.default.createFile(atPath: logURL.path, contents: nil)
        }
        freopen(logURL.path, "a", stdout)
        setvbuf(stdout, nil, _IOLBF, 0)
    }
    let app = NSApplication.shared
    let delegate = AppDelegate(configPath: configPath)
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
}
