// macOS app: menu bar, Apple Speech, CoreAudio, CGEvent hotkeys.
#if os(macOS)
import AppKit
import os
import AVFoundation
import Foundation
import ServiceManagement
import Speech

let work = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: rate, channels: 1, interleaved: false)!


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

struct TranscribeTimeoutError: Error, CustomStringConvertible {
    let seconds: TimeInterval
    var description: String { "timed out after \(Int(seconds.rounded(.up)))s" }
}

/// One-shot Apple Speech with the Windows-matching deadline (RESEARCH hyp 2).
/// On timeout, throws `TranscribeTimeoutError` so callers take the existing `transcribe failed` path.
/// cancelAll alone still waits for children; `transcribeUnbounded` hard-stops via cancelAndFinishNow.
func transcribe(_ samples: [Float], locale: Locale) async throws -> Transcript {
    let limit = transcribeDeadlineSeconds(audioSeconds: Double(samples.count) / rate)
    try await withThrowingTaskGroup(of: Transcript.self) { group in
        group.addTask {
            try await transcribeUnbounded(samples, locale: locale)
        }
        group.addTask {
            try await Task.sleep(nanoseconds: UInt64(limit * 1_000_000_000))
            throw TranscribeTimeoutError(seconds: limit)
        }
        do {
            guard let result = try await group.next() else {
                throw TranscribeTimeoutError(seconds: limit)
            }
            group.cancelAll()
            return result
        } catch {
            group.cancelAll()
            throw error
        }
    }
}

/// Hard-stops SpeechAnalyzer on cancel (Windows KillProcess intent).
/// `withThrowingTaskGroup.cancelAll` still waits for children; analyzeSequence/finalize may ignore
/// Task cancellation alone, so without cancelAndFinishNow the deadline never frees the group.
func transcribeUnbounded(_ samples: [Float], locale: Locale) async throws -> Transcript {
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
            try Task.checkCancellation()
            for run in r.text.runs {
                let range = run.audioTimeRange
                t.runs.append((String(r.text[run.range].characters), range?.start.seconds, range?.end.seconds))
            }
        }
        return t
    }
    return try await withTaskCancellationHandler {
        do {
            try Task.checkCancellation()
            if let end = try await an.analyzeSequence(stream) {
                try Task.checkCancellation()
                try await an.finalizeAndFinish(through: end)
            } else {
                await an.cancelAndFinishNow()
            }
            return try await collect.value
        } catch {
            collect.cancel()
            await an.cancelAndFinishNow()
            throw error
        }
    } onCancel: {
        collect.cancel()
        // Must hard-stop SpeechAnalyzer so cancelAll can return (Windows KillProcess equivalent).
        Task { await an.cancelAndFinishNow() }
    }
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

/// Superwhisper modes / preferences sit next to `Documents/superwhisper/recordings`.
func superwhisperPreferencesPath() -> String {
    Platform.expandPath("~/Documents/superwhisper/preferences.json")
}

func superwhisperModesDir() -> String {
    Platform.expandPath("~/Documents/superwhisper/modes")
}

func openSuperwhisperURL(_ url: String) throws {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
    p.arguments = ["-g", url]
    try p.run()
}

func readSuperwhisperActiveMode(preferencesPath: String = superwhisperPreferencesPath()) -> String? {
    guard let data = FileManager.default.contents(atPath: preferencesPath),
          let text = String(data: data, encoding: .utf8) else { return nil }
    return activeSuperwhisperMode(text)
}

func readSuperwhisperModeJsons(modesDir: String = superwhisperModesDir()) -> [String] {
    let fm = FileManager.default
    guard let names = try? fm.contentsOfDirectory(atPath: modesDir) else { return [] }
    return names.filter { $0.hasSuffix(".json") }.sorted().compactMap { name in
        guard let data = fm.contents(atPath: (modesDir as NSString).appendingPathComponent(name)) else { return nil }
        return String(data: data, encoding: .utf8)
    }
}

/// Restore focus while Superwhisper is frontmost (mode URL and file URL both may steal it).
func restoreFocusIfSuperwhisper(_ target: NSRunningApplication?) async {
    if NSWorkspace.shared.frontmostApplication?.bundleIdentifier == "com.superduper.superwhisper" {
        await MainActor.run { _ = target?.activate() }
    }
}

