// voice-switch: say a wake word on its own -> run a configured command.
//
// mic -> 16 kHz mono -> energy VAD cuts utterances -> short ones go to on-device STT
// (Apple Speech on macOS) -> whole-utterance match against config wake words.
//
// usage (macOS): VoiceSwitch.app / voice-switch [--check|--simulate|--fire|--vad-selftest|--hud-labels]
// usage (Windows Swift legacy stub): voice-switch.exe [--fire|--vad-selftest|--help]
//
// Build: default `make` → macOS app; `PC=wsl make` → Windows (.NET runtime).
import Foundation

#if os(macOS)
import AppKit

setvbuf(stdout, nil, _IOLBF, 0)

let logURL = Platform.logURL
var args = Array(CommandLine.arguments.dropFirst())
let mode = args.first.flatMap { ["--check", "--simulate", "--hotkey-test", "--vad-selftest", "--fire", "--enroll-speaker", "--speaker-cut", "--hud-labels"].contains($0) ? $0 : nil }
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
} else if mode == "--hud-labels" {
    // Verification only: shows every HUD glyph, writes glyphs.txt (and a PNG per glyph when screen recording is
    // allowed) into the given folder, and fails unless every glyph is distinct, drawn, centered, and every sequence holds.
    guard args.count == 1 else { print("usage: --hud-labels <evidence-dir>"); exit(1) }
    let dir = URL(fileURLWithPath: args[0])
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)
    app.finishLaunching()
    let hud = HUD()
    let steps: [(String, () -> Void)] = [
        ("followup", { hud.show(.followup) }),
        ("waiting", { hud.show(.waiting) }),
        ("recording", { hud.show(.recording) }),
        ("processing", { hud.show(.idle); hud.deliver(.processing) }),
        ("failed", { hud.deliver(.failed) }),
        ("done", { hud.deliver(.done) }),
        // historyAvailable relabels only when the newest history entry is automatic.
        ("history", {
            hud.show(.idle)
            DictationHistory.shared.add(id: UUID(), text: "hud label proof", origin: WakeOrigin(rule: .autoB, inputIdleSeconds: nil))
            hud.historyAvailable()
        }),
        ("paused", { hud.notify(.paused) }),
        ("resumed", { hud.notify(.resumed) }),
    ]
    var lines: [String] = [], labels: [String] = [], failures: [String] = []
    for (name, step) in steps {
        step()
        RunLoop.main.run(until: Date() + 0.3)
        guard let panel = app.windows.first(where: { $0 is NSPanel && $0.isVisible }),
              let glyphView = panel.contentView?.subviews.compactMap({ $0 as? HUDGlyphView }).first,
              let glyph = hud.visibleGlyph else {
            failures.append("\(name): HUD not visible"); continue
        }
        labels.append(glyph.rawValue)
        // Centering is checked in the glyph's own superview space, where its constraints live.
        let frame = glyphView.frame
        let host = glyphView.superview?.bounds ?? .zero
        let centered = abs(frame.midX - host.midX) < 0.5 && abs(frame.midY - host.midY) < 0.5
        lines.append("phase=\(name) glyph=\(glyph.rawValue) frame=\(Int(frame.width))x\(Int(frame.height)) hud=\(Int(panel.frame.width))x\(Int(panel.frame.height)) centered=\(centered ? "yes" : "no")")
        if !centered { failures.append("\(name): glyph is not centered in the HUD") }
        if frame.width > host.width || frame.height > host.height { failures.append("\(name): glyph is larger than the HUD") }
        let shot = Process()
        shot.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        shot.arguments = ["-x", "-o", "-l", String(panel.windowNumber), dir.appendingPathComponent("\(name).png").path]
        try? shot.run()
        shot.waitUntilExit()
    }
    if Set(labels).count != HUDLabel.allCases.count {
        failures.append("expected \(HUDLabel.allCases.count) distinct glyphs, saw \(Set(labels).count): \(labels.joined(separator: ","))")
    }
    func visibleGlyph() -> String {
        hud.visibleGlyph?.rawValue ?? "hidden"
    }
    // Each step: what to do, how long to let timers run, and what the HUD must then show.
    let sequences: [(String, [(String, (HUD) -> Void, TimeInterval, String)])] = [
        ("ended-then-done", [("ended", { $0.show(.ended) }, 0.3, HUDLabel.processing.rawValue),
                             ("done", { $0.deliver(.processing); $0.deliver(.done) }, 0.3, HUDLabel.done.rawValue),
                             ("1.3s", { _ in }, 1.0, "hidden")]),
        ("failed-hides", [("failed", { $0.deliver(.processing); $0.deliver(.failed) }, 0.3, HUDLabel.failed.rawValue),
                          ("3.3s", { _ in }, 3.0, "hidden")]),
        ("new-dictation-wins", [("waiting", { $0.deliver(.processing); $0.show(.waiting) }, 0.3, HUDLabel.waiting.rawValue),
                                ("idle", { $0.show(.idle) }, 0.3, HUDLabel.processing.rawValue)]),
        ("saved-survives-done", [("done", { $0.deliver(.saved); $0.deliver(.done) }, 0.3, HUDLabel.saved.rawValue)]),
        ("dropped-ended-hides", [("ended", { $0.show(.ended) }, 0.3, HUDLabel.processing.rawValue),
                                 ("1.7s", { _ in }, 1.4, "hidden")]),
        // The pause notice covers a dictation label for 1 s, then the dictation label comes back.
        ("notice-then-phase", [("paused", { $0.show(.waiting); $0.notify(.paused) }, 0.3, HUDLabel.paused.rawValue),
                               ("1.3s", { _ in }, 1.0, HUDLabel.waiting.rawValue)]),
    ]
    for (name, steps) in sequences {
        let settled = Date() + 3
        while visibleGlyph() != "hidden" && Date() < settled { RunLoop.main.run(until: Date() + 0.1) }
        let seq = HUD()
        var seen: [String] = [], ok = true
        for (step, act, wait, want) in steps {
            act(seq)
            RunLoop.main.run(until: Date() + wait)
            let got = seq.visibleGlyph?.rawValue ?? "hidden"
            seen.append("\(step)=\(got)")
            if got != want { ok = false; failures.append("sequence \(name): \(step) showed \(got), wanted \(want)") }
        }
        lines.append("sequence=\(name) \(ok ? "ok" : "FAIL") \(seen.joined(separator: " "))")
        seq.show(.idle)
        seq.deliver(nil)
    }
    // superwhisper's own toggle key, as measured on 2026-10-17: the run folder appears empty when recording starts; a stop
    // writes meta.json as the mic closes and judges it later, while Esc deletes the folder or leaves it empty.
    let placeholder: [String: Any] = ["duration": 0, "processingTime": 0, "result": ""]
    let judgedLater: [(String, [String: Any]?, Bool, DirectRunEnd)] = [
        ("polished", ["duration": 5753, "result": "テスト", "llmResult": "テスト。", "languageModelKey": "k", "languageModelProcessingTime": 1221], false, .done),
        ("heard-nothing", ["duration": 900, "processingTime": 120, "result": ""], false, .failed),
        ("esc-deleted", nil, true, .cancelled),
        ("esc-empty", nil, false, .cancelled),
    ]
    for (name, judged, deleted, want) in judgedLater {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("voice-switch-direct-\(UUID().uuidString)")
        let run = root.appendingPathComponent(String(Int(Date().timeIntervalSince1970)))
        try? FileManager.default.createDirectory(at: run, withIntermediateDirectories: true)
        func writeMeta(_ meta: [String: Any]) { try? JSONSerialization.data(withJSONObject: meta).write(to: run.appendingPathComponent("meta.json")) }
        let since = Int(Date().timeIntervalSince1970) - 1
        if deleted { try? FileManager.default.removeItem(at: run) } else if judged != nil { writeMeta(placeholder) }
        var got: DirectRunEnd?
        let began = Date()
        Task { @MainActor in got = await awaitDirectRun(in: root.path, since: since, current: { true }) }
        RunLoop.main.run(until: Date() + 0.5)
        let early = got
        if let judged { writeMeta(judged) }
        let deadline = Date() + 3
        while got == nil && Date() < deadline { RunLoop.main.run(until: Date() + 0.1) }
        let seconds = Date().timeIntervalSince(began)
        try? FileManager.default.removeItem(at: root)
        let ok = early == nil && got == want && seconds < 2.5
        lines.append("direct=\(name) \(ok ? "ok" : "FAIL") end=\(got.map { "\($0)" } ?? "none") seconds=\(String(format: "%.1f", seconds))")
        if !ok { failures.append("direct \(name): wanted \(want) within 2.5 s, got \(got.map { "\($0)" } ?? "none") after \(seconds) s, early=\(String(describing: early))") }
    }
    lines += failures.map { "FAIL \($0)" }
    lines.append(failures.isEmpty ? "PASS: every HUD glyph is distinct, centered, and fits the HUD; every sequence holds" : "FAIL: \(failures.count) check(s)")
    let report = lines.joined(separator: "\n") + "\n"
    try? report.write(to: dir.appendingPathComponent("glyphs.txt"), atomically: true, encoding: .utf8)
    print(report, terminator: "")
    exit(failures.isEmpty ? 0 : 1)
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
    listener.onDelivery = { hud.deliver($0) }
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
