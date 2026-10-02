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
import AppKit
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

    init(cfg: Config) { self.cfg = cfg }

    private mutating func isSpeech(_ f: [Float]) -> Bool {
        let rms = (f.reduce(0) { $0 + $1 * $1 } / Float(f.count)).squareRoot()
        let speech = rms > max(floor * (cfg.vadRatio ?? 3), cfg.vadMinRMS ?? 0.005)
        // Track the noise floor only while quiet, so speech does not raise it.
        if !speech { floor = floor * 0.95 + rms * 0.05 }
        return speech
    }

    /// Returns a finished short utterance, or nil.
    mutating func push(_ f: [Float]) -> [Float]? {
        let preroll = frames(ms: cfg.prerollMs ?? 300)
        let hangover = frames(ms: cfg.hangoverMs ?? 300)
        let minFrames = frames(ms: cfg.minSpeechMs ?? 300)
        let maxFrames = Int((cfg.maxSeconds ?? 2.5) * rate) / frameLen
        let speech = isSpeech(f)
        if utt.isEmpty {
            ring.append(f)
            if ring.count > preroll { ring.removeFirst() }
            if speech { utt = ring; silent = 0 }
            return nil
        }
        silent = speech ? 0 : silent + 1
        // Too long to be a wake word (dictation or steady noise): stop buffering and
        // wait for silence so a tail fragment of the dictation is never judged alone.
        if utt.count <= maxFrames + hangover { utt.append(f) } else { skipping = true }
        guard silent >= hangover else { return nil }
        let done = !skipping && utt.count - hangover >= minFrames ? Array(utt.joined()) : nil
        utt = []; ring = []; skipping = false
        return done
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

func normalize(_ s: String) -> String {
    let drop = Set("、。,.!?！？「」")
    return String(s.filter { !$0.isWhitespace && !drop.contains($0) })
}

func transcribe(_ samples: [Float], locale: Locale) async throws -> String {
    let tr = SpeechTranscriber(locale: locale, preset: .transcription)
    let an = SpeechAnalyzer(modules: [tr])
    var input = pcmBuffer(samples)
    if let fmt = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [tr]), fmt != work,
       let conv = AVAudioConverter(from: work, to: fmt), let out = convert(input, with: conv, flush: true) {
        input = out
    }
    let (stream, cont) = AsyncStream<AnalyzerInput>.makeStream()
    cont.yield(AnalyzerInput(buffer: input))
    cont.finish()
    let collect = Task { () throws -> String in
        var s = ""
        for try await r in tr.results { s += String(r.text.characters) }
        return s
    }
    if let end = try await an.analyzeSequence(stream) {
        try await an.finalizeAndFinish(through: end)
    } else {
        await an.cancelAndFinishNow()
    }
    return normalize(try await collect.value)
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
        let file = try AVAudioFile(forReading: URL(fileURLWithPath: path))
        let raw = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length))!
        try file.read(into: raw)
        let buf = convert(raw, with: AVAudioConverter(from: raw.format, to: work)!, flush: true)!
        var samples = Array(UnsafeBufferPointer(start: buf.floatChannelData![0], count: Int(buf.frameLength)))
        samples += [Float](repeating: 0, count: Int(rate)) // trailing silence ends the utterance
        var seg = Segmenter(cfg: cfg)
        var fired = 0
        var heard: [String] = []
        for i in stride(from: 0, to: samples.count - frameLen + 1, by: frameLen) {
            guard let u = seg.push(Array(samples[i ..< i + frameLen])) else { continue }
            let t = try await transcribe(u, locale: locale)
            heard.append(t)
            if targets.contains(t) { fired += 1 }
        }
        print("\(path)\theard=\(heard)\tfired=\(fired)")
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

    func stop() {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        running = false
        cont.yield([]) // tells consume() to drop any half-collected utterance
        log("paused")
    }

    private func consume(_ stream: AsyncStream<[Float]>) async {
        var seg = Segmenter(cfg: config.cfg)
        for await f in stream {
            if f.isEmpty { seg = Segmenter(cfg: config.cfg); continue }
            guard let u = seg.push(f) else { continue }
            config.reloadIfChanged()
            seg.cfg = config.cfg
            let t: String
            let began = Date()
            do { t = try await transcribe(u, locale: Locale(identifier: config.cfg.locale ?? "ja_JP")) } catch {
                log("transcribe failed: \(error)"); continue
            }
            // Logged too, so misses that transcribe to nothing are visible when tuning.
            guard !t.isEmpty else { log("heard: (empty, \(u.count * 1000 / Int(rate)) ms)"); continue }
            let hit = config.cfg.wakeWords.map(normalize).contains(t)
            // Utterance length shows whether the VAD holds on past the word; stt is recognizer time.
            log("heard: \(t)\(hit ? "  -> wake" : "")  [utt \(u.count * 1000 / Int(rate)) ms, stt \(Int(Date().timeIntervalSince(began) * 1000)) ms]")
            if hit { runCommand(config.cfg.command) }
        }
    }
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
let checkMode = args.first == "--check"
if checkMode { args.removeFirst() }
let configPath = ProcessInfo.processInfo.environment["VOICE_SWITCH_CONFIG"]
    ?? NSString(string: "~/.config/voice-switch/config.json").expandingTildeInPath

if checkMode {
    Task {
        do { try await check(args, cfg: ConfigFile(path: configPath).cfg); exit(0) } catch { log("fatal: \(error)"); exit(1) }
    }
    dispatchMain()
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