/// Switches Superwhisper mode via `superwhisper://mode?key=` and polls activeMode up to ~3 s.
func switchSuperwhisperMode(_ key: String, target: NSRunningApplication?) async -> Bool {
    let encoded = key.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? key
    do {
        try openSuperwhisperURL("superwhisper://mode?key=\(encoded)")
    } catch {
        log("superwhisper mode: switch to \(key) failed to start: \(error)")
        return false
    }
    for _ in 0 ..< 30 {
        try? await Task.sleep(nanoseconds: 100_000_000)
        await restoreFocusIfSuperwhisper(target)
        if readSuperwhisperActiveMode() == key { return true }
    }
    log("superwhisper mode: \(key) not active after 3 s; continuing")
    return false
}

/// Requested is true only when the configured mode is confirmed active; Previous is what to restore.
/// A failed 3 s poll must leave requested false so Decide stays on the Superwhisper path (no paste):
/// otherwise Superwhisper may still be on an autoPaste mode → double delivery (B2).
func enterSuperwhisperMode(_ wanted: String?, target: NSRunningApplication?) async -> (requested: Bool, previous: String?) {
    guard let wanted, !wanted.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
        return (false, nil)
    }
    let trimmed = wanted.trimmingCharacters(in: .whitespacesAndNewlines)
    guard let key = resolveSuperwhisperModeKey(trimmed, modeJsons: readSuperwhisperModeJsons()) else {
        log("superwhisper mode \"\(trimmed)\" not found; using the active mode")
        return (false, nil)
    }
    guard let active = readSuperwhisperActiveMode() else {
        log("superwhisper mode: activeMode is unreadable; using the active mode")
        return (false, nil)
    }
    if active == key { return (true, nil) }
    log("superwhisper mode: \(key) (was \(active))")
    _ = await switchSuperwhisperMode(key, target: target)
    // Gate on the post-switch read, not switchSuperwhisperMode's Bool alone — same contract as Windows EnterModeAsync.
    guard readSuperwhisperActiveMode() == key else {
        return (false, nil)
    }
    return (true, active)
}

/// Clipboard + Cmd+V into the wake-time app. Used when mode auto-paste is off and pane did not take it.
func pasteDictation(_ text: String, target: NSRunningApplication?) -> Bool {
    guard let target else { return false }
    let pb = NSPasteboard.general
    let previous = pb.string(forType: .string)
    pb.clearContents()
    guard pb.setString(text, forType: .string) else {
        log("dictation paste: clipboard failed")
        return false
    }
    let ok = target.activate()
    if !ok {
        log("dictation paste: could not bring target to the front")
    }
    let source = CGEventSource(stateID: .hidSystemState)
    let keyV: CGKeyCode = 0x09 // kVK_ANSI_V
    if let down = CGEvent(keyboardEventSource: source, virtualKey: keyV, keyDown: true),
       let up = CGEvent(keyboardEventSource: source, virtualKey: keyV, keyDown: false) {
        down.flags = .maskCommand
        up.flags = .maskCommand
        down.post(tap: .cghidEventTap)
        up.post(tap: .cghidEventTap)
    }
    if let previous {
        Task {
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            if pb.string(forType: .string) == text {
                pb.clearContents()
                _ = pb.setString(previous, forType: .string)
            }
        }
    }
    return true
}

