// voice-switch: say a wake word on its own -> run a configured command.
//
// mic -> 16 kHz mono -> energy VAD cuts utterances -> short ones go to on-device STT
// (Apple Speech on macOS) -> whole-utterance match against config wake words.
//
// usage (macOS): VoiceSwitch.app / voice-switch [--check|--simulate|--fire|--vad-selftest]
// usage (Windows Swift legacy stub): voice-switch.exe [--fire|--vad-selftest|--help]
//
// Build: default `make` → macOS app; `PC=wsl make` → Windows (.NET runtime).
import Foundation

#if os(macOS)
import AppKit

setvbuf(stdout, nil, _IOLBF, 0)

let logURL = Platform.logURL
var args = Array(CommandLine.arguments.dropFirst())
let mode = args.first.flatMap { ["--check", "--simulate", "--hotkey-test", "--vad-selftest", "--fire", "--enroll-speaker", "--speaker-cut"].contains($0) ? $0 : nil }
if mode != nil { args.removeFirst() }
let configPath = Platform.defaultConfigPath

if mode == "--enroll-speaker" {
    guard args.count >= 2 else { print("usage: --enroll-speaker <out.json> <16k-mono-pcm16.wav>..."); exit(1) }
    do { try SpeakerGate.enroll(args[0], Array(args.dropFirst())); exit(0) }
    catch { log("speaker enrollment failed: \(error)"); exit(1) }
} else if mode == "--speaker-cut" {
    // Verification only: prints how many samples SpeakerGate.withoutOtherSpeakers keeps, for comparison with the measured rule.
    guard args.count == 2 else { print("usage: --speaker-cut <enrollment.json> <16k-mono-pcm16.wav>"); exit(1) }
    var cfg = Config(wakeWords: [], command: "")
    cfg.speakerEnrollment = args[0]
    do { print("kept=\(SpeakerGate.withoutOtherSpeakers(try SpeakerGate.readWave(args[1]), cfg: cfg).count)"); exit(0) }
    catch { log("speaker cut failed: \(error)"); exit(1) }
} else if mode == "--vad-selftest" {
    // Catches an app bundle that lost the Silero resource; the runtime gate itself fails open.
    guard (try? SpeechGate.voicedWindows([Float](repeating: 0, count: Int(rate)))) == 0 else {
        print("vad-selftest: FAILED (speech gate)")
        exit(1)
    }
    guard FileManager.default.fileExists(atPath: WhisperGate.modelPath) else {
        print("vad-selftest: FAILED (Whisper model missing at \(WhisperGate.modelPath); run make model)")
        exit(1)
    }
    vadSelftest()
    exit(0)
} else if mode == "--fire" {
    do {
        let cfg = try ConfigFile(path: configPath).cfg
        guard Platform.runMacAction(cfg, wait: true) else { exit(1) }
        RunLoop.main.run(until: Date() + 1)
        exit(0)
    } catch { log("fatal: \(error)"); exit(1) }
} else if mode == "--check" {
    Task {
        do { try await check(args, cfg: ConfigFile(path: configPath).cfg); exit(0) } catch { log("fatal: \(error)"); exit(1) }
    }
    dispatchMain()
} else if mode == "--hotkey-test" {
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
    let listener = Listener(config: try ConfigFile(path: configPath))
    _ = NSApplication.shared
    let hud = HUD()
    listener.onPhase = { log("phase: \($0)"); hud.show($0) }
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
    if isatty(STDOUT_FILENO) == 0 {
        try? FileManager.default.createDirectory(at: logURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        if !FileManager.default.fileExists(atPath: logURL.path) {
            FileManager.default.createFile(atPath: logURL.path, contents: nil)
        }
        freopen(logURL.path, "a", stdout)
        freopen(logURL.path, "a", stderr)
        setvbuf(stdout, nil, _IOLBF, 0)
    }
    let app = NSApplication.shared
    let delegate = AppDelegate(configPath: configPath)
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
}

#elseif os(Windows)
WindowsApp.run(args: Array(CommandLine.arguments.dropFirst()))

#else
#error("voice-switch supports macOS and Windows only")
#endif