/// superwhisper transcribes the file. With `dictation.superwhisperMode` set, switches to that mode
/// (auto-paste off), restores after, and voice-switch pastes only when the pane route did not deliver.
/// Unset keeps the old behavior (Superwhisper may auto-paste).
func handoff(_ samples: [Float], cfg: DictationConfig, target: NSRunningApplication?) async {
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent("voice-switch")
    let wav = dir.appendingPathComponent("\(UUID().uuidString).wav")
    defer { try? FileManager.default.removeItem(at: wav) }
    let submitted = Date()
    let (modeRequested, previousMode) = await enterSuperwhisperMode(cfg.superwhisperMode, target: target)
    var result: String?
    var launched = false
    do {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try writeWAV(samples, to: wav)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = ["-g", "-a", "superwhisper", wav.path]
        try p.run()
        launched = true
        // Opening a file brings superwhisper to the front despite -g, and it skips auto-paste when it is
        // still frontmost at the end, so hand focus back to where the user was dictating.
        for _ in 0 ..< 20 {
            try? await Task.sleep(nanoseconds: 100_000_000)
            await restoreFocusIfSuperwhisper(target)
        }
        let recordings = Platform.expandPath(cfg.recordingsDir ?? Platform.defaultRecordingsDir)
        result = await awaitResult(in: recordings, since: Int(submitted.timeIntervalSince1970) - 2)
    } catch {
        log("dictation: handing off to superwhisper failed: \(error)")
    }
    if let previousMode, await switchSuperwhisperMode(previousMode, target: target) {
        log("superwhisper mode restored: \(previousMode)")
    }
    guard launched else { return }
    guard let result else {
        log("dictation: no superwhisper result within 30 s"); return
    }
    log("dictation: \(result.count) chars in \(Int(Date().timeIntervalSince(submitted) * 1000)) ms")
    let route = await routeDictation(result)
    switch decideDictationDelivery(modeRequested: modeRequested, route: route) {
    case .pane:
        break
    case .paste:
        // SendFailed already chose a pane body; paste that, never the full dictation wrapper.
        let payload: String?
        if route == .sendFailed {
            payload = extractSendBody(result)
            if payload == nil {
                log("dictation not delivered: send failed and no body to paste")
                break
            }
        } else {
            payload = result
        }
        if let payload {
            log(pasteDictation(payload, target: target)
                ? "dictation delivered: paste"
                : "dictation not delivered: no target window to paste into")
        }
    case .superwhisper:
        log("dictation delivered: superwhisper")
    }
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

func writeWAV(_ samples: [Float], to url: URL) throws {
    let settings: [String: Any] = [AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: rate, AVNumberOfChannelsKey: 1,
                                   AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false]
    let file = try AVAudioFile(forWriting: url, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
    try file.write(from: pcmBuffer(samples))
}

/// The samples from `cutAt` seconds on.
/// After an over-cap head with no wake, adopt the mean level while skipping as the new floor (Windows Segmenter.RebaseFloor).
func rebaseFloorAfterNoWake(_ seg: inout Segmenter) {
    let before = seg.noiseFloor
    seg.rebaseFloor()
    log(String(format: "dictation vad: floor rebased floor=%.4f->%.4f threshold=%.4f", before, seg.noiseFloor, seg.threshold))
}

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
    /// The user wants input on; stays true while a failed restart is being retried, unlike `running`.
    private(set) var wanted = false
    /// Called on the main thread after each restart attempt so the menu bar can follow `running`.
    var onRestart: (() -> Void)?
    private var restartError: String?
    private var retryScheduled = false
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
            guard let self, self.wanted, note.object as? AVAudioEngine === self.engine else { return }
            log("audio configuration changed, restarting input")
            self.restart()
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
        guard let conv = AVAudioConverter(from: fmt, to: work) else {
            throw NSError(domain: "convert", code: 1, userInfo: [NSLocalizedDescriptionKey: "input format \(fmt.sampleRate) Hz, \(fmt.channelCount) ch"])
        }
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
        wanted = true
        log("listening on \(deviceName) at \(fmt.sampleRate) Hz for \(config.cfg.wakeWords)")
    }

    /// realtime paces frames like the mic does, which matters when the consumer awaits mid-stream.
    func feed(_ samples: [Float], realtime: Bool = false) async {
        for i in stride(from: 0, to: samples.count - frameLen + 1, by: frameLen) {
            cont.yield(Array(samples[i ..< i + frameLen]))
            if realtime { try? await Task.sleep(nanoseconds: 30_000_000) }
        }
    }

    func stop() { wanted = false; halt() }

    private func halt() {
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
        /// Frames during which the mic hears our own confirmation sound, so it cannot count as the text starting.
        var deafFrames = 0
    }

    enum Phase { case idle, waiting, recording, ended }

    /// Called on the main thread whenever the dictation phase changes.
    var onPhase: ((Phase) -> Void)?

    private func consume(_ stream: AsyncStream<[Float]>) async {
        var seg = Segmenter(cfg: config.cfg)
        var dictation: Dictation?
        var shown = Phase.idle
        for await f in stream {
            // Set where the user ended it on purpose (stop word or superwhisper's shortcut), so the HUD confirms it.
            var endedByUser = false
            // Derived from `dictation` after every frame (defer covers each `continue`) instead of at each place that sets it.
            defer {
                let phase: Phase = endedByUser ? .ended : dictation.map { $0.heardSpeech ? .recording : .waiting } ?? .idle
                // .ended hides itself after a moment; the idle that follows it is not a change.
                if phase != shown, !(shown == .ended && phase == .idle) {
                    shown = phase
                    DispatchQueue.main.async { self.onPhase?(phase) }
                }
            }
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
                    dictation = nil; Hotkeys.end(); endedByUser = true
                    log("dictation finished by stop word")
                    if d.heardSpeech, !d.samples.isEmpty { submit(d, cfg: dc) }
                    continue
                }
                if d.deafFrames > 0 { d.deafFrames -= 1 } else if !d.heardSpeech && seg.lastWasSpeech {
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
                dictation = nil; Hotkeys.end(); endedByUser = key == .finish
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
                log("transcribe failed: \(error)")
                // Over-cap head that timed out / failed STT: same trap as empty/no-wake; lift the floor (Windows RebaseFloor).
                if isHead { rebaseFloorAfterNoWake(&seg) }
                continue
            }
            let t = transcript.text
            // Logged too, so misses that transcribe to nothing are visible when tuning.
            guard !t.isEmpty else {
                log("heard: (empty, \(u.count * 1000 / Int(rate)) ms)")
                // Over-cap head with no text: same trap as steady noise; lift the floor (Windows RebaseFloor).
                if isHead { rebaseFloorAfterNoWake(&seg) }
                continue
            }
            if !isHead, (config.cfg.stopWords ?? []).map(normalize).contains(t) {
                if let busy = micInUse(by: config.cfg.skipWhileMicInUseBy ?? []) {
                    // Only while it records: superwhisper://record toggles, so this cannot start a recording.
                    log("heard: \(t)  -> stop \(busy)")
                    Platform.runCommand(config.cfg.stopCommand ?? Platform.defaultSuperwhisperToggle)
                } else {
                    log("heard: \(t)  (stop word, nothing is recording)")
                }
                continue
            }
            let hit = !isHead && config.cfg.wakeWords.map(normalize).contains(t)
            // Utterance length shows whether the VAD holds on past the word; stt is recognizer time.
            log("heard: \(t)\(hit ? "  -> wake" : "")  [utt \(u.count * 1000 / Int(rate)) ms, stt \(Int(Date().timeIntervalSince(began) * 1000)) ms]")
            let start = hit || config.cfg.dictation == nil ? nil : dictationStart(transcript, wakeWords: config.cfg.wakeWords)
            // Over-cap head and recognizer found no wake: steady noise must not keep skipping forever (Windows RebaseFloor).
            if isHead && start == nil {
                rebaseFloorAfterNoWake(&seg)
                continue
            }
            guard hit || start != nil else { continue }
            if let busy = micInUse(by: config.cfg.skipWhileMicInUseBy ?? []) {
                // superwhisper://record toggles, so firing while it records would stop it; a dictation
                // started then would be the user's speech already being recorded by superwhisper.
                log("skipped: \(busy) is using the microphone"); continue
            }
            // People pause after the wake word ("音声入力、…"), which ends the utterance at the wake word alone.
            // With dictation on, a lone wake word therefore opens a dictation that waits for the text.
            guard start != nil || config.cfg.dictation != nil else { Platform.runCommand(config.cfg.command); continue }
            if let id = NSWorkspace.shared.frontmostApplication?.bundleIdentifier,
               config.cfg.dictation?.excludeBundleIDs?.contains(id) == true {
                log("dictation skipped: \(id) is excluded"); continue
            }
            if let start {
                dictation = Dictation(samples: trimmed(u, cutAt: start.cutAt), silentFrames: 0, heardSpeech: true)
                log("dictation started (cut at \(Int(start.cutAt * 1000)) ms)")
            } else {
                dictation = Dictation(samples: [], silentFrames: 0, heardSpeech: false)
                // Only here: in a one-breath dictation the user is already talking and the sound would be recorded.
                if UserDefaults.standard.bool(forKey: soundKey) {
                    NSSound(named: "Tink")?.play()
                    dictation?.deafFrames = frames(ms: 600) // frames queued during STT predate the sound, so leave margin
                }
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

    private func restart() { halt(); attemptStart() }

    private func attemptStart() {
        do { try start(); restartError = nil } catch {
            // One log line per distinct error, the retry repeats every 10 s while the device is away.
            if restartError != "\(error)" { restartError = "\(error)"; log("restart failed, retrying every 10 s: \(error)") }
            // The one recovery seen in the field (2026-10-03 log) went through a fresh engine.
            engine = AVAudioEngine()
            if !retryScheduled {
                retryScheduled = true
                DispatchQueue.main.asyncAfter(deadline: .now() + 10) { [weak self] in
                    guard let self else { return }
                    self.retryScheduled = false
                    if self.wanted, !self.running { self.attemptStart() }
                }
            }
        }
        onRestart?()
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


// MARK: menu bar app

let deviceKey = "inputDeviceUID"
let soundKey = "confirmationSound"

/// Floating label at the top of the screen while a dictation is open; the menu bar may be hidden.
final class HUD {
    private let label = NSTextField(labelWithString: "")
    private lazy var panel: NSPanel = {
        let p = NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        p.level = .statusBar
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        p.ignoresMouseEvents = true
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = true
        let bg = NSVisualEffectView()
        bg.material = .hudWindow
        bg.state = .active
        bg.wantsLayer = true
        bg.layer?.cornerRadius = 12
        label.font = .systemFont(ofSize: 15, weight: .semibold)
        label.translatesAutoresizingMaskIntoConstraints = false
        bg.addSubview(label)
        NSLayoutConstraint.activate([
            label.centerXAnchor.constraint(equalTo: bg.centerXAnchor),
            label.centerYAnchor.constraint(equalTo: bg.centerYAnchor),
        ])
        p.contentView = bg
        return p
    }()

    private var shown = 0

    func show(_ phase: Listener.Phase) {
        shown += 1
        switch phase {
        case .idle: panel.orderOut(nil); return
        case .waiting: label.stringValue = "🎙 どうぞ"; label.textColor = .labelColor
        case .recording: label.stringValue = "● 録音中"; label.textColor = .systemRed
        case .ended:
            label.stringValue = "■ 録音終了"; label.textColor = .labelColor
            let mine = shown
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [self] in
                if shown == mine { panel.orderOut(nil) }
            }
        }
        // The screen with the mouse, so it shows where the user is looking on multi-monitor setups.
        let screen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main
        guard let area = screen?.visibleFrame else { return }
        let size = NSSize(width: 160, height: 40)
        panel.setFrame(NSRect(x: area.midX - size.width / 2, y: area.maxY - size.height - 12, width: size.width, height: size.height), display: true)
        panel.orderFrontRegardless()
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    let configPath: String
    var listener: Listener!
    let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    let toggle = NSMenuItem(title: "一時停止", action: #selector(togglePause), keyEquivalent: "p")
    let login = NSMenuItem(title: "ログイン時に起動", action: #selector(toggleLogin), keyEquivalent: "")
    let sound = NSMenuItem(title: "効果音", action: #selector(toggleSound), keyEquivalent: "")
    let micMenu = NSMenu()
    let hud = HUD()

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
        menu.addItem(sound)
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
        listener.onPhase = { [hud] in hud.show($0) }
        listener.onRestart = { [weak self] in self?.refresh() }
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
        let wanted = listener?.wanted ?? false
        item.button?.image = NSImage(systemSymbolName: on ? "mic.fill" : "mic.slash",
                                     accessibilityDescription: on ? "voice-switch: 待ち受け中" : "voice-switch: 停止中")
        toggle.title = wanted ? "一時停止" : "再開"
        toggle.isEnabled = listener != nil
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        sound.state = UserDefaults.standard.bool(forKey: soundKey) ? .on : .off
    }

    @objc func toggleSound() {
        UserDefaults.standard.set(!UserDefaults.standard.bool(forKey: soundKey), forKey: soundKey)
        refresh()
    }

    func fail(_ message: String) {
        log(message)
        let a = NSAlert()
        a.messageText = "voice-switch"
        a.informativeText = message
        a.runModal()
    }

    @objc func togglePause() {
        if listener.wanted { listener.stop() } else {
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
        let wanted = listener.wanted
        if wanted { listener.stop() }
        listener.deviceUID = uid
        if wanted {
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

#endif
