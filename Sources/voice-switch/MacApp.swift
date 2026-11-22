// macOS app: menu bar, Apple Speech, CoreAudio, CGEvent hotkeys.
#if os(macOS)
import AppKit
import Carbon
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
    return try await withThrowingTaskGroup(of: Transcript.self) { group in
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

/// Characters Apple Speech and Whisper both wrote for `samples`, compared with `NoWakePolicy.minAgreedCharacters`.
func agreedWordCount(_ samples: [Float], locale: Locale) async throws -> Int {
    async let whisperText = Task.detached { try WhisperGate.transcribe(samples) }.value
    async let appleText = transcribe(samples, locale: locale).text
    return NoWakePolicy.agreedWithWhisper(apple: try await appleText, whisper: try await whisperText)
}

/// What a stop check found: no stop word, a stop word too recent to end the dictation on yet (shown on the HUD at once,
/// so the user knows it was heard), or the samples to drop from the end of the dictation.
enum StopVerdict: Equatable, Sendable { case none, heard, cut(Int) }

/// `heard` is a transcript of `u` already taken this frame, so the utterance is not recognized twice.
/// Runs beside the frame loop, so it may wait on the recognizer.
func stopWordCut(_ u: [Float], heard: Transcript?, stopWords: [String], locale: Locale, quiet: Bool, kind: String, sessionID: UUID) async -> StopVerdict {
    let head = "stop check: kind=\(kind) utt=\(u.count * 1000 / Int(rate)) ms"
    let t: Transcript
    if let heard { t = heard } else {
        guard let tr = try? await transcribe(u, locale: locale) else { log("\(head) recognizer=failed \(sidField(sessionID))"); return .none }
        t = tr
    }
    if !quiet { log("dictating: \(t.text)  [utt \(u.count * 1000 / Int(rate)) ms]") }
    var start = stopWordStart(t, stopWords: stopWords)
    var recent = false
    if kind == "window", let s = start {
        // A window opens anywhere in the speech, and Apple Speech stretches its first run back over the unrecognized sound
        // before it (入@0.00-0.90), so a stop word that opens the window starts where its second character does, less that
        // character's length. Cutting at the window start dropped the end of the sentence said before it.
        let second = t.runs.count > 1 ? t.runs[1] : nil
        let opening = second.flatMap { r in r.start.map { max(0, $0 - ((r.end ?? $0) - $0)) } } ?? 0
        // Speech going on past a stop word would be heard within half a second, so only that much silence after it stops.
        // ponytail: fixed 0.5 s after the last recognized word; make it a knob if slow speakers pause longer mid-sentence.
        let heardUntil = t.runs.last.flatMap { $0.end ?? $0.start } ?? Double(u.count) / rate
        recent = heardUntil > Double(u.count) / rate - 0.5
        start = recent ? nil : s == 0 ? opening : s
    }
    let cut = start.map { u.count - min(u.count, Int($0 * rate)) }
    // A window check runs several times a second; only its matches are worth a line.
    if kind != "window" || cut != nil || recent {
        log("\(head) recognizer=ok matched=\(recent ? "heard" : cut == nil ? "no" : "yes") cut=\((cut ?? 0) * 1000 / Int(rate)) ms \(sidField(sessionID))")
    }
    return recent ? .heard : cut.map { .cut($0) } ?? .none
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
    let sets = wakeSets(cfg)
    for path in paths {
        let samples = try loadSamples(path) + [Float](repeating: 0, count: Int(rate)) // trailing silence ends the utterance
        if ProcessInfo.processInfo.environment["VS_VAD_TRACE"] != nil {
            // Offline word-end study: Silero probability beside the raw level of the same 32 ms window.
            for (n, p) in try SpeechGate.probabilities(samples).enumerated() {
                let w = samples[n * SpeechGate.window ..< min((n + 1) * SpeechGate.window, samples.count)]
                let rms = (w.reduce(0) { $0 + $1 * $1 } / Float(w.count)).squareRoot()
                FileHandle.standardError.write(Data("vad-trace\t\(path)\t\(n * 32)\t\(p)\t\(rms)\n".utf8))
            }
            continue
        }
        var seg = Segmenter(cfg: cfg)
        var silero = SileroStream()
        var verdicts: [String] = []
        var earlyHit = false // one early verdict per utterance; the hangover verdict still follows for comparison
        for i in stride(from: 0, to: samples.count - frameLen + 1, by: frameLen) {
            let frame = Array(samples[i ..< i + frameLen])
            let event = seg.push(frame)
            if !(cfg.macOS?.actions ?? []).isEmpty { for p in silero.push(frame) { seg.noteVoice(p) } }
            if event == nil, !earlyHit, let buf = seg.probe() {
                let began = Date()
                let tr = try await transcribe(buf, locale: locale)
                let stt = Int(Date().timeIntervalSince(began) * 1000)
                let at = (i + frameLen) * 1000 / Int(rate) // audio ms at which this probe could fire
                if let early = wakeVerdict(tr, sets: sets, tailSilentFrames: seg.probeSilentFrames, closed: false) {
                    let action = early.action.map { " action=\($0.name)" } ?? ""
                    verdicts.append(early.start.map { "early-dictate@\(at)ms+stt\(stt):\($0.rest)\(action)" } ?? "early-wake@\(at)ms+stt\(stt)\(action)")
                    earlyHit = true
                }
            }
            guard let event else { continue }
            if case .tail = event { continue }
            earlyHit = false
            let (u, isHead) = switch event { case let .utterance(u): (u, false); case let .head(u): (u, true); case .tail: ([], false) }
            let began = Date()
            let tr = try await transcribe(u, locale: locale)
            if let verdict = wakeVerdict(tr, sets: sets, tailSilentFrames: 0, closed: !isHead) {
                let timing = "@\((i + frameLen) * 1000 / Int(rate))ms+stt\(Int(Date().timeIntervalSince(began) * 1000))"
                let action = verdict.action.map { " action=\($0.name)" } ?? ""
                verdicts.append(verdict.start.map { "dictate\(timing):\($0.rest)\(action)" } ?? "wake\(timing)\(action)")
            } else {
                verdicts.append(tr.text)
            }
        }
        print("\(path)\t\(verdicts)")
        // stderr keeps stdout in the format scripts and the Windows --check share.
        FileHandle.standardError.write(Data("\(path)\tvoiced=\(try SpeechGate.voicedWindows(samples))\n".utf8))
        let apple = try await transcribe(samples, locale: locale).text
        let whisper = (try? WhisperGate.transcribe(samples)) ?? "<unavailable>"
        FileHandle.standardError.write(Data("\(path)\tagreed=\(NoWakePolicy.agreedCharacters(apple, whisper))\tapple=\(apple)\twhisper=\(whisper)\n".utf8))
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
    // Current Superwhisper keeps the active mode in its defaults domain, not in preferences.json.
    let domain = "com.superduper.superwhisper" as CFString
    _ = CFPreferencesAppSynchronize(domain)
    if let key = CFPreferencesCopyAppValue("activeModeKey" as CFString, domain) as? String, !key.isEmpty {
        return key
    }
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
    do { try MacModeRestoreStore.write(ModeRestore(previous: active, temporary: key)) }
    catch { log("superwhisper mode: cannot save recovery state; no mode switch"); return (false, nil) }
    log("superwhisper mode: \(key) (was \(active))")
    _ = await switchSuperwhisperMode(key, target: target)
    // Gate on the post-switch read, not switchSuperwhisperMode's Bool alone — same contract as Windows EnterModeAsync.
    guard readSuperwhisperActiveMode() == key else {
        return (false, active)
    }
    return (true, active)
}

func recoverSuperwhisperMode(target: NSRunningApplication?) async -> Bool {
    do {
        guard let state = try MacModeRestoreStore.read() else { return true }
        switch state.decide(readSuperwhisperActiveMode()) {
        case .unknown: return false
        case .restore:
            guard await switchSuperwhisperMode(state.previous, target: target) else { return false }
            log("superwhisper mode restored from recovery state")
        case .clear: break
        }
        try MacModeRestoreStore.clear()
        return true
    } catch {
        log("superwhisper mode recovery unavailable; local history required")
        return false
    }
}

/// At launch and before each wake-word handoff. The switch is sent even when the stored activeModeKey already names the
/// mode: superwhisper ran a recording in `message` while storing `default` (2026-10-28 13:25:10), so the stored value is not trusted.
func selectPasteMode(_ wanted: String) async {
    guard let key = resolveSuperwhisperModeKey(wanted.trimmingCharacters(in: .whitespacesAndNewlines), modeJsons: readSuperwhisperModeJsons()) else {
        log("superwhisper mode \"\(wanted)\" not found; pasteMode ignored")
        return
    }
    let active = readSuperwhisperActiveMode()
    if active != key { log("superwhisper mode: \(key) selected (was \(active ?? "unknown"))") }
    _ = await switchSuperwhisperMode(key, target: nil)
}

/// Clipboard + Cmd+V into the wake-time app. Used when mode auto-paste is off and pane did not take it.
func pasteDictation(_ text: String, target: NSRunningApplication?) -> Bool {
    guard AXIsProcessTrusted(), let target, let idle = MacInputActivity.idleSeconds(), idle >= 1 else { return false }
    let lastInput = ProcessInfo.processInfo.systemUptime - idle
    guard target.activate(), NSWorkspace.shared.frontmostApplication?.processIdentifier == target.processIdentifier else { return false }
    let pb = NSPasteboard.general
    let previous = pb.pasteboardItems ?? []
    var snapshot: [[NSPasteboard.PasteboardType: Data]] = []
    var bytes = 0
    for item in previous {
        var values: [NSPasteboard.PasteboardType: Data] = [:]
        for type in item.types {
            guard let data = item.data(forType: type) else { return false }
            bytes += data.count
            guard bytes <= 8 * 1024 * 1024 else { return false }
            values[type] = data
        }
        snapshot.append(values)
    }
    guard macPasteSafety(target: target, lastInput: lastInput).allowsPaste else { return false }
    pb.clearContents()
    let copied = pb.setString(text, forType: .string)
    let owned = pb.changeCount
    func restore() {
        guard pb.changeCount == owned else { return }
        let items = snapshot.map { values in
            let item = NSPasteboardItem()
            for (type, data) in values { item.setData(data, forType: type) }
            return item
        }
        pb.clearContents()
        if !items.isEmpty { pb.writeObjects(items) }
    }
    guard copied, macPasteSafety(target: target, lastInput: lastInput).allowsPaste,
          let source = CGEventSource(stateID: .hidSystemState),
          let down = CGEvent(keyboardEventSource: source, virtualKey: 0x09, keyDown: true),
          let up = CGEvent(keyboardEventSource: source, virtualKey: 0x09, keyDown: false) else {
        restore()
        return false
    }
    down.flags = .maskCommand
    up.flags = .maskCommand
    down.post(tap: .cghidEventTap)
    up.post(tap: .cghidEventTap)
    DispatchQueue.main.asyncAfter(deadline: .now() + 1) { restore() }
    return true
}

func macPasteSafety(target: NSRunningApplication, lastInput: TimeInterval) -> PasteSafety {
    let app = AXUIElementCreateApplication(target.processIdentifier)
    var focused: CFTypeRef?
    var plain = false
    if AXUIElementCopyAttributeValue(app, kAXFocusedUIElementAttribute as CFString, &focused) == .success,
       let focused, CFGetTypeID(focused) == AXUIElementGetTypeID() {
        let element = focused as! AXUIElement
        var role: CFTypeRef?
        var subrole: CFTypeRef?
        if AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &role) == .success,
           AXUIElementCopyAttributeValue(element, kAXSubroleAttribute as CFString, &subrole) == .success {
            plain = ["AXTextField", "AXTextArea"].contains(role as? String ?? "") && (subrole as? String) != "AXSecureTextField"
        }
    }
    var compositionClear = false
    if let source = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue(),
       let property = TISGetInputSourceProperty(source, kTISPropertyInputSourceType) {
        let type = unsafeBitCast(property, to: CFString.self)
        compositionClear = CFEqual(type, kTISTypeKeyboardLayout)
    }
    let currentInput = MacInputActivity.idleSeconds().map { ProcessInfo.processInfo.systemUptime - $0 }
    return PasteSafety(targetFocused: NSWorkspace.shared.frontmostApplication?.processIdentifier == target.processIdentifier,
        plainTextField: plain && !IsSecureEventInputEnabled(), compositionClear: compositionClear,
        clipboardRestorable: true, inputUnchanged: currentInput.map { abs($0 - lastInput) < 0.02 } ?? false)
}

/// superwhisper transcribes the file. Wake-word audio switches to `dictation.pasteMode` when set (else keeps the active
/// mode) and lets superwhisper paste.
/// Automatic audio switches to `dictation.superwhisperMode`, which must have auto-paste off, and stays in history.
/// An action's text goes only to its URL, so it switches to its own mode or, failing that, `dictation.superwhisperMode`.
/// notDispatched means the caller should fall back to local history; noResult means superwhisper returned no words.
enum HandoffOutcome { case notDispatched, noResult, delivered }

// MARK: sticky target

let stickyTargetKey = "stickyTarget"

func stickyTargetOverride() -> Bool? {
    guard UserDefaults.standard.object(forKey: stickyTargetKey) != nil else { return nil }
    return UserDefaults.standard.bool(forKey: stickyTargetKey)
}

final class StickySession {
    var state: StickyState
    let pid: pid_t
    let bundleID: String?
    let element: AXUIElement?
    let window: AXUIElement?
    let paneID: String?
    let capturedAt: Date
    var journalDelivery: JournalDelivery { paneID == nil ? .sticky : .pane }
    /// bundleID is in macOS.stickyEnterApps.
    var enter = false
    /// The target app's bundle, drawn in the HUD instead of the mic so a sticky dictation shows where its text goes.
    var appPath: String?
    init(state: StickyState, pid: pid_t, bundleID: String?, element: AXUIElement?, window: AXUIElement?, paneID: String?, capturedAt: Date) {
        self.state = state
        self.pid = pid
        self.bundleID = bundleID
        self.element = element
        self.window = window
        self.paneID = paneID
        self.capturedAt = capturedAt
    }
}

func runProcessCapped(executable: String, args: [String], capMs: Int) -> String? {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: executable)
    p.arguments = args
    let out = Pipe()
    p.standardOutput = out
    p.standardError = Pipe()
    do { try p.run() } catch { return nil }
    let deadline = Date().addingTimeInterval(Double(capMs) / 1000)
    while p.isRunning, Date() < deadline {
        Thread.sleep(forTimeInterval: 0.01)
    }
    if p.isRunning {
        p.terminate()
        return nil
    }
    p.waitUntilExit()
    guard p.terminationStatus == 0,
        let text = String(data: out.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines),
        !text.isEmpty
    else { return nil }
    return text
}

func parentPID(_ pid: pid_t) -> pid_t? {
    var info = kinfo_proc()
    var size = MemoryLayout<kinfo_proc>.stride
    var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
    guard sysctl(&mib, 4, &info, &size, nil, 0) == 0, size > 0 else { return nil }
    return info.kp_eproc.e_ppid
}

func descends(_ pid: pid_t, from ancestor: pid_t) -> Bool {
    var p = pid
    for _ in 0..<32 {
        if p == ancestor { return true }
        guard p > 1, let next = parentPID(p) else { return false }
        p = next
    }
    return false
}

/// The pane shown by the tmux client running under the terminal app, found by process ancestry so any terminal
/// works (Ghostty and WezTerm have no tty AppleScript; iTerm's session tty is the client's, never a pane's).
// ponytail: several clients under one app (several windows) pick the one typed in last; a window without tmux beside
// one with tmux still picks the tmux one. Match the focused window title if that shows up.
func stickyTmuxPane(terminalPID: pid_t) -> String? {
    guard let out = runProcessCapped(executable: tmuxExecutable, args: ["list-clients", "-F", "#{client_pid} #{client_activity} #{pane_id}"], capMs: 400) else { return nil }
    var best: (activity: Int, pane: String)?
    for line in out.split(whereSeparator: \.isNewline) {
        let parts = line.split(separator: " ").map(String.init)
        guard parts.count == 3, let pid = pid_t(parts[0]), let activity = Int(parts[1]), isPaneID(parts[2]),
            descends(pid, from: terminalPID)
        else { continue }
        if best == nil || activity > best!.activity { best = (activity, parts[2]) }
    }
    return best?.pane
}

/// argv only, never a shell. stdin feeds load-buffer.
func stickyTmux(_ args: [String], stdin: String? = nil) -> Bool {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: tmuxExecutable)
    p.arguments = args
    let input = Pipe()
    if stdin != nil { p.standardInput = input }
    do { try p.run() } catch {
        log("sticky: tmux \(args[0]) failed to start")
        return false
    }
    if let stdin {
        input.fileHandleForWriting.write(Data(stdin.utf8))
        try? input.fileHandleForWriting.close()
    }
    p.waitUntilExit()
    if p.terminationStatus != 0 {
        log("sticky: tmux \(args[0]) exited \(p.terminationStatus)")
        return false
    }
    return true
}

/// One paste-buffer -p: tmux adds the bracketed-paste marks only when the pane asked for them, and a failure cannot
/// leave an open mark behind.
func stickyPasteToPane(_ id: String, _ text: String) -> Bool {
    let buffer = "voice-switch-sticky"
    return stickyTmux(["load-buffer", "-b", buffer, "-"], stdin: text)
        && stickyTmux(["paste-buffer", "-p", "-d", "-b", buffer, "-t", id])
}

/// An app keeps its focused element in the background, so this alone does not say where CGEvent lands.
func stickyFocusIs(_ pid: pid_t, _ element: AXUIElement) -> Bool {
    let appEl = AXUIElementCreateApplication(pid)
    var focused: CFTypeRef?
    guard AXUIElementCopyAttributeValue(appEl, kAXFocusedUIElementAttribute as CFString, &focused) == .success,
        let focused, CFGetTypeID(focused) == AXUIElementGetTypeID()
    else { return false }
    return CFEqual(focused as! AXUIElement, element)
}

/// activate() can return before, or without, the app coming to the front (macOS 14 cooperative activation).
@MainActor func stickyFrontIs(_ pid: pid_t) async -> Bool {
    for _ in 0..<10 {
        if NSWorkspace.shared.frontmostApplication?.processIdentifier == pid { return true }
        try? await Task.sleep(nanoseconds: 50_000_000)
    }
    return false
}

@MainActor func captureStickyTarget(_ mac: MacOSActions?) -> StickySession? {
    guard stickyEnabled(configFlag: mac?.stickyTarget, override: stickyTargetOverride()) else {
        log("sticky: wake word heard while sticky is off")
        return nil
    }
    guard let front = NSWorkspace.shared.frontmostApplication else {
        let state = stickyStep(StickyState.initial(on: true), .start(.noField))
        return StickySession(state: state, pid: -1, bundleID: nil, element: nil, window: nil, paneID: nil, capturedAt: Date())
    }
    let pid = front.processIdentifier
    let bundle = front.bundleIdentifier
    var kind: StickyKind = .noField
    var element: AXUIElement?
    var window: AXUIElement?
    if !AXIsProcessTrusted() {
        log("sticky: accessibility not trusted, no field")
        kind = .noField
    } else {
        let appEl = AXUIElementCreateApplication(pid)
        var focused: CFTypeRef?
        let focusErr = AXUIElementCopyAttributeValue(appEl, kAXFocusedUIElementAttribute as CFString, &focused)
        if focusErr == .success, let focused, CFGetTypeID(focused) == AXUIElementGetTypeID() {
            let el = focused as! AXUIElement
            var subrole: CFTypeRef?
            let subErr = AXUIElementCopyAttributeValue(el, kAXSubroleAttribute as CFString, &subrole)
            let secureSubrole = subErr == .success && (subrole as? String) == "AXSecureTextField"
            if IsSecureEventInputEnabled() || secureSubrole {
                kind = .secure
                element = el
            } else {
                var role: CFTypeRef?
                let roleErr = AXUIElementCopyAttributeValue(el, kAXRoleAttribute as CFString, &role)
                if roleErr == .success, let r = role as? String, r == "AXTextField" || r == "AXTextArea" {
                    kind = .field
                    element = el
                    var win: CFTypeRef?
                    if AXUIElementCopyAttributeValue(el, kAXWindowAttribute as CFString, &win) == .success,
                        let win, CFGetTypeID(win) == AXUIElementGetTypeID()
                    {
                        window = win as! AXUIElement
                    }
                } else {
                    kind = .noField
                }
            }
        } else {
            kind = .noField
            if IsSecureEventInputEnabled() { kind = .secure }
        }
    }
    var paneID: String?
    // A terminal's AX text area is the same element for every tmux pane, so a field paste would follow the user.
    if kind != .secure, let pane = stickyTmuxPane(terminalPID: pid) {
        kind = .tmux
        paneID = pane
        element = nil
        window = nil
    }
    let state = stickyStep(StickyState.initial(on: true), .start(kind))
    let session = StickySession(state: state, pid: pid, bundleID: bundle, element: element, window: window, paneID: paneID, capturedAt: Date())
    session.enter = bundle.map { (mac?.stickyEnterApps ?? []).contains($0) } ?? false
    session.appPath = front.bundleURL?.path
    log("sticky: captured kind=\(kind) app=\(bundle ?? "-") pane=\(paneID ?? "-") enter=\(session.enter)")
    return session
}

func stickyFallback(_ session: StickySession, text: String, reason: String) {
    if session.state.phase == .delivering {
        session.state = stickyStep(session.state, .pasteFail)
    }
    let pb = NSPasteboard.general
    pb.clearContents()
    _ = pb.setString(text, forType: .string)
    log("sticky: fallback chars=\(text.count) reason=\(reason)")
}

@MainActor func deliverSticky(_ session: StickySession, text: String) async {
    let chars = text.count
    if text.isEmpty {
        session.state = stickyStep(session.state, .stop(false))
        log("sticky: empty chars=0")
        return
    }
    let elapsed = Int(Date().timeIntervalSince(session.capturedAt))
    session.state = stickyAdvanceAge(session.state, max(0, elapsed))
    var gone = false
    if session.pid != -1 {
        if let app = NSRunningApplication(processIdentifier: session.pid) {
            if app.isTerminated { gone = true }
        } else {
            gone = true
        }
    }
    if !gone, let el = session.element {
        var probe: CFTypeRef?
        if AXUIElementCopyAttributeValue(el, kAXRoleAttribute as CFString, &probe) == .invalidUIElement {
            gone = true
        }
    }
    if !gone, let win = session.window {
        var probe: CFTypeRef?
        if AXUIElementCopyAttributeValue(win, kAXRoleAttribute as CFString, &probe) == .invalidUIElement {
            gone = true
        }
    }
    if gone {
        session.state = stickyStep(session.state, .targetGone)
    }
    let frontBefore = NSWorkspace.shared.frontmostApplication
    if let frontBefore, frontBefore.processIdentifier != session.pid {
        session.state = stickyStep(session.state, .switchApp)
    }
    var secureNow = IsSecureEventInputEnabled()
    if !secureNow, let el = session.element {
        var subrole: CFTypeRef?
        if AXUIElementCopyAttributeValue(el, kAXSubroleAttribute as CFString, &subrole) == .success,
            (subrole as? String) == "AXSecureTextField"
        {
            secureNow = true
        }
    }
    session.state = stickyStep(session.state, .stop(true))
    if secureNow {
        if session.state.phase == .delivering {
            session.state = stickyStep(session.state, .pasteFail)
        }
        stickyFallback(session, text: text, reason: "secure")
        return
    }
    guard stickyMayPaste(session.state) else {
        stickyFallback(session, text: text, reason: "not-pasteable")
        return
    }
    if session.state.kind == .tmux, let pane = session.paneID {
        guard stickyPasteToPane(pane, text) else {
            session.state = stickyStep(session.state, .pasteFail)
            stickyFallback(session, text: text, reason: "tmux-send-failed")
            return
        }
        session.state = stickyStep(session.state, .pasteOk)
        if stickyMayEnter(session.state) {
            if !session.enter || stickyTmux(["send-keys", "-t", pane, "Enter"]) {
                // The model has no paste-only end: enterOk without the key ends delivery the same way.
                session.state = stickyStep(session.state, .enterOk)
            } else {
                // Already pasted, so the clipboard copy would only invite a second paste.
                session.state = stickyStep(session.state, .enterFail)
            }
        }
        log("sticky: tmux chars=\(chars) enter=\(session.enter)")
        return
    }
    guard session.pid != -1,
        let target = NSRunningApplication(processIdentifier: session.pid),
        !target.isTerminated,
        let element = session.element
    else {
        session.state = stickyStep(session.state, .pasteFail)
        stickyFallback(session, text: text, reason: "target-gone")
        return
    }
    _ = target.activate()
    if let win = session.window {
        AXUIElementPerformAction(win, kAXRaiseAction as CFString)
    }
    AXUIElementSetAttributeValue(element, kAXFocusedAttribute as CFString, kCFBooleanTrue)
    func backToUser() {
        if let back = frontBefore, back.processIdentifier != session.pid, !back.isTerminated { _ = back.activate() }
    }
    func refuse(_ reason: String) {
        session.state = stickyStep(session.state, .pasteFail)
        stickyFallback(session, text: text, reason: reason)
        backToUser()
    }
    // CGEvent goes to whatever is frontmost, so the target must be front, not only focused inside its own app.
    guard await stickyFrontIs(session.pid), stickyFocusIs(session.pid, element) else {
        refuse("focus-mismatch")
        return
    }
    let pb = NSPasteboard.general
    let previous = pb.pasteboardItems ?? []
    var snapshot: [[NSPasteboard.PasteboardType: Data]] = []
    var bytes = 0
    var restorable = true
    for item in previous {
        var values: [NSPasteboard.PasteboardType: Data] = [:]
        for type in item.types {
            guard let data = item.data(forType: type) else { restorable = false; break }
            bytes += data.count
            if bytes > 8 * 1024 * 1024 { restorable = false; break }
            values[type] = data
        }
        if !restorable { break }
        snapshot.append(values)
    }
    if !restorable {
        refuse("clipboard-snapshot")
        return
    }
    pb.clearContents()
    guard pb.setString(text, forType: .string) else {
        refuse("clipboard-set")
        return
    }
    let owned = pb.changeCount
    func postKey(_ key: Int64, flags: CGEventFlags) -> Bool {
        guard let source = CGEventSource(stateID: .hidSystemState),
            let down = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(key), keyDown: true),
            let up = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(key), keyDown: false)
        else { return false }
        down.flags = flags
        up.flags = flags
        down.post(tap: .cghidEventTap)
        up.post(tap: .cghidEventTap)
        return true
    }
    // Rechecked at each key: the user can switch apps or land in a secure field between the steps.
    func stillTarget() -> Bool {
        NSWorkspace.shared.frontmostApplication?.processIdentifier == session.pid && !IsSecureEventInputEnabled()
            && stickyFocusIs(session.pid, element)
    }
    guard stillTarget(), postKey(0x09, flags: .maskCommand) else {
        refuse("front-changed")
        return
    }
    try? await Task.sleep(nanoseconds: 80_000_000)
    var probe: CFTypeRef?
    if AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &probe) == .invalidUIElement {
        session.state = stickyStep(session.state, .pasteOk)
        session.state = stickyStep(session.state, .enterFail)
        log("sticky: field chars=\(chars) reason=element-gone")
        backToUser()
        return
    }
    session.state = stickyStep(session.state, .pasteOk)
    if stickyMayEnter(session.state) {
        if !session.enter {
            // The model has no paste-only end: enterOk without the key ends delivery the same way.
            session.state = stickyStep(session.state, .enterOk)
        } else if stillTarget(), postKey(0x24, flags: []) {
            session.state = stickyStep(session.state, .enterOk)
        } else {
            session.state = stickyStep(session.state, .enterFail)
            log("sticky: field chars=\(chars) reason=enter-front-changed")
        }
    }
    if session.state.phase == .delivered {
        let items = snapshot.map { values -> NSPasteboardItem in
            let item = NSPasteboardItem()
            for (type, data) in values { item.setData(data, forType: type) }
            return item
        }
        let capturedOwned = owned
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            if pb.changeCount == capturedOwned {
                pb.clearContents()
                if !items.isEmpty { pb.writeObjects(items) }
            }
        }
    }
    log("sticky: field chars=\(chars) enter=\(session.enter)")
    if session.state.phase == .delivered, session.state.focus == .user, let back = frontBefore, !back.isTerminated {
        if !back.activate() {
            log("sticky: focus return failed")
        }
    }
}

func handoff(_ samples: [Float], cfg: DictationConfig, target: NSRunningApplication?, action: WakeAction?, origin: WakeOrigin = WakeOrigin(rule: .strict, inputIdleSeconds: nil), sessionID: UUID = UUID(), transcriptPolicy: AutomaticTranscriptPolicy = .disabled, stillCurrent: () -> Bool = { true }, sticky: StickySession? = nil) async -> HandoffOutcome {
    if DictationHistory.shared.select(sessionID) != nil { return .delivered }
    guard let ownership = await acquireModeOwnership() else {
        log("superwhisper handoff: not dispatched, mode lock not acquired within 45 s \(sidField(sessionID))"); return .notDispatched
    }
    defer { withExtendedLifetime(ownership) {} }
    let historyGeneration = DictationHistory.shared.generation
    let recordings = Platform.expandPath(cfg.recordingsDir ?? Platform.defaultRecordingsDir)
    if (try? MacModeRestoreStore.read()) != nil, !(await awaitRunsSettled(in: recordings)) {
        log("superwhisper mode: an earlier run is still in flight; recording goes to local history \(sidField(sessionID))")
        log("superwhisper handoff: not dispatched, an earlier run is still in flight \(sidField(sessionID))")
        return .notDispatched
    }
    guard await recoverSuperwhisperMode(target: target) else {
        log("superwhisper handoff: not dispatched, earlier mode switch not recovered \(sidField(sessionID))"); return .notDispatched
    }
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent("voice-switch")
    let wav = dir.appendingPathComponent("\(UUID().uuidString).wav")
    defer { try? FileManager.default.removeItem(at: wav) }
    let submitted = Date()
    // Nobody asked for an automatic recording, so it may only enter a mode verified not to paste.
    let controlled = origin.isAutomatic
    let stickySession = sticky
    let wantedMode = controlled ? cfg.superwhisperMode : action == nil ? nil : action?.superwhisperMode ?? cfg.superwhisperMode
    let enteredMode = stickySession != nil ? cfg.superwhisperMode : wantedMode
    if stickySession != nil {
        guard superwhisperAutoPasteDisabled(enteredMode, modeJsons: readSuperwhisperModeJsons()) else {
            log("superwhisper handoff: not dispatched, sticky mode \(enteredMode ?? "-") missing or has auto-paste on \(sidField(sessionID))"); return .notDispatched
        }
    } else if controlled {
        guard superwhisperAutoPasteDisabled(wantedMode, modeJsons: readSuperwhisperModeJsons()) else {
            log("superwhisper handoff: not dispatched, mode \(wantedMode ?? "-") missing or has auto-paste on \(sidField(sessionID))"); return .notDispatched
        }
    }
    if stickySession == nil, !controlled, action == nil, let mode = cfg.pasteMode { await selectPasteMode(mode) }
    let entered: (requested: Bool, previous: String?)
    if stickySession == nil {
        entered = await enterSuperwhisperMode(wantedMode, target: target)
    } else {
        entered = await enterSuperwhisperMode(enteredMode, target: target)
    }
    let (modeRequested, previousMode) = entered
    if stickySession != nil {
        guard modeRequested, superwhisperAutoPasteDisabled(enteredMode, modeJsons: readSuperwhisperModeJsons()) else {
            log("superwhisper handoff: not dispatched, sticky mode \(enteredMode ?? "-") not entered (requested=\(modeRequested)) or has auto-paste on \(sidField(sessionID))")
            _ = await recoverSuperwhisperMode(target: target)
            return .notDispatched
        }
    } else if controlled {
        guard modeRequested, superwhisperAutoPasteDisabled(wantedMode, modeJsons: readSuperwhisperModeJsons()) else {
            log("superwhisper handoff: not dispatched, mode \(wantedMode ?? "-") not entered (requested=\(modeRequested)) or has auto-paste on \(sidField(sessionID))")
            _ = await recoverSuperwhisperMode(target: target)
            return .notDispatched
        }
    }
    let previousRuns: Set<String>
    do {
        previousRuns = FileManager.default.fileExists(atPath: recordings)
            ? Set(try FileManager.default.contentsOfDirectory(atPath: recordings)) : []
    } catch {
        log("superwhisper handoff: not dispatched, recordings folder unreadable \(sidField(sessionID))")
        _ = await recoverSuperwhisperMode(target: target)
        return .notDispatched
    }
    var result: String?
    var launched = false
    let trimmedForHandoff = Array(trimQuietHead(samples, rate: rate))
    do {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try writeWAV(trimmedForHandoff, to: wav)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = ["-g", "-a", "superwhisper", wav.path]
        // Exit 0 means LaunchServices accepted the file, not that superwhisper ran; the run lines from awaitResult say that.
        p.terminationHandler = { log("superwhisper handoff: open exited status=\($0.terminationStatus) \(sidField(sessionID))") }
        log("superwhisper handoff: open audio=\(String(format: "%.2f", locale: Locale(identifier: "en_US_POSIX"), Double(trimmedForHandoff.count) / rate))s mode=\(enteredMode ?? "active") \(sidField(sessionID))")
        try p.run()
        launched = true
        // Opening a file brings superwhisper to the front despite -g, and it skips auto-paste when it is
        // still frontmost at the end, so hand focus back to where the user was dictating.
        if stickySession == nil {
            for _ in 0 ..< 20 {
                try? await Task.sleep(nanoseconds: 100_000_000)
                await restoreFocusIfSuperwhisper(target)
            }
        }
        let waitForLLM = previousMode != nil && superwhisperModeUsesLLM(enteredMode, modeJsons: readSuperwhisperModeJsons())
        result = await awaitResult(in: recordings, since: Int(submitted.timeIntervalSince1970) - 2, excluding: previousRuns, waitForLLM: waitForLLM,
                                   onRun: { log("superwhisper run: \($0) after \(Int(Date().timeIntervalSince(submitted) * 1000)) ms \(sidField(sessionID))") })
    } catch {
        log(cfg.noWakeWord?.enabled == true ? "dictation: handing off to superwhisper failed" : "dictation: handing off to superwhisper failed: \(error)")
    }
    // Restoring while superwhisper still works on the file lets it paste in the restored mode. With no judged run the
    // recovery state stays, and the next handoff or launch restores it once nothing is in flight.
    if previousMode != nil {
        if result != nil || !launched { _ = await recoverSuperwhisperMode(target: target) }
        else { log("superwhisper mode: kept \(enteredMode ?? "") until the run finishes \(sidField(sessionID))") }
    }
    guard launched else { return .notDispatched }
    guard let result else {
        log("dictation: no superwhisper result within 30 s \(sidField(sessionID))"); return .noResult
    }
    guard !result.isEmpty else {
        log("dictation: superwhisper heard no words \(sidField(sessionID))"); return .noResult
    }
    log("dictation: \(result.count) chars in \(Int(Date().timeIntervalSince(submitted) * 1000)) ms audio=\(String(format: "%.2f", locale: Locale(identifier: "en_US_POSIX"), Double(samples.count) / rate))s \(sidField(sessionID))")
    guard stillCurrent() else { return .delivered }
    let delivery: JournalDelivery? = stickySession?.journalDelivery ?? (controlled ? .history : action != nil ? .action : dictationMayRouteToPane(result) ? nil : .paste)
    DictationJournal.note(sessionID, delivery: delivery)
    guard DictationHistory.shared.add(id: sessionID, text: result, origin: origin, expectedGeneration: historyGeneration, policy: transcriptPolicy) else { return .delivered }
    if let stickySession {
        await deliverSticky(stickySession, text: result)
        return .delivered
    }
    if controlled { return .delivered }
    if let action {
        _ = await executeWakeAction(action, text: result, app: target?.bundleIdentifier)
        return .delivered
    }
    let route = await routeDictation(result)
    // Wake-word audio runs in the active mode or pasteMode, both auto-paste, so superwhisper has already pasted unless the pane took it.
    if decideDictationDelivery(modeRequested: false, route: route.disposition) == .superwhisper {
        log("dictation delivered: superwhisper")
    }
    return .delivered
}

func executeWakeAction(_ action: WakeAction, text: String, app: String?, wait: Bool = false, quiet: Bool = false,
                       onFailure: ((String) -> Void)? = nil) async -> Bool {
    switch action.delivery {
    case .post(let url):
        do {
            try await postAction(action, to: url, text: text)
            if !quiet { log("dictation delivered: post \(action.name)") }
            return true
        } catch {
            if !quiet { log("dictation action \(action.name) failed: \(error.localizedDescription)") }
            let failure = error as? PostActionError
            onFailure?(failure?.reason ?? "POST 通信失敗")
            return false
        }
    case .open(let url):
        if !quiet, GrokBotPrompt.isPrompt(url.rawValue), GrokBotPrompt.fit(text).1 {
            log("grokbot prompt text truncated to 1000 encoded chars for action \(action.name)")
        }
        let process = Platform.urlProcess(url)
        process.arguments = action.openArguments(input: text)
        return Platform.runURLProcess(process, wait: wait, quiet: quiet, onFailure: onFailure)
    case .command(let command):
        let sent = Platform.runCommand(command, stdin: text, environment: ["VOICE_SWITCH_APP": app ?? ""],
                                       wait: wait, quiet: quiet, onFailure: onFailure)
        if !quiet { log("dictation delivered: command \(action.name)") }
        return sent
    }
}

private enum PostActionError: LocalizedError {
    case missingToken(String)
    case invalidResponse
    case http(Int, String)

    var reason: String {
        switch self {
        case .missingToken: return "POST 認証設定なし"
        case .invalidResponse: return "POST の HTTP 応答なし"
        case .http(let status, _): return "HTTP \(status)"
        }
    }

    var errorDescription: String? {
        switch self {
        case .missingToken(let env): return "environment variable \(env) is not set"
        case .http(_, let reply): return "\(reason) \(reply)"
        case .invalidResponse: return reason
        }
    }
}

/// POSTs `text` to the action's https url as the multipart fields from `postFields` with the bearer token from `tokenEnv`; the token is never in the config.
func postAction(_ action: WakeAction, to url: ActionURL, text: String) async throws {
    guard let env = action.tokenEnv, let token = ProcessInfo.processInfo.environment[env] else {
        throw PostActionError.missingToken(action.tokenEnv ?? "")
    }
    let boundary = "voice-switch-\(UUID().uuidString)"
    var body = Data()
    for (name, value) in action.postFields(text: text, recordedAtMs: Int64(Date().timeIntervalSince1970 * 1000)) {
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
    }
    body.append(Data("--\(boundary)--\r\n".utf8))
    var request = URLRequest(url: URL(string: url.rawValue)!, timeoutInterval: 30)
    request.httpMethod = "POST"
    request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
    request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
    let (data, response) = try await URLSession.shared.upload(for: request, from: body)
    guard let status = (response as? HTTPURLResponse)?.statusCode else { throw PostActionError.invalidResponse }
    if !(200 ..< 300).contains(status) {
        // The status alone cannot tell a bad field from a receiver fault; the head of the reply can.
        let reply = String(decoding: data.prefix(300), as: UTF8.self)
        throw PostActionError.http(status, reply)
    }
}

/// The mode lock is per user and non-blocking, so wait for a handoff already in flight instead of dropping this one.
// ponytail: 100 ms polling for up to 45 s, longer than one handoff's 30 s result wait; a blocking flock on a worker thread if it ever matters.
func acquireModeOwnership() async -> MacModeOwnership? {
    for _ in 0 ..< 450 {
        if let ownership = try? MacModeRestoreStore.acquire() { return ownership }
        try? await Task.sleep(nanoseconds: 100_000_000)
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
    enum Command { case finish, cancel, start }
    struct Shortcut: Equatable { var keyCode: Int64; var flags: CGEventFlags.RawValue }
    struct State {
        var active = false
        var shortcuts: [(Shortcut, Command)] = []
        var pending: Command?
        var pendingAction: String?
        var swallowUp: Set<Int64> = [] // keyUps of swallowed presses, so superwhisper never sees half a press
        var loggedMissing: Set<String> = []
    }

    static let state = OSAllocatedUnfairLock(initialState: State())
    static let relevantFlags: CGEventFlags = [.maskCommand, .maskShift, .maskAlternate, .maskControl]
    static let escape = Shortcut(keyCode: 53, flags: 0)
    nonisolated(unsafe) static var tap: CFMachPort?
    nonisolated(unsafe) static var triedTap = false

    /// Called when a dictation starts: re-read the shortcuts (the user may have changed them) and arm the tap.
    static func begin(finishOnly: Bool = false) {
        let defaults = UserDefaults(suiteName: "com.superduper.superwhisper")
        let keys = [("KeyboardShortcuts_toggleRecording", Command.finish), ("KeyboardShortcuts_cancelRecording", .cancel)]
        // superwhisper stores a shortcut only once the user changes it; its default cancel, Escape, is never written.
        let read = keys.prefix(finishOnly ? 1 : 2).map {
            (key: $0.0, command: $0.1, shortcut: defaults?.string(forKey: $0.0).flatMap(parseShortcut) ?? ($0.1 == .cancel ? escape : nil))
        }
        let shortcuts = read.compactMap { r in r.shortcut.map { ($0, r.command) } }
        let missing = read.filter { $0.shortcut == nil }.map(\.key)
        let newlyMissing = state.withLock { st in
            st.shortcuts = shortcuts; st.pending = nil; st.pendingAction = nil; st.active = true
            defer { st.loggedMissing.formUnion(missing) }
            return missing.filter { !st.loggedMissing.contains($0) }
        }
        for key in newlyMissing { log("hotkey: superwhisper shortcut \(key) missing or unreadable, that key is off") }
        DispatchQueue.main.async(execute: installTap)
    }

    static func end() { state.withLock { $0.active = false; $0.pending = nil; $0.pendingAction = nil } }

    static func requestStart() { state.withLock { $0.pending = .start; $0.pendingAction = nil } }

    static func requestAction(_ name: String) { state.withLock { $0.pending = .start; $0.pendingAction = name } }

    static func take() -> Command? {
        state.withLock { st in
            let command = st.pending
            st.pending = nil
            if command != .start { st.pendingAction = nil }
            return command
        }
    }

    static func takeAction() -> String? {
        state.withLock { st in
            defer { st.pendingAction = nil }
            return st.pendingAction
        }
    }

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

    static func installTap() {
        guard !triedTap else { return }
        triedTap = true
        guard AXIsProcessTrusted() else {
            triedTap = false
            log("hotkey interception disabled: Accessibility is not granted")
            return
        }
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
        guard AXIsProcessTrusted() else { return Unmanaged.passUnretained(event) }
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

private func audioProperty<T>(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector, _ initial: T,
                              scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> T? {
    var addr = AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    var value = initial
    var size = UInt32(MemoryLayout<T>.size)
    return AudioObjectGetPropertyData(id, &addr, 0, nil, &size, &value) == noErr ? value : nil
}

private func audioObjectIDs(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector,
                            scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> [AudioObjectID] {
    var addr = AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(id, &addr, 0, nil, &size) == noErr, size > 0 else { return [] }
    var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
    guard AudioObjectGetPropertyData(id, &addr, 0, nil, &size, &ids) == noErr else { return [] }
    return ids
}

/// Physical microphones only. While voice processing runs, every output device grows an echo-reference input
/// stream (terminal type 'spkr' or 0) and an aggregate device appears; pinning one of those fails with -10851.
func inputDeviceIDs() -> [AudioDeviceID] {
    audioObjectIDs(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDevices).filter { id in
        audioProperty(id, kAudioDevicePropertyTransportType, UInt32(0)) != kAudioDeviceTransportTypeAggregate
            && audioObjectIDs(id, kAudioDevicePropertyStreams, scope: kAudioDevicePropertyScopeInput).contains { stream in
                let terminal = audioProperty(stream, kAudioStreamPropertyTerminalType, UInt32(0)) ?? 0
                return terminal != 0 && terminal != kAudioStreamTerminalTypeSpeaker
            }
    }
}

func defaultInputDeviceID() -> AudioDeviceID? {
    audioProperty(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDefaultInputDevice, AudioDeviceID(0)).flatMap { $0 == 0 ? nil : $0 }
}

func builtInInputDeviceID() -> AudioDeviceID? {
    inputDeviceIDs().first { audioProperty($0, kAudioDevicePropertyTransportType, UInt32(0)) == kAudioDeviceTransportTypeBuiltIn }
}

func audioDeviceUID(_ id: AudioDeviceID) -> String? {
    audioProperty(id, kAudioDevicePropertyDeviceUID, nil as Unmanaged<CFString>?).flatMap { $0?.takeRetainedValue() as String? }
}

func audioDeviceName(_ id: AudioDeviceID) -> String {
    audioProperty(id, kAudioObjectPropertyName, nil as Unmanaged<CFString>?).flatMap { $0?.takeRetainedValue() as String? } ?? "device \(id)"
}

/// Where start() takes audio from. Both are raw input: voice processing changed the mic for every other app
/// (superwhisper recorded silence, measured 2026-10-28), so echo cancellation comes from HAL voice activity detection instead.
enum InputRoute: Equatable {
    case systemDefault
    case pinned(AudioDeviceID)

    var deviceID: AudioDeviceID? {
        switch self {
        case .systemDefault: return defaultInputDeviceID()
        case .pinned(let id): return id
        }
    }

    /// The first plugged-in candidate (a device UID or exact name) is used, then the built-in mic; an unplugged one
    /// stays in the list. No candidates means the system default. fallback names the rung taken, nil for the first.
    static func resolve(candidates: [String]) -> (route: InputRoute, fallback: String?) {
        guard !candidates.isEmpty else { return (.systemDefault, nil) }
        let plugged = inputDeviceIDs()
        for (rank, candidate) in candidates.enumerated() {
            if let id = plugged.first(where: { audioDeviceUID($0) == candidate || audioDeviceName($0) == candidate }) {
                return (.pinned(id), rank == 0 ? nil : "fallback \(rank)")
            }
        }
        guard let builtIn = builtInInputDeviceID() else { return (.systemDefault, "system default") }
        return (.pinned(builtIn), "built-in")
    }
}

/// Owns the microphone. stop() releases it fully (menu-bar mic indicator goes off).
final class Listener {
    let journalFallback: String?
    let config: ConfigFile
    private var engine = AVAudioEngine()
    private let localHistoryState = OSAllocatedUnfairLock(initialState: (busy: false, generation: 0))
    private typealias CaptureFrame = (samples: [Float], generation: Int)
    private let cont: AsyncStream<CaptureFrame>.Continuation
    /// The icon rides with .waiting only: the action the wake word chose, so the HUD shows which mode opened.
    private let phaseCont: AsyncStream<(phase: Phase, icon: String?)>.Continuation
    private var pending: [Float] = []
    private(set) var running = false
    /// Capture is enabled; stays true while a failed restart is being retried, unlike `running`.
    private(set) var wanted = false
    /// Called on the main thread after each restart attempt so the menu bar can follow `running`.
    var onRestart: (() -> Void)?
    var onInputDevicesChanged: (() -> Void)?
    private var restartError: String?
    private var retryScheduled = false
    private var rerouteScheduled = false
    /// The device supplying HAL voice activity (0 = unavailable), when its current voice began,
    /// and when the last voice long enough to count ended.
    private let voiceActivity = OSAllocatedUnfairLock(initialState: (device: AudioDeviceID(0), onSince: Date?.none, lastVoiceEnd: Date.distantPast))
    private var voiceActivityListened: Set<AudioDeviceID> = []
    /// When the tap last delivered audio. Display sleep can make coreaudiod drop the input without
    /// AVAudioEngineConfigurationChange (2026-10-13 log), and every end condition counts frames, so a dictation open then never ends.
    private let lastBuffer = OSAllocatedUnfairLock(initialState: Date())
    private var stallRestarts = 0
    /// Monotonic recognition counter for complete lines (Windows id=N parity), starting at 1.
    private let nextRecognitionId = OSAllocatedUnfairLock(initialState: 1)
    private func takeRecognitionId() -> Int {
        nextRecognitionId.withLock { state in let id = state; state += 1; return id }
    }
    /// CoreAudio device UID; nil follows the system default input. Set only while stopped.
    var deviceUID: String?
    var fallbackUIDs: [String] = []
    /// config.json microphones; set, it replaces deviceUID and fallbackUIDs.
    var configuredMicrophones: [String]?
    private var candidates: [String] { configuredMicrophones ?? deviceUID.map { [$0] + fallbackUIDs } ?? [] }
    private var activeRoute: InputRoute?
    /// The mic being recorded from, for the menu; nil while not listening.
    var activeDeviceName: String? { running ? activeRoute?.deviceID.map(audioDeviceName) : nil }

    init(config: ConfigFile, journalFallback: String? = nil) {
        self.config = config
        self.journalFallback = journalFallback
        let (stream, cont) = AsyncStream<CaptureFrame>.makeStream()
        self.cont = cont
        let (phases, phaseCont) = AsyncStream<(phase: Phase, icon: String?)>.makeStream()
        self.phaseCont = phaseCont
        Task { await self.consume(stream) }
        Task { @MainActor [weak self] in for await next in phases { self?.onPhase?(next.phase, next.icon) } }
        // Input device switches stop the tap silently; restart on the engine's own signal.
        NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: nil, queue: .main) { [weak self] note in
            guard let self, self.wanted, note.object as? AVAudioEngine === self.engine else { return }
            log("audio configuration changed, restarting input")
            self.restart()
        }
        // Unplugging the saved mic moves to the built-in one and plugging it back returns to it; neither always posts a configuration change.
        for selector in [kAudioHardwarePropertyDevices, kAudioHardwarePropertyDefaultInputDevice] {
            var addr = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
            AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &addr, .main) { [weak self] _, _ in
                guard let self else { return }
                // Apply the connection hold before a route change can reopen capture.
                if selector == kAudioHardwarePropertyDevices { self.onInputDevicesChanged?() }
                // An unplug posts several notices within ~0.3 s. Restarting on each one reached AVAudioEngine.inputNode
                // while coreaudiod was still removing the device, and the main thread hung there (2026-11-20).
                guard !self.rerouteScheduled else { return }
                self.rerouteScheduled = true
                DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
                    self?.rerouteScheduled = false
                    self?.reroute("input devices changed")
                }
            }
        }
        Timer.scheduledTimer(withTimeInterval: 10, repeats: true) { [weak self] _ in self?.checkStall() }
        // Loading Whisper and compiling its Metal kernels takes seconds; pay it now, not in front of the first automatic recording.
        if config.cfg.dictation?.noWakeWord?.enabled == true {
            Task.detached(priority: .utility) {
                do { _ = try WhisperGate.transcribe([Float](repeating: 0, count: Int(rate))); log("whisper: ready") }
                catch { log("whisper: unavailable, automatic recordings skip word agreement: \(error)") }
            }
        }
    }

    /// Restarts only when the saved mic and fallback list now pick another device than the one in use.
    func reroute(_ reason: String) {
        guard wanted, InputRoute.resolve(candidates: candidates).route != activeRoute else { return }
        log("\(reason), restarting input")
        restart()
    }

    private func checkStall() {
        guard running else { return }
        let silentFor = Date().timeIntervalSince(lastBuffer.withLock { $0 })
        if silentFor < 10 { stallRestarts = 0; return }
        // ponytail: backoff doubles to 320 s so a long display sleep does not restart the engine every 10 s.
        guard silentFor >= 10 * pow(2, Double(min(stallRestarts, 5))) else { return }
        log("input stalled: no audio for \(Int(silentFor)) s, restarting")
        stallRestarts += 1
        halt(); activeRoute = nil; attemptStart()
    }

    func start() throws {
        let (route, fallback) = InputRoute.resolve(candidates: candidates)
        // A pinned unit never follows the system default again, so a different route needs a fresh engine.
        if route != activeRoute { engine = AVAudioEngine(); activeRoute = route }
        if case .pinned(var id) = route {
            guard let au = engine.inputNode.audioUnit else { throw NSError(domain: "pin", code: 1) }
            var current = AudioDeviceID(0)
            var size = UInt32(MemoryLayout<AudioDeviceID>.size)
            AudioUnitGetProperty(au, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &current, &size)
            // Setting it posts AVAudioEngineConfigurationChange; skipping a no-op set keeps restarts from looping.
            if current != id {
                let status = AudioUnitSetProperty(au, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &id, size)
                if status != noErr { throw NSError(domain: NSOSStatusErrorDomain, code: Int(status)) }
            }
        }
        let deviceName = route.deviceID.map(audioDeviceName) ?? "system default"
        if let fallback, let uid = candidates.first { log("input device \(uid) not found, falling back to \(deviceName) (\(fallback))") }
        let node = engine.inputNode
        // outputFormat stays at the previous device's rate after CurrentDevice is switched
        // (48 kHz vs the webcam's 16 kHz), and a tap with that format gets no buffers at all.
        let fmt = node.inputFormat(forBus: 0)
        guard let conv = AVAudioConverter(from: fmt, to: work) else {
            throw NSError(domain: "convert", code: 1, userInfo: [NSLocalizedDescriptionKey: "input format \(fmt.sampleRate) Hz, \(fmt.channelCount) ch"])
        }
        pending = []
        node.removeTap(onBus: 0)
        let captureGeneration = localHistoryState.withLock { $0.generation }
        lastBuffer.withLock { $0 = Date() }
        node.installTap(onBus: 0, bufferSize: 4096, format: fmt) { [self] buf, _ in
            lastBuffer.withLock { $0 = Date() }
            guard let out = convert(buf, with: conv, flush: false) else { return }
            pending += UnsafeBufferPointer(start: out.floatChannelData![0], count: Int(out.frameLength))
            while pending.count >= frameLen {
                cont.yield((Array(pending.prefix(frameLen)), captureGeneration))
                pending.removeFirst(frameLen)
            }
        }
        engine.prepare()
        try engine.start()
        running = true
        wanted = true
        watchVoiceActivity(on: route.deviceID)
        log("listening on \(deviceName) at \(fmt.sampleRate) Hz for \(config.cfg.wakeWords)")
        let detectorOn = voiceActivity.withLock { $0.device != 0 }
        let gateOn = detectorOn && config.cfg.dictation?.echoCancellation != false
        log("voice activity: HAL detector=\(detectorOn ? "on" : "off") gate=\(gateOn ? "on" : "off") minVoiceSeconds=\(Listener.minVoiceSeconds) on \(deviceName)")
    }

    /// Windows twin: MicCapture.Open. HAL voice activity detection runs on an echo-cancelled copy of the input,
    /// so speaker playback never turns it on (0 of 240 polls, measured 2026-10-28), and other apps keep the raw mic.
    private func watchVoiceActivity(on id: AudioDeviceID?) {
        let previous = voiceActivity.withLock { state in
            let old = state.device
            state = (0, nil, .distantPast)
            return old
        }
        if previous != 0, previous != id { setVoiceActivityDetection(previous, on: false) }
        guard let id else { return }
        guard setVoiceActivityDetection(id, on: true) else {
            log("echo cancellation unavailable, raw input: no voice activity detection on \(audioDeviceName(id))")
            return
        }
        voiceActivity.withLock { $0.device = id }
        guard voiceActivityListened.insert(id).inserted else { return }
        var addr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyVoiceActivityDetectionState,
                                              mScope: kAudioDevicePropertyScopeInput, mElement: kAudioObjectPropertyElementMain)
        // The listener fires once per transition, so the state is kept here rather than polled.
        AudioObjectAddPropertyListenerBlock(id, &addr, .main) { [weak self] _, _ in
            guard let self, let on = audioProperty(id, kAudioDevicePropertyVoiceActivityDetectionState, UInt32(0),
                                                    scope: kAudioDevicePropertyScopeInput) else { return }
            self.voiceActivity.withLock { state in
                guard state.device == id else { return }
                let now = Date()
                if on != 0 {
                    if state.onSince == nil { state.onSince = now }
                } else {
                    if let since = state.onSince, now.timeIntervalSince(since) >= Listener.minVoiceSeconds { state.lastVoiceEnd = now }
                    state.onSince = nil
                }
            }
        }
    }

    @discardableResult
    private func setVoiceActivityDetection(_ id: AudioDeviceID, on: Bool) -> Bool {
        var addr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyVoiceActivityDetectionEnable,
                                              mScope: kAudioDevicePropertyScopeInput, mElement: kAudioObjectPropertyElementMain)
        var value: UInt32 = on ? 1 : 0
        return AudioObjectHasProperty(id, &addr)
            && AudioObjectSetPropertyData(id, &addr, 0, nil, UInt32(MemoryLayout<UInt32>.size), &value) == noErr
    }

    // ponytail: fixed threshold. Speaker playback still blipped the detector for 725–1236 ms in 3 of 6 runs while a voice
    // from another source held it for 8000 ms (2026-10-28); a late start keeps the phrase from its preroll.
    // Make it a config value if short live phrases get dropped.
    static let minVoiceSeconds = 1.5
    static let noVoiceActivity = "no voice activity (speaker playback is not a voice)"
    static func micHeldBy(_ busy: String) -> String { "\(busy) is using the microphone" }

    private func voiceBeingHeard() -> Bool {
        voiceActivity.withLock { $0.device != 0 && $0.onSince != nil }
    }

    /// True when no gate is set, or the detector has heard one voice for minVoiceSeconds now or within the last 1.5 s.
    private func voiceRecentlyActive() -> Bool {
        voiceActivity.withLock { state in
            let now = Date()
            if state.device == 0 { return true }
            if let since = state.onSince, now.timeIntervalSince(since) >= Listener.minVoiceSeconds { return true }
            return now.timeIntervalSince(state.lastVoiceEnd) < 1.5
        }
    }

    /// realtime paces frames like the mic does, which matters when the consumer awaits mid-stream.
    func feed(_ samples: [Float], realtime: Bool = false) async {
        let captureGeneration = localHistoryState.withLock { $0.generation }
        for i in stride(from: 0, to: samples.count - frameLen + 1, by: frameLen) {
            cont.yield((Array(samples[i ..< i + frameLen]), captureGeneration))
            if realtime { try? await Task.sleep(nanoseconds: 30_000_000) }
        }
    }

    func stop() { wanted = false; halt(); watchVoiceActivity(on: nil) }

    /// Automatic resume keeps `wanted` on failure and retries every 10 s.
    func startRetrying() { wanted = true; attemptStart() }

    private func halt() {
        let generation = localHistoryState.withLock { state in state.generation += 1; return state.generation }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        running = false
        cont.yield(([], generation)) // tells consume() to drop any half-collected utterance
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
        var action: WakeAction? = nil
        let origin: WakeOrigin
        let transcriptPolicy: AutomaticTranscriptPolicy
        let sessionID = UUID()
        let captureGeneration: Int
        let historyGeneration = DictationHistory.shared.generation
        /// Whether Apple Speech and Whisper agreed on words in it. An automatic recording records from its first sound but shows
        /// the recording HUD only once confirmed, so a cough no longer flashes it; one the user started is confirmed from the start.
        var confirmed: Bool
        /// Length of `samples` when the last agreement check started.
        var checkedSamples = 0
        /// Length of `samples` when the last stop check over its newest seconds started.
        var stopCheckedSamples = 0
        /// The newest stop check ended on a stop word too recent to stop on; the HUD shows the stop already.
        var stopHeard = false
        var sticky: StickySession? = nil
        init(samples: [Float], generation: Int, transcriptPolicy: AutomaticTranscriptPolicy, silentFrames: Int, heardSpeech: Bool, action: WakeAction? = nil,
             origin: WakeOrigin = WakeOrigin(rule: .strict, inputIdleSeconds: MacInputActivity.idleSeconds()), sticky: StickySession? = nil) {
            self.samples = samples
            self.captureGeneration = generation
            self.silentFrames = silentFrames
            self.heardSpeech = heardSpeech
            self.action = action
            self.origin = origin
            self.transcriptPolicy = transcriptPolicy
            self.sticky = sticky
            confirmed = !origin.isAutomatic
        }
    }

    /// `stopping`: a stop word was heard and the dictation is waiting the moment that confirms it.
    enum Phase { case idle, waiting, recording, stopping, ended, followup }

    /// Called on the main thread whenever the dictation phase changes.
    var onPhase: ((Phase, String?) -> Void)?

    /// Called on the main thread while superwhisper processes a handed-off recording; nil when it is done.
    var onDelivery: ((DeliveryState?) -> Void)?

    private func reportDelivery(_ state: DeliveryState?) {
        log("delivery: \(state.map { "\($0)" } ?? "done")")
        DispatchQueue.main.async { self.onDelivery?(state) }
    }

    private func consume(_ stream: AsyncStream<CaptureFrame>) async {
        var seg = Segmenter(cfg: config.cfg)
        var silero = SileroStream()
        var dictation: Dictation?
        var shown = Phase.idle
        var clock = 0
        var followupUntil = 0
        var recent: [Float] = []
        var speechFrames = 0
        var gapFrames = 0
        // Which source counted each voiced frame of the open phrase, and why it has not opened a session; "" once logged.
        var phraseEnergy = 0, phraseHAL = 0
        // Loudest frame of the phrase against the energy threshold, so a mic too quiet for the segmenter shows as such.
        var phrasePeak: Float = 0
        var phraseOutcome = ""
        func endPhrase(_ outcome: String) {
            let ms = { (n: Int) in n * frameLen * 1000 / Int(rate) }
            let level = String(format: "peakRms=%.4f threshold=%.4f", locale: Locale(identifier: "en_US_POSIX"), phrasePeak, seg.threshold)
            log("voice: phrase voicedMs=\(ms(speechFrames)) energyMs=\(ms(phraseEnergy)) halMs=\(ms(phraseHAL)) \(level) -> \(outcome)")
            phraseOutcome = ""
        }
        // A phrase that would have opened an automatic session but for voice activity detection, which needs
        // minVoiceSeconds of voice: a first sentence often ends before that, and the next sentence then opens the session.
        var phraseHeldByVoice = false
        var heldPhrase: (samples: [Float], until: Int)?
        // A phrase spoken while another dictation app held the mic was meant for that app: when it lets go mid-phrase
        // (Superwhisper moving to processing, 2026-10-29 12:23 JST), the phrase must not open a session of its own.
        var phraseForOtherApp = false
        // Stop-word judgement runs beside the frame loop; `stopJob` says which dictation and generation it belongs to.
        let stopCheck = LatestRecognition<StopVerdict>()
        let wordCheck = LatestRecognition<(session: UUID, agreed: Bool)>()
        var stopJob: (session: UUID, generation: Int)?
        var stopFrames = 0
        func submitAndArm(_ d: Dictation, cfg: DictationConfig, reason: String) {
            if !d.origin.isAutomatic && d.action == nil, let options = cfg.noWakeWord, options.followup == true, options.valid {
                followupUntil = clock + Int(rate) * (options.followupMs ?? 8000) / 1000
            }
            recent.removeAll(); speechFrames = 0
            submit(d, cfg: cfg, reason: reason)
        }
        var activeGeneration = localHistoryState.withLock { $0.generation }
        for await frame in stream {
            let currentGeneration = localHistoryState.withLock { $0.generation }
            if activeGeneration != currentGeneration {
                seg = Segmenter(cfg: config.cfg); silero = SileroStream(); dictation = nil; followupUntil = 0
                recent = []; speechFrames = 0; Hotkeys.end()
                activeGeneration = currentGeneration
                phaseCont.yield((.idle, nil))
            }
            guard frame.generation == currentGeneration else { continue }
            let f = frame.samples
            let generation = frame.generation
            clock += f.count
            // Set where the user ended it on purpose (stop word or superwhisper's shortcut), so the HUD confirms it.
            var endedByUser = false
            // Derived from `dictation` after every frame (defer covers each `continue`) instead of at each place that sets it.
            defer {
                // An automatic session that has heard speech shows nothing until its words are confirmed, and a waiting phrase shows nothing,
                // so a noise that blips the detector never lights the pill.
                let phase: Phase = endedByUser ? .ended
                    : dictation.map { $0.heardSpeech && !$0.confirmed ? .idle : $0.stopHeard ? .stopping : !$0.heardSpeech ? .waiting : .recording }
                    ?? (clock < followupUntil && config.cfg.dictation?.noWakeWord?.followup == true ? .followup : .idle)
                // .ended hides itself after a moment; the idle that follows it is not a change.
                if phase != shown, !(shown == .ended && phase == .idle) {
                    // The stop sound goes with the stop glyph, which .stopping shows as soon as the word is heard. Playing it on the
                    // later cut lagged the HUD by the 0.5 s wait, and a stop that ended another way after .stopping played none.
                    if phase == .stopping || (phase == .ended && shown != .stopping) { playStopSound() }
                    shown = phase
                    phaseCont.yield((phase, dictation?.action?.icon ?? dictation?.sticky?.appPath))
                }
            }
            if f.isEmpty { seg = Segmenter(cfg: config.cfg); silero = SileroStream(); dictation = nil; followupUntil = 0; recent = []; speechFrames = 0; Hotkeys.end(); continue }
            seg.adaptFloor = dictation == nil
            let hal = voiceBeingHeard()
            let event = seg.push(f, voice: hal)
            // Only action wake words probe on silence gaps, so only they pay for Silero on every frame.
            if dictation == nil, !(config.cfg.macOS?.actions ?? []).isEmpty {
                for p in silero.push(f) { seg.noteVoice(p) }
            }
            let energy = seg.lastWasEnergy
            let speaking = seg.lastWasSpeech
            // The automatic session opens on voice activity before any transcript, so a spoken wake word replaces it with the
            // dictation the user asked for. The idle path below transcribes the same utterance again and opens that dictation.
            var heardDuringAutomatic: Transcript?
            if dictation?.origin.isAutomatic == true, case let .utterance(u)? = event,
               let heard = try? await transcribe(u, locale: Locale(identifier: config.cfg.locale ?? "ja_JP")) {
                guard localHistoryState.withLock({ $0.generation == generation }) else { continue }
                if speaksWake(heard) {
                    dictation = nil; speechFrames = 0; Hotkeys.end()
                    log("dictation session: automatic replaced by wake word")
                } else {
                    heardDuringAutomatic = heard
                }
            }
            if var d = dictation {
                if speechFrames > 0, !phraseOutcome.isEmpty { endPhrase("dictation opened \(d.origin.fields) \(sidField(d.sessionID))") }
                let dc = config.cfg.dictation ?? DictationConfig()
                let key = Hotkeys.take()
                if key == .cancel {
                    dictation = nil; recent.removeAll(); speechFrames = 0; Hotkeys.end()
                    followupUntil = 0
                    log("dictation cancelled \(sidField(d.sessionID))"); continue
                }
                d.samples += f
                d.silentFrames = speaking ? 0 : d.silentFrames + 1
                if !d.confirmed, let check = wordCheck.take(), check.session == d.sessionID, check.agreed {
                    d.confirmed = true
                    log("dictation session: automatic confirmed by agreed words \(sidField(d.sessionID))")
                }
                // Checked beside the frame loop on the audio so far, after every further second of it, until one agrees. Waiting
                // for an utterance to close instead left long continuous speech unconfirmed, so its HUD never appeared.
                if !d.confirmed, !wordCheck.pending, d.samples.count - d.checkedSamples >= Int(rate) {
                    d.checkedSamples = d.samples.count
                    let samples = d.samples, session = d.sessionID, locale = Locale(identifier: config.cfg.locale ?? "ja_JP")
                    // A recognizer that fails confirms, as the handoff hands such a recording off.
                    wordCheck.submit { (session, (try? await agreedWordCount(samples, locale: locale)).map { $0 >= NoWakePolicy.minAgreedCharacters } ?? true) }
                }
                stopFrames += f.count
                // The stop word is judged beside the frame loop, on the newest utterance only. Frames keep flowing meanwhile,
                // so the result is taken on a later frame, and the cut also drops the frames that arrived since.
                if let verdict = stopCheck.take(), let job = stopJob {
                    stopJob = nil
                    let current = job.session == d.sessionID && job.generation == generation
                    if current { d.stopHeard = verdict == .heard }
                    if case let .cut(cut) = verdict, cut > 0, current {
                        d.samples.removeLast(min(cut + stopFrames, d.samples.count))
                        dictation = nil; recent.removeAll(); speechFrames = 0; Hotkeys.end(); endedByUser = true
                        log("dictation finished by stop word \(sidField(d.sessionID))")
                        if d.heardSpeech, !d.samples.isEmpty { submitAndArm(d, cfg: dc, reason: finishReasonToken("stop word")) }
                        continue
                    }
                    if case .cut = verdict, !current { log("stop check: result discarded job=\(sidField(job.session)) generation=\(job.generation) current=\(sidField(d.sessionID)) generation=\(generation)") }
                }
                let stopAudio: (u: [Float], heard: Transcript?, kind: String)? = switch event {
                case let .utterance(u)?: (u, heardDuringAutomatic, "utterance")
                case let .tail(u)?: (u, nil, "tail")
                // Steady sound above the threshold (room noise on a raw mic) keeps the segmenter in one open utterance, so no
                // utterance or tail comes until it stops; and even in a quiet room the utterance closes only after hangoverMs.
                // So the newest seconds are also checked during a long utterance and from the first 60 ms gap, so a stop word
                // reaches the HUD (.stopping) about as soon as the recognizer (~100 ms) has it.
                // A check that ran mid-sentence no longer holds back the one after the stop word by up to 250 ms; `pending` already
                // keeps one recognition at a time.
                // ponytail: at most every 100 ms while a dictation is open; widen it if the recognizer load shows up.
                default: (seg.isSkipping || seg.tailSilentFrames >= 1) && !stopCheck.pending
                    && d.samples.count - d.stopCheckedSamples >= Int(rate) / 10
                    ? (Array(d.samples.suffix(Int(rate) * 4)), nil, "window") : nil
                }
                if let stopAudio, let words = config.cfg.stopWords, !words.isEmpty {
                    let locale = Locale(identifier: config.cfg.locale ?? "ja_JP")
                    let quiet = config.cfg.dictation?.noWakeWord?.enabled == true
                    let session = d.sessionID
                    stopCheck.submit { await stopWordCut(stopAudio.u, heard: stopAudio.heard, stopWords: words, locale: locale, quiet: quiet, kind: stopAudio.kind, sessionID: session) }
                    d.stopCheckedSamples = d.samples.count
                    stopJob = (d.sessionID, generation)
                    stopFrames = 0
                }
                guard localHistoryState.withLock({ $0.generation == generation }) else { continue }
                if d.deafFrames > 0 { d.deafFrames -= 1 } else if !d.heardSpeech && speaking {
                    // Drop the wait before the text, keeping a preroll so the first syllable is whole.
                    d.samples = Array(d.samples.suffix(frames(ms: config.cfg.prerollMs ?? 300) * frameLen + frameLen))
                    d.heardSpeech = true
                }
                dictation = d
                if !d.heardSpeech {
                    if d.silentFrames * frameLen * 1000 / Int(rate) >= dc.startTimeoutMs ?? 3000 {
                        dictation = nil; recent.removeAll(); speechFrames = 0; Hotkeys.end()
                        log("dictation cancelled: nothing said after the wake word \(sidField(d.sessionID))")
                    }
                    continue
                }
                // A pending stop check holds the end, so a stop word is not submitted as text.
                guard key == .finish || (!stopCheck.pending && (d.silentFrames * frameLen * 1000 / Int(rate) >= (d.origin.isAutomatic ? dc.noWakeWord?.endSilenceMs : nil) ?? dc.endSilenceMs ?? 1200
                    || d.samples.count > Int((dc.maxSeconds ?? 60) * rate))) else { continue }
                let reason = key == .finish ? "hotkey" : d.samples.count > Int((dc.maxSeconds ?? 60) * rate) ? "maxSeconds"
                    : "\(d.silentFrames * frameLen * 1000 / Int(rate)) ms silence"
                d.samples.removeLast(d.silentFrames * frameLen)
                // A whole maxSeconds with no word Apple Speech and Whisper agreed on was the room, not a voice. Lift the floor
                // as an idle over-cap head with no wake does; otherwise the next frame reads the same noise as speech and
                // reopens a session at once, and the mic HUD never leaves (model: .claude/skills/verify-hud-noise).
                if reason == "maxSeconds", d.origin.isAutomatic, !d.confirmed { rebaseFloorAfterNoWake(&seg) }
                dictation = nil; recent.removeAll(); speechFrames = 0; Hotkeys.end(); endedByUser = key == .finish
                log("dictation ended by \(reason) after \(d.samples.count * 1000 / Int(rate)) ms of audio \(sidField(d.sessionID))")
                submitAndArm(d, cfg: dc, reason: finishReasonToken(reason))
                continue
            }
            // speechFrames counts the voiced frames of the phrase being spoken, joined across gaps up to the hangover,
            // which the VAD alone splits into short runs.
            if speaking {
                if speechFrames == 0 { phraseEnergy = 0; phraseHAL = 0; phrasePeak = 0; phraseForOtherApp = false }
                speechFrames += 1; gapFrames = 0
                if energy { phraseEnergy += 1 }
                if hal { phraseHAL += 1 }
                phrasePeak = max(phrasePeak, seg.lastRms)
            }
            else if speechFrames > 0 {
                gapFrames += 1
                phrasePeak = max(phrasePeak, seg.lastRms)
                if gapFrames > frames(ms: config.cfg.hangoverMs ?? 300) {
                    if !phraseOutcome.isEmpty { endPhrase(phraseOutcome) }
                    // ponytail: one held phrase for 5 s; a third sentence before the gate opens drops the first.
                    if phraseHeldByVoice { heldPhrase = (recent, clock + Int(rate) * 5) }
                    speechFrames = 0; phraseHeldByVoice = false
                }
            }
            recent += f
            // A live phrase is kept so an automatic session that starts late still records it from the beginning.
            // ponytail: capped at 10 s of audio; a longer phrase keeps only its tail.
            let keep = speechFrames > 0 ? Int(rate) * 10 : frames(ms: config.cfg.prerollMs ?? 300) * frameLen + frameLen
            if recent.count > keep { recent.removeFirst(recent.count - keep) }
            if Hotkeys.take() == .start {
                if let name = Hotkeys.takeAction() {
                    if let action = config.cfg.action(named: name) {
                        // Same gates as a spoken action: a handoff still in flight would drop this recording in submit, and a busy mic is speech the wake word would skip.
                        if handoffBusy.withLock({ $0 }) {
                            log("action skipped: previous dictation still in flight")
                        } else if let busy = micInUse(by: config.cfg.skipWhileMicInUseBy ?? []) {
                            log("skipped: \(busy) is using the microphone")
                        } else {
                            dictation = Dictation(samples: [], generation: generation, transcriptPolicy: AutomaticTranscriptPolicy(config.cfg.dictation?.noWakeWord), silentFrames: 0, heardSpeech: false, action: action)
                            // Same chime as a spoken action waiting for text, so the press is heard the same way.
                            if UserDefaults.standard.bool(forKey: soundKey) {
                                actionWakeChime?.play()
                                dictation?.deafFrames = frames(ms: 600)
                            }
                            Hotkeys.begin()
                            DictationJournal.note(dictation!.sessionID, manual: true)
                            log("dictation session: action-hotkey \(action.name) \(sidField(dictation?.sessionID))")
                        }
                    } else {
                        log("action hotkey ignored: \(name) is not in the config")
                    }
                } else if config.cfg.dictation != nil, automaticWakeAllowed() {
                    dictation = Dictation(samples: [], generation: generation, transcriptPolicy: AutomaticTranscriptPolicy(config.cfg.dictation?.noWakeWord), silentFrames: 0, heardSpeech: false)
                    Hotkeys.begin()
                    DictationJournal.note(dictation!.sessionID, manual: true)
                    log("dictation session: manual-hotkey \(dictation!.origin.fields) \(sidField(dictation!.sessionID))")
                }
                continue
            }
            let options = config.cfg.dictation?.noWakeWord
            // Starts even while the user types or moves the mouse; input idle no longer gates it.
            // Same short-circuit order as the single condition it replaces, so each clause can name itself in the phrase line.
            let longEnough = speechFrames >= frames(ms: config.cfg.minSpeechMs ?? 300)
            let modeSet = !(config.cfg.dictation?.superwhisperMode ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            let idle = speaking && longEnough && modeSet ? MacInputActivity.idleSeconds() : nil
            let origin = idle.flatMap { NoWakePolicy.onSpeech(options, idleSeconds: $0, inFollowupWindow: clock < followupUntil) }
            if speaking {
                phraseOutcome = !longEnough ? "shorter than minSpeechMs" : !modeSet ? "superwhisperMode unset"
                    : idle == nil ? "input idle unavailable" : origin == nil ? "NoWakePolicy.onSpeech nil" : "allowed but no audio"
            }
            if let origin {
                if phraseForOtherApp {
                    phraseOutcome = "automatic wake blocked: phrase began while another app held the microphone"
                } else if !automaticWakeAllowed() {
                    if lastAutomaticBlock == Listener.noVoiceActivity { phraseHeldByVoice = true }
                    if let block = lastAutomaticBlock, (config.cfg.skipWhileMicInUseBy ?? []).contains(where: { block == Listener.micHeldBy($0) }) { phraseForOtherApp = true }
                    phraseOutcome = "automatic wake blocked: \(lastAutomaticBlock ?? "-")"
                } else {
                    // The whole phrase with its preroll, even when it overlaps typing, after a phrase the gate held back.
                    let held = heldPhrase.flatMap { clock < $0.until ? $0.samples : nil } ?? []
                    heldPhrase = nil; phraseHeldByVoice = false
                    if !held.isEmpty { log("dictation session: automatic keeps \(held.count * 1000 / Int(rate)) ms held for voice activity") }
                    let samples = held + recent
                    if !samples.isEmpty {
                        // Finish only: the user is mid-task elsewhere and their Escape belongs to that app. The finish key is the manual
                        // stop for when the stop word is misheard.
                        dictation = Dictation(samples: samples, generation: generation, transcriptPolicy: AutomaticTranscriptPolicy(config.cfg.dictation?.noWakeWord), silentFrames: 0, heardSpeech: true, origin: origin)
                        Hotkeys.begin(finishOnly: true)
                        log("dictation session: automatic \(origin.fields) \(sidField(dictation?.sessionID))")
                        endPhrase("session opened \(sidField(dictation?.sessionID))")
                        continue
                    }
                }
            }
            // Experimental early wake (cfg.earlyWakeMs): STT on the open utterance before the hangover. A transcript that
            // starts with a wake word and continues opens the dictation now; a bare wake word at a short gap fires now.
            if event == nil, let buf = seg.probe() {
                let began = Date()
                let energySilentMs = seg.energySilent * frameLen * 1000 / Int(rate)
                let voiceSilentMs = seg.voiceSilentMs
                guard let tr = try? await transcribe(buf, locale: Locale(identifier: config.cfg.locale ?? "ja_JP")) else { continue }
                guard localHistoryState.withLock({ $0.generation == generation }) else { continue }
                let earlyOrigin = WakeOrigin(rule: .strict, inputIdleSeconds: MacInputActivity.idleSeconds())
                log("dictation recognition: complete id=\(takeRecognitionId()) \(earlyOrigin.fields) \(sidField(nil))")
                let stt = Int(Date().timeIntervalSince(began) * 1000)
                let early = wakeVerdict(tr, sets: wakeSets(config.cfg), tailSilentFrames: seg.probeSilentFrames, closed: false)
                // No transcript text: this line is logged with noWakeWord on too, where speech text stays out of the log.
                log("wake probe: sttMs=\(stt) energySilentMs=\(energySilentMs) silentMs=\(seg.tailSilentFrames * frameLen * 1000 / Int(rate)) sileroSilentMs=\(voiceSilentMs.map(String.init) ?? "-") bufMs=\(buf.count * 1000 / Int(rate)) hit=\(early != nil)")
                guard let early else { continue }
                let hit = early.start
                let earlyAction = early.action
                let start = config.cfg.dictation == nil && earlyAction == nil ? nil : hit
                guard start != nil || hit == nil else { continue } // a continuing transcript without dictation is not a wake
                if earlyAction != nil, handoffBusy.withLock({ $0 }) { log("action skipped: previous dictation still in flight"); continue }
                if let busy = micInUse(by: config.cfg.skipWhileMicInUseBy ?? []) { log("skipped: \(busy) is using the microphone"); continue }
                if config.cfg.dictation?.noWakeWord?.enabled != true { log("early: \(tr.text)\(earlyAction.map { " action=\($0.name)" } ?? "")  [buf \(buf.count * 1000 / Int(rate)) ms, stt \(stt) ms]") }
                seg.reset()
                if let start {
                    let stickyCapture: StickySession? = early.sticky ? await MainActor.run { captureStickyTarget(config.cfg.macOS) } : nil
                    dictation = Dictation(samples: trimmed(buf, cutAt: start.cutAt), generation: generation, transcriptPolicy: AutomaticTranscriptPolicy(config.cfg.dictation?.noWakeWord), silentFrames: 0, heardSpeech: true, action: earlyAction, sticky: stickyCapture)
                    log("dictation started early (cut at \(Int(start.cutAt * 1000)) ms)\(earlyAction.map { " action=\($0.name)" } ?? "")")
                    Hotkeys.begin()
                } else if config.cfg.dictation != nil || earlyAction != nil {
                    let stickyCapture: StickySession? = early.sticky ? await MainActor.run { captureStickyTarget(config.cfg.macOS) } : nil
                    dictation = Dictation(samples: [], generation: generation, transcriptPolicy: AutomaticTranscriptPolicy(config.cfg.dictation?.noWakeWord), silentFrames: 0, heardSpeech: false, action: earlyAction, sticky: stickyCapture)
                    if earlyAction != nil, UserDefaults.standard.bool(forKey: soundKey) {
                        actionWakeChime?.play()
                        dictation?.deafFrames = frames(ms: 600)
                    }
                    log("dictation started early (waiting for text)\(earlyAction.map { " action=\($0.name)" } ?? "")")
                    Hotkeys.begin()
                } else {
                    Macrowhisper.prepare(config.cfg.macrowhisper)
                    Platform.runMacAction(config.cfg)
                }
                continue
            }
            guard let event else { continue }
            // Only a dictation listens for a stop word at the end of a long utterance.
            if case .tail = event { continue }
            let (u, isHead) = switch event { case let .utterance(u): (u, false); case let .head(u): (u, true); case .tail: ([], false) }
            config.reloadIfChanged()
            DictationJournal.bind(config.cfg.dictation?.journalDir, journalTaskFile: config.cfg.dictation?.journalTaskFile,
                                  fallback: journalFallback)
            seg.cfg = config.cfg
            let transcript: Transcript
            let began = Date()
            do { transcript = try await transcribe(u, locale: Locale(identifier: config.cfg.locale ?? "ja_JP")) } catch {
                log(config.cfg.dictation?.noWakeWord?.enabled == true ? "transcribe failed" : "transcribe failed: \(error)")
                // Over-cap head that timed out / failed STT: same trap as empty/no-wake; lift the floor (Windows RebaseFloor).
                if isHead { rebaseFloorAfterNoWake(&seg) }
                continue
            }
            guard localHistoryState.withLock({ $0.generation == generation }) else { continue }
            let t = transcript.text
            let strictMatch = speaksWake(transcript)
            let relaxed = !strictMatch && !isHead && config.cfg.dictation?.noWakeWord?.relaxedReadingDistance2 == true
                && !(config.cfg.dictation?.superwhisperMode?.isEmpty ?? true)
                && macRelaxedWake(t, wakeWords: config.cfg.wakeWords)
            let recognitionOrigin = WakeOrigin(rule: relaxed ? .relaxedD2 : .strict, inputIdleSeconds: MacInputActivity.idleSeconds())
            let recognitionId = takeRecognitionId()
            if !isHead { WakeMissLabel.shared.observe(id: recognitionId, origin: recognitionOrigin, at: Date()) }
            log("dictation recognition: complete id=\(recognitionId) \(recognitionOrigin.fields) \(sidField(nil))")
            // Logged too, so misses that transcribe to nothing are visible when tuning.
            guard !t.isEmpty else {
                if config.cfg.dictation?.noWakeWord?.enabled != true { log("heard: (empty, \(u.count * 1000 / Int(rate)) ms)") }
                // Over-cap head with no text: same trap as steady noise; lift the floor (Windows RebaseFloor).
                if isHead { rebaseFloorAfterNoWake(&seg) }
                continue
            }
            if !isHead, (config.cfg.stopWords ?? []).map(normalize).contains(t) {
                if let busy = micInUse(by: config.cfg.skipWhileMicInUseBy ?? []) {
                    // Only when observed busy. Prefer a stop-only URL: mic state can change before delivery.
                    if config.cfg.dictation?.noWakeWord?.enabled != true { log("heard: \(t)  -> stop \(busy)") }
                    Platform.runMacAction(config.cfg, stop: true)
                } else {
                    if config.cfg.dictation?.noWakeWord?.enabled != true { log("heard: \(t)  (stop word, nothing is recording)") }
                }
                continue
            }
            let verdict = wakeVerdict(transcript, sets: wakeSets(config.cfg), tailSilentFrames: 0, closed: !isHead)
            let hit = verdict != nil && verdict?.action == nil && verdict?.start == nil
            // Utterance length shows whether the VAD holds on past the word; stt is recognizer time.
            if config.cfg.dictation?.noWakeWord?.enabled != true { log("heard: \(t)\(hit ? "  -> wake" : "")  [utt \(u.count * 1000 / Int(rate)) ms, stt \(Int(Date().timeIntervalSince(began) * 1000)) ms]") }
            let start = verdict?.action != nil || config.cfg.dictation == nil ? nil : verdict?.start
            if let action = verdict?.action {
                guard !handoffBusy.withLock({ $0 }) else {
                    log("action skipped: previous dictation still in flight"); continue
                }
                if let busy = micInUse(by: config.cfg.skipWhileMicInUseBy ?? []) {
                    log("skipped: \(busy) is using the microphone"); continue
                }
                if let start = verdict?.start {
                    dictation = Dictation(samples: trimmed(u, cutAt: start.cutAt), generation: generation, transcriptPolicy: AutomaticTranscriptPolicy(config.cfg.dictation?.noWakeWord), silentFrames: 0, heardSpeech: true,
                                          action: action)
                    log("dictation started (cut at \(Int(start.cutAt * 1000)) ms) -> action \(action.name)")
                } else {
                    dictation = Dictation(samples: [], generation: generation, transcriptPolicy: AutomaticTranscriptPolicy(config.cfg.dictation?.noWakeWord), silentFrames: 0, heardSpeech: false, action: action)
                    if UserDefaults.standard.bool(forKey: soundKey) {
                        actionWakeChime?.play()
                        dictation?.deafFrames = frames(ms: 600)
                    }
                    log("dictation started (waiting for text) -> action \(action.name)")
                }
                Hotkeys.begin()
                continue
            }
            if !hit && start == nil && relaxed, recognitionOrigin.inputIdleSeconds != nil, automaticWakeAllowed() {
                dictation = Dictation(samples: [], generation: generation, transcriptPolicy: AutomaticTranscriptPolicy(config.cfg.dictation?.noWakeWord), silentFrames: 0, heardSpeech: false, origin: recognitionOrigin)
                Hotkeys.begin()
                log("dictation session: wake-only \(recognitionOrigin.fields) \(sidField(dictation?.sessionID))")
                continue
            }
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
            guard start != nil || config.cfg.dictation != nil else {
                Macrowhisper.prepare(config.cfg.macrowhisper)
                Platform.runMacAction(config.cfg)
                continue
            }
            if let id = NSWorkspace.shared.frontmostApplication?.bundleIdentifier,
               config.cfg.dictation?.excludeBundleIDs?.contains(id) == true {
                log("dictation skipped: \(id) is excluded"); continue
            }
            if let start {
                let stickyCapture: StickySession? = verdict?.sticky == true ? await MainActor.run { captureStickyTarget(config.cfg.macOS) } : nil
                dictation = Dictation(samples: trimmed(u, cutAt: start.cutAt), generation: generation, transcriptPolicy: AutomaticTranscriptPolicy(config.cfg.dictation?.noWakeWord), silentFrames: 0, heardSpeech: true, sticky: stickyCapture)
                log("dictation started (cut at \(Int(start.cutAt * 1000)) ms)")
            } else {
                let stickyCapture: StickySession? = verdict?.sticky == true ? await MainActor.run { captureStickyTarget(config.cfg.macOS) } : nil
                dictation = Dictation(samples: [], generation: generation, transcriptPolicy: AutomaticTranscriptPolicy(config.cfg.dictation?.noWakeWord), silentFrames: 0, heardSpeech: false, sticky: stickyCapture)
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

    private func speaksWake(_ transcript: Transcript) -> Bool {
        wakeVerdict(transcript, sets: wakeSets(config.cfg), tailSilentFrames: 0, closed: true) != nil
    }

    /// Checked on every voiced frame, so only a change of reason is logged.
    private var lastAutomaticBlock: String?

    private func automaticWakeAllowed() -> Bool {
        let block = automaticWakeBlock()
        if let block, block != lastAutomaticBlock { log("automatic wake blocked: \(block)") }
        lastAutomaticBlock = block
        return block == nil
    }

    private func automaticWakeBlock() -> String? {
        if localHistoryState.withLock({ $0.busy }) { return "previous recording still transcribing" }
        if config.cfg.dictation?.echoCancellation != false && !voiceRecentlyActive() { return Listener.noVoiceActivity }
        if let busy = micInUse(by: config.cfg.skipWhileMicInUseBy ?? [], failClosed: true) { return Listener.micHeldBy(busy) }
        if let excluded = config.cfg.dictation?.excludeBundleIDs, !excluded.isEmpty {
            guard let id = NSWorkspace.shared.frontmostApplication?.bundleIdentifier else { return "frontmost app unknown" }
            if excluded.contains(id) { return "\(id) is excluded" }
        }
        return nil
    }

    private func restart() { halt(); attemptStart() }

    private func attemptStart() {
        do { try start(); restartError = nil } catch {
            // One log line per distinct error, the retry repeats every 10 s while the device is away.
            if restartError != "\(error)" { restartError = "\(error)"; log("restart failed, retrying every 10 s: \(error)") }
            // The one recovery seen in the field (2026-10-03 log) went through a fresh engine; start() builds one when the route is unset.
            activeRoute = nil
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

    private func claimLocalHistory(_ d: Dictation) -> Int? {
        localHistoryState.withLock { state -> Int? in
            guard !state.busy && state.generation == d.captureGeneration else { return nil }
            state.busy = true
            return state.generation
        }
    }

    private func storeLocal(_ d: Dictation) async {
        guard let generation = claimLocalHistory(d) else { log("local history transcription busy; recording discarded \(sidField(d.sessionID))"); return }
        defer { localHistoryState.withLock { $0.busy = false } }
        await transcribeToHistory(d, generation: generation)
    }

    private func transcribeToHistory(_ d: Dictation, generation: Int) async {
        let historyGeneration = d.historyGeneration
        do {
            let transcript = try await transcribe(d.samples, locale: Locale(identifier: config.cfg.locale ?? "ja_JP"))
            guard localHistoryState.withLock({ $0.generation == generation }) else { return }
            log("dictation recognition: complete id=\(takeRecognitionId()) \(d.origin.fields) \(sidField(d.sessionID))")
            let text = transcript.runs.map(\.text).joined()
            let delivery: JournalDelivery = d.origin.isAutomatic || d.action != nil ? .history : d.sticky?.journalDelivery ?? .paste
            DictationJournal.note(d.sessionID, delivery: delivery)
            let retained = DictationHistory.shared.add(id: d.sessionID, text: text, origin: d.origin, expectedGeneration: historyGeneration, policy: d.transcriptPolicy)
            if retained && delivery != .history {
                if let sticky = d.sticky {
                    await deliverSticky(sticky, text: text)
                } else {
                    _ = await MainActor.run { pasteDictation(text, target: d.target) }
                }
            }
        } catch { log("local history transcription failed") }
    }

    private func submit(_ d: Dictation, cfg: DictationConfig, reason: String) {
        guard localHistoryState.withLock({ $0.generation == d.captureGeneration }) else { return }
        let token = finishReasonToken(reason)
        DictationJournal.note(d.sessionID, endReason: token, recordingSeconds: Double(d.samples.count) / rate, focus: d.target.map(DictationJournal.focus(of:)))
        log("dictation session: end reason=\(token) \(d.origin.fields) seconds=\(String(format: "%.2f", locale: Locale(identifier: "en_US_POSIX"), Double(d.samples.count) / rate)) \(sidField(d.sessionID))")
        // A wake word followed straight by the stop word leaves a few frames. Superwhisper returns nothing for them and
        // the 30 s wait holds the handoff lock, so the next wake dictation and every automatic session behind it stall.
        if d.samples.count < frames(ms: config.cfg.minSpeechMs ?? 300) * frameLen {
            log("dictation dropped: audio shorter than minSpeechMs (\(d.samples.count * 1000 / Int(rate)) ms) reason=\(token) \(sidField(d.sessionID))")
            return
        }
        if d.origin.isAutomatic {
            if NoWakePolicy.belowMinHandoff(sampleCount: d.samples.count, rate: rate, options: cfg.noWakeWord) {
                log("dictation dropped: automatic audio shorter than minHandoffMs (\(d.samples.count * 1000 / Int(rate)) ms) reason=\(token) \(sidField(d.sessionID))")
                return
            }
            Task {
                // A cough or a keystroke still opens superwhisper and leaves its own empty or "よっ!" entry; Silero finds no voice in them.
                do {
                    if try SpeechGate.voicedWindows(d.samples) == 0 {
                        log("dictation dropped: automatic audio had no speech reason=\(token) \(sidField(d.sessionID))")
                        return
                    }
                } catch { log("speech gate failed; handing off anyway: \(error)") }
                guard SpeakerGate.accepts(d.samples, cfg: config.cfg) else {
                    log("dictation dropped: automatic audio was another speaker reason=\(token) \(sidField(d.sessionID))")
                    return
                }
                // Silero still hears a voiced cough as speech. Whisper and Apple Speech each invent their own words for it,
                // while spoken words come out the same from both, so only a recording they agree on reaches superwhisper.
                // A recording confirmed while it was recorded already agreed; checking it again would only delay superwhisper.
                if !d.confirmed {
                    let began = Date()
                    do {
                        let shared = try await agreedWordCount(d.samples, locale: Locale(identifier: config.cfg.locale ?? "ja_JP"))
                        log("dictation agreement: shared=\(shared) ms=\(Int(Date().timeIntervalSince(began) * 1000)) \(sidField(d.sessionID))")
                        if shared < NoWakePolicy.minAgreedCharacters {
                            log("dictation dropped: automatic audio had no agreed words reason=\(token) \(sidField(d.sessionID))")
                            return
                        }
                    } catch { log("word agreement failed; handing off anyway: \(error)") }
                }
                guard let generation = claimLocalHistory(d) else { log("local history transcription busy; recording discarded \(sidField(d.sessionID))"); return }
                defer { localHistoryState.withLock { $0.busy = false } }
                var d = d
                d.samples = SpeakerGate.withoutOtherSpeakers(d.samples, cfg: config.cfg)
                // superwhisper takes focus when it opens the file; hand it back to wherever the user is now, not where the session began.
                // Ending it with the stop word or the finish key shows the user meant to dictate into the app in front, so it is handed
                // off like a wake-word dictation and superwhisper pastes it. One that ended on silence or maxSeconds stays history-only.
                let origin = token == "StandaloneStop" || token == "FinishCommand" ? WakeOrigin(rule: .strict, inputIdleSeconds: d.origin.inputIdleSeconds) : d.origin
                if origin != d.origin { log("dictation session: automatic ended by \(token), pasting like a wake-word dictation \(sidField(d.sessionID))") }
                reportDelivery(.processing)
                let outcome = await handoff(d.samples, cfg: cfg, target: NSWorkspace.shared.frontmostApplication,
                                            action: nil, origin: origin, sessionID: d.sessionID, transcriptPolicy: d.transcriptPolicy,
                                            stillCurrent: { self.localHistoryState.withLock { $0.generation == generation } })
                if outcome == .notDispatched { await transcribeToHistory(d, generation: generation) }
                reportDelivery(outcome == .noResult ? .failed : .done)
            }
            return
        }
        let claimed = handoffBusy.withLock { busy in
            defer { busy = true }
            return !busy
        }
        guard claimed else { log("dictation dropped: previous one still in flight \(sidField(d.sessionID))"); return }
        Task {
            defer { handoffBusy.withLock { $0 = false } }
            var d = d
            d.samples = SpeakerGate.withoutOtherSpeakers(d.samples, cfg: config.cfg)
            reportDelivery(.processing)
            let outcome = await handoff(d.samples, cfg: cfg, target: d.target,
                                        action: d.action, origin: d.origin, sessionID: d.sessionID, transcriptPolicy: d.transcriptPolicy, sticky: d.sticky)
            if outcome == .notDispatched { await storeLocal(d) }
            let fellBack = d.sticky?.state.phase == .fellBack
            reportDelivery(outcome == .noResult || fellBack ? .failed : .done)
        }
    }

}

/// First bundle ID in the list whose process is reading an input device, via CoreAudio's per-process objects.
/// superwhisper keeps input closed while idle and opens it only while recording.
func micInUse(by bundleIDs: [String], failClosed: Bool = false) -> String? {
    guard !bundleIDs.isEmpty else { return nil }
    func address(_ sel: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: sel, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    }
    let system = AudioObjectID(kAudioObjectSystemObject)
    var addr = address(kAudioHardwarePropertyProcessObjectList)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(system, &addr, 0, nil, &size) == noErr else { return failClosed ? "microphone status unavailable" : nil }
    var procs = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
    guard AudioObjectGetPropertyData(system, &addr, 0, nil, &size, &procs) == noErr else { return failClosed ? "microphone status unavailable" : nil }
    for proc in procs {
        var running: UInt32 = 0
        var runningSize = UInt32(MemoryLayout<UInt32>.size)
        var runAddr = address(kAudioProcessPropertyIsRunningInput)
        guard AudioObjectGetPropertyData(proc, &runAddr, 0, nil, &runningSize, &running) == noErr else {
            if failClosed { return "microphone status unavailable" }
            continue
        }
        guard running != 0 else { continue }
        var bundle: Unmanaged<CFString>?
        var bundleSize = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        var bundleAddr = address(kAudioProcessPropertyBundleID)
        guard AudioObjectGetPropertyData(proc, &bundleAddr, 0, nil, &bundleSize, &bundle) == noErr,
              let id = bundle?.takeRetainedValue() as String? else {
            if failClosed { return "microphone process unavailable" }
            continue
        }
        guard bundleIDs.contains(id) else { continue }
        // superwhisper 3.4's Muffle keeps its own audio tap running after a recording ends, so IsRunningInput stays 1
        // with no input device until the app quits (2026-11-21). Only a process with an input device holds the mic.
        var devicesAddr = AudioObjectPropertyAddress(
            mSelector: kAudioProcessPropertyDevices, mScope: kAudioObjectPropertyScopeInput, mElement: kAudioObjectPropertyElementMain)
        var devicesSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(proc, &devicesAddr, 0, nil, &devicesSize) == noErr else {
            if failClosed { return "microphone status unavailable" }
            continue
        }
        if devicesSize > 0 { return id }
    }
    return nil
}


// MARK: menu bar app

let deviceKey = "inputDeviceUID"
let fallbackKey = "inputFallbackUIDs"
let soundKey = "confirmationSound"

/// Soft pops on the notes Windows plays (TraySettings.Synthesize), so each chime reads the same on both platforms.
/// One envelope spans every note and the phase runs on across the pitch step: a decay per note fell to silence
/// between the notes, and a two-note chime was heard as the chime playing twice.
private func chime(_ name: String, _ notes: [Double]) -> NSSound? {
    let noteSamples = Int(rate) * 120 / 1000
    let total = Double(noteSamples * notes.count)
    var phase = 0.0
    let samples = (0 ..< noteSamples * notes.count).map { i -> Float in
        phase += 2 * .pi * notes[i / noteSamples] / rate
        let env = min(1, Double(i) / 80) * pow(1 - Double(i) / total, 3 / Double(notes.count))
        return Float(sin(phase) * env * 9000 / 32768)
    }
    let url = FileManager.default.temporaryDirectory.appendingPathComponent("voice-switch-\(name)-chime.wav")
    guard (try? writeWAV(samples, to: url)) != nil else { return nil }
    defer { try? FileManager.default.removeItem(at: url) }
    return NSSound(contentsOf: url, byReference: false)
}

/// Stop falls A5 -> E5, so it reads differently from the wake Tink.
private let stopChime = chime("stop", [880.0, 659.25])
/// An action's wake rises A5 -> E6, so a wireless mic away from the screen still tells it from dictation by ear.
private let actionWakeChime = chime("action-wake", [880.0, 1318.51])

func playStopSound() {
    if UserDefaults.standard.bool(forKey: soundKey) { stopChime?.play() }
}

/// Every HUD glyph, drawn as a vector path so the HUD no longer depends on emoji artwork. Windows DictationHud.cs
/// mirrors these names and shapes; only the drawing code differs per platform.
enum HUDLabel: String, CaseIterable {
    case followup, waiting, recording, processing, done, failed, saved, paused, resumed
}

/// Draws one `HUDLabel` in a 24x24 design space. Recording and processing animate at 30 fps while visible;
/// every other glyph is static, so a hidden HUD costs nothing.
final class HUDGlyphView: NSView {
    private(set) var glyph: HUDLabel = .waiting
    /// The waking action's icon: drawn instead of the mic while .waiting, and under a red dot while .recording, so an
    /// action dictation stays distinct from a plain one; nil draws the usual glyph.
    var actionImage: NSImage? { didSet { needsDisplay = true } }
    private var ticker: Timer?
    private var began = Date()

    override var isFlipped: Bool { false }

    func show(_ glyph: HUDLabel) {
        // A glyph that reappears after the HUD hid starts its animation from zero, so a second dictation pulses again.
        if self.glyph != glyph || ticker == nil { began = Date() }
        self.glyph = glyph
        let animated = glyph == .recording || glyph == .processing
        if animated, ticker == nil {
            ticker = Timer.scheduledTimer(withTimeInterval: 1 / 30, repeats: true) { [weak self] _ in self?.needsDisplay = true }
        } else if !animated {
            stop()
        }
        needsDisplay = true
    }

    /// Call when the HUD hides, so the animation timer does not outlive the visible pill.
    func stop() {
        ticker?.invalidate()
        ticker = nil
    }

    deinit { ticker?.invalidate() }

    override func draw(_: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        let side = min(bounds.width, bounds.height)
        guard side > 0 else { return }
        if glyph == .waiting || glyph == .recording, let actionImage {
            actionImage.draw(in: bounds)
            if glyph == .recording {
                let dot = side * 0.32
                ctx.setFillColor(Ink.red.cgColor)
                ctx.fillEllipse(in: CGRect(x: bounds.maxX - dot, y: bounds.maxY - dot, width: dot, height: dot))
            }
            return
        }
        ctx.saveGState()
        ctx.translateBy(x: (bounds.width - side) / 2, y: (bounds.height - side) / 2)
        ctx.scaleBy(x: side / 24, y: side / 24)
        drawGlyph()
        ctx.restoreGState()
    }

    /// Fixed colors, matching `ColorFor` in DictationHud.cs. Dynamic colors would resolve to black over the dark
    /// HUD material in light mode, because the panel does not inherit the HUD appearance from the window.
    private enum Ink {
        static let white = NSColor(srgbRed: 0.949, green: 0.949, blue: 0.957, alpha: 1)
        static let red = NSColor(srgbRed: 1, green: 0.271, blue: 0.227, alpha: 1)
        static let green = NSColor(srgbRed: 0.188, green: 0.820, blue: 0.345, alpha: 1)
        static let gray = NSColor(srgbRed: 0.557, green: 0.557, blue: 0.576, alpha: 1)
    }

    private func color(for glyph: HUDLabel) -> NSColor {
        switch glyph {
        case .waiting: return Ink.white.withAlphaComponent(0.65)
        case .recording, .failed: return Ink.red
        case .done, .saved: return Ink.green
        case .processing: return Ink.white.withAlphaComponent(0.9)
        case .paused: return Ink.gray
        case .followup: return Ink.white.withAlphaComponent(0.8)
        case .resumed: return Ink.white.withAlphaComponent(0.75)
        }
    }

    private func stroke(_ path: NSBezierPath, _ color: NSColor, width: CGFloat = 1.8) {
        color.setStroke()
        path.lineWidth = width
        path.lineCapStyle = .round
        path.lineJoinStyle = .round
        path.stroke()
    }

    private func drawGlyph() {
        let color = color(for: glyph)
        switch glyph {
        case .waiting:
            mic(color)
        case .resumed:
            // A play triangle: "listening is back on", next to the pause bars it toggles with.
            let play = NSBezierPath()
            play.move(to: NSPoint(x: 9, y: 17))
            play.line(to: NSPoint(x: 17.5, y: 12))
            play.line(to: NSPoint(x: 9, y: 7))
            play.close()
            stroke(play, color, width: 1.7)
        case .recording:
            let elapsed = Date().timeIntervalSince(began)
            let pulse = elapsed.truncatingRemainder(dividingBy: 1.4) / 1.4
            color.withAlphaComponent(0.4 * (1 - pulse)).setFill()
            NSBezierPath(ovalIn: NSRect(x: 12 - 5.5 - 5 * pulse, y: 12 - 5.5 - 5 * pulse,
                                        width: 11 + 10 * pulse, height: 11 + 10 * pulse)).fill()
            color.setFill()
            NSBezierPath(ovalIn: NSRect(x: 6.5, y: 6.5, width: 11, height: 11)).fill()
        case .processing:
            let track = NSBezierPath()
            track.appendArc(withCenter: NSPoint(x: 12, y: 12), radius: 8.5, startAngle: 0, endAngle: 360, clockwise: false)
            stroke(track, Ink.white.withAlphaComponent(0.18), width: 2)
            let angle = Date().timeIntervalSince(began) / 1.1 * 360
            let arc = NSBezierPath()
            arc.appendArc(withCenter: NSPoint(x: 12, y: 12), radius: 8.5, startAngle: angle - 90, endAngle: angle + 10, clockwise: false)
            stroke(arc, color, width: 2.2)
        case .done:
            let check = NSBezierPath()
            check.move(to: NSPoint(x: 6, y: 11.5))
            check.line(to: NSPoint(x: 9.7, y: 7.8))
            check.line(to: NSPoint(x: 18, y: 16.2))
            stroke(check, color, width: 1.9)
        case .saved:
            // A check with an underline: the result was also filed into history.
            let check = NSBezierPath()
            check.move(to: NSPoint(x: 6, y: 13))
            check.line(to: NSPoint(x: 9.5, y: 9.5))
            check.line(to: NSPoint(x: 17, y: 17))
            stroke(check, color)
            let rule = NSBezierPath()
            rule.move(to: NSPoint(x: 8, y: 5.5))
            rule.line(to: NSPoint(x: 16, y: 5.5))
            stroke(rule, color, width: 1.6)
        case .failed:
            let cross = NSBezierPath()
            cross.move(to: NSPoint(x: 6.5, y: 17.5))
            cross.line(to: NSPoint(x: 17.5, y: 6.5))
            cross.move(to: NSPoint(x: 17.5, y: 17.5))
            cross.line(to: NSPoint(x: 6.5, y: 6.5))
            stroke(cross, color, width: 1.9)
        case .paused:
            color.setFill()
            NSBezierPath(roundedRect: NSRect(x: 8.7, y: 6, width: 2.4, height: 12), xRadius: 1.2, yRadius: 1.2).fill()
            NSBezierPath(roundedRect: NSRect(x: 12.9, y: 6, width: 2.4, height: 12), xRadius: 1.2, yRadius: 1.2).fill()
        case .followup:
            // A loop arrow: the follow-up window is open without a wake word. The head sits on the arc's end
            // (330°), not its start, so it reads as an arrow rather than a stray tick.
            let loop = NSBezierPath()
            loop.appendArc(withCenter: NSPoint(x: 12, y: 12), radius: 7, startAngle: 50, endAngle: 330, clockwise: false)
            let end = CGFloat(330) * .pi / 180
            let tip = NSPoint(x: 12 + 7 * cos(end), y: 12 + 7 * sin(end))
            let travel = NSPoint(x: -sin(end), y: cos(end))
            let normal = NSPoint(x: cos(end), y: sin(end))
            let back = NSPoint(x: tip.x - travel.x * 3.4, y: tip.y - travel.y * 3.4)
            loop.move(to: NSPoint(x: back.x + normal.x * 2.4, y: back.y + normal.y * 2.4))
            loop.line(to: tip)
            loop.line(to: NSPoint(x: back.x - normal.x * 2.4, y: back.y - normal.y * 2.4))
            stroke(loop, color, width: 1.7)
        }
    }

    private func mic(_ color: NSColor) {
        let capsule = NSBezierPath(roundedRect: NSRect(x: 9, y: 10, width: 6, height: 11), xRadius: 3, yRadius: 3)
        stroke(capsule, color, width: 1.6)
        let cradle = NSBezierPath()
        cradle.move(to: NSPoint(x: 6, y: 12.5))
        cradle.appendArc(withCenter: NSPoint(x: 12, y: 12.5), radius: 6, startAngle: 180, endAngle: 360, clockwise: false)
        stroke(cradle, color, width: 1.6)
        let stem = NSBezierPath()
        stem.move(to: NSPoint(x: 12, y: 6.5))
        stem.line(to: NSPoint(x: 12, y: 3.5))
        stroke(stem, color, width: 1.6)
    }
}

/// What superwhisper is doing with a recording, beside the listener's own phase.
enum DeliveryState { case recording /* superwhisper's own mic recording */, processing, done, failed, saved }

enum DirectRunEnd { case done, failed, cancelled, superseded }

/// How the newest superwhisper run since `since` ended, polled from the moment its recording stopped.
@MainActor func awaitDirectRun(in recordings: String, since: Int, current: () -> Bool) async -> DirectRunEnd {
    for tick in 0 ..< 300 {
        guard current() else { return .superseded }
        let runs = superwhisperRuns(in: recordings, since: since)
        if let newest = runs.first, !newest.run.inFlight(meta: newest.meta) { return newest.run == .heardNothing ? .failed : .done }
        // superwhisper writes meta.json within 0.2 s of the mic closing; Esc instead deletes the run folder or leaves it
        // empty (measured 2026-10-17). 1.5 s also covers the 0.5 s mic poll.
        if tick >= 15 && runs.isEmpty { return .cancelled }
        try? await Task.sleep(for: .milliseconds(100))
    }
    return current() ? .failed : .superseded
}

/// Floating label at the top of the screen while a dictation is open; the menu bar may be hidden.
final class HUD {
    private let glyphView = HUDGlyphView()
    private lazy var panel: NSPanel = {
        let p = NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        p.level = .statusBar
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        p.ignoresMouseEvents = false
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = true
        let bg = NSVisualEffectView()
        bg.material = .hudWindow
        bg.state = .active
        // The glyph palette assumes a dark pill, so pin the appearance instead of following the system theme.
        bg.appearance = NSAppearance(named: .darkAqua)
        bg.wantsLayer = true
        bg.layer?.cornerRadius = 12
        glyphView.translatesAutoresizingMaskIntoConstraints = false
        glyphView.wantsLayer = true
        bg.addGestureRecognizer(NSClickGestureRecognizer(target: HistoryPanel.shared, action: #selector(HistoryPanel.show)))
        bg.addSubview(glyphView)
        NSLayoutConstraint.activate([
            glyphView.centerXAnchor.constraint(equalTo: bg.centerXAnchor),
            glyphView.centerYAnchor.constraint(equalTo: bg.centerYAnchor),
            glyphView.widthAnchor.constraint(equalToConstant: 22),
            glyphView.heightAnchor.constraint(equalToConstant: 22),
        ])
        p.contentView = bg
        return p
    }()

    private var shown = 0
    private var phase = Listener.Phase.idle
    private var delivery: DeliveryState?
    private var notice: HUDLabel?
    private var loggedLabel: HUDLabel?

    /// The glyph currently on screen, or nil when the HUD is hidden. `--hud-labels` reads it.
    var visibleGlyph: HUDLabel? { panel.isVisible ? glyphView.glyph : nil }

    /// Pause/resume confirmation for 1 s, over whatever the HUD shows; the hotkey otherwise gives no feedback.
    func notify(_ label: HUDLabel) {
        notice = label
        render()
    }

    func historyAvailable() {
        // Wake-word dictation also keeps a copy in history, but its output was delivered, so only an automatic entry earns this label.
        guard phase != .recording && phase != .waiting && phase != .followup,
              DictationHistory.shared.snapshot().first?.origin.isAutomatic == true else { return }
        deliver(.saved)
    }

    /// icon: the waking action's `icon`, or a sticky dictation's target app, drawn in place of the mic while .waiting;
    /// nil keeps the mic.
    func show(_ phase: Listener.Phase, icon: String? = nil) {
        self.phase = phase
        // Cleared on every other phase, so an icon left from the last action never shows on a later dictation.
        glyphView.actionImage = phase == .waiting || phase == .recording ? icon.flatMap(Self.image) : nil
        render()
    }

    private static var images: [String: NSImage?] = [:]

    /// An .app gives its own icon; anything else is read as an image. A file that fails to load keeps the mic.
    private static func image(_ path: String) -> NSImage? {
        let full = (path as NSString).expandingTildeInPath
        if let cached = images[full] { return cached }
        let image = full.hasSuffix(".app") ? (FileManager.default.fileExists(atPath: full) ? NSWorkspace.shared.icon(forFile: full) : nil)
            : NSImage(contentsOfFile: full)
        if image == nil { log("hud: action icon \(full) could not be loaded") }
        images[full] = image
        return image
    }

    /// .done leaves the saved glyph up, since the automatic path saves to history before its handoff returns.
    func deliver(_ state: DeliveryState?) {
        // The handoff that .ended was waiting for has begun or finished, so .ended no longer holds the HUD.
        if phase == .ended { phase = .idle }
        if delivery != .saved || (state != nil && state != .done) { delivery = state }
        render()
    }

    /// hideNow: an expiry ends the pill on time; only a state change waits for the next state before hiding.
    private func render(hideNow: Bool = false) {
        shown += 1
        let view: (label: HUDLabel, expiry: Double?)? = if let notice { (notice, 1) } else { switch (phase, delivery) {
        case (.waiting, _): (.waiting, nil)
        // The same glyph a confirmed stop shows, so the user sees the stop word land the moment it is recognized.
        case (.stopping, _): (.processing, nil)
        case (.recording, _), (_, .recording?): (.recording, nil)
        case (_, .processing?): (.processing, nil)
        case (_, .failed?): (.failed, 3)
        case (_, .done?): (.done, 1)
        case (_, .saved?): (.saved, 1.5)
        // A short recording dropped before its handoff never reports a delivery.
        case (.ended, nil): (.processing, 1.5)
        case (.followup, nil): (.followup, nil)
        case (.idle, nil): nil
        } }
        // A HUD the user reported missing leaves no other trace, so each visible change is logged.
        if view?.label != loggedLabel {
            loggedLabel = view?.label
            log("hud: \(view.map { "\($0.label)" } ?? "hidden") phase=\(phase) delivery=\(delivery.map { "\($0)" } ?? "-")")
        }
        // Animation must stop with the pill: a hidden HUD that keeps a 30 fps timer burns CPU for nothing.
        // The next state often arrives a moment later (recording → processing), so the pill waits briefly before hiding
        // instead of vanishing and reappearing.
        guard let view else {
            if hideNow { glyphView.stop(); panel.orderOut(nil); return }
            let mine = shown
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [self] in
                guard shown == mine else { return }
                glyphView.stop(); panel.orderOut(nil)
            }
            return
        }
        // A glyph change on the visible pill fades, so it reads as the same HUD changing its icon.
        if panel.isVisible, glyphView.glyph != view.label, let layer = glyphView.layer {
            let fade = CATransition()
            fade.type = .fade
            fade.duration = 0.15
            layer.add(fade, forKey: "glyph")
        }
        glyphView.show(view.label)
        if let expiry = view.expiry {
            let mine = shown
            DispatchQueue.main.asyncAfter(deadline: .now() + expiry) { [self] in
                guard shown == mine else { return }
                notice = nil
                if phase == .ended { phase = .idle }
                if delivery == .failed || delivery == .saved || delivery == .done { delivery = nil }
                render(hideNow: true)
            }
        }
        // Follow the focused window even when the pointer remains on another display.
        let screen = NSScreen.main ?? NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) }
        guard let area = screen?.visibleFrame else { return }
        let size = NSSize(width: 64, height: 40)
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
    let sticky = NSMenuItem(title: "スティッキー貼り付け", action: #selector(toggleSticky), keyEquivalent: "")
    let requests = NSMenuItem(title: "依頼", action: #selector(showRequests), keyEquivalent: "")
    let micMenu = NSMenu()
    let hud = HUD()
    private var listeningPolicy = ListeningPolicy()
    private var captureReady = false
    private var pauseMicrophoneName: String?
    private var triageReviewCount = 0
    private var triagePanel: TriagePanel?

    init(configPath: String) { self.configPath = configPath }

    func applicationDidFinishLaunching(_: Notification) {
        let menu = NSMenu()
        MacWakeShortcuts.install()
        menu.addItem(toggle)
        menu.addItem(NSMenuItem(title: "文字起こし履歴", action: #selector(showHistory), keyEquivalent: ""))
        requests.isHidden = true
        menu.addItem(requests)
        menu.addItem(.separator())
        let mic = NSMenuItem(title: "マイク", action: nil, keyEquivalent: "")
        micMenu.delegate = self
        mic.submenu = micMenu
        menu.addItem(mic)
        menu.addItem(NSMenuItem(title: "設定ファイルを開く", action: #selector(openConfig), keyEquivalent: ","))
        menu.addItem(NSMenuItem(title: "ログを開く", action: #selector(openLog), keyEquivalent: "l"))
        menu.addItem(sound)
        menu.addItem(sticky)
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
        pauseMicrophoneName = config.cfg.pauseWhenMicrophoneConnected
        DictationJournal.bind(config.cfg.dictation?.journalDir, journalTaskFile: config.cfg.dictation?.journalTaskFile)
        if let settings = config.cfg.dictation?.triage, settings.enabled == true {
            let triage = TriageService(settings: settings, journalDir: config.cfg.dictation?.journalDir)
            triagePanel = TriagePanel(service: triage)
            triage.onChange = { [weak self, weak triage] in
                guard let self else { return }
                self.triageReviewCount = triage?.review.count ?? 0
                self.refresh()
                self.triagePanel?.refresh()
            }
            requests.isHidden = false
            triage.start()
        }
        listener = Listener(config: config)
        listener.onPhase = { [hud] in hud.show($0, icon: $1) }
        listener.onDelivery = { [hud] in hud.deliver($0) }
        DictationHistory.shared.onChange = { [hud] in DispatchQueue.main.async { hud.historyAvailable() } }
        listener.onRestart = { [weak self] in self?.refresh() }
        listener.onInputDevicesChanged = { [weak self] in self?.followPauseMicrophone() }
        listener.deviceUID = UserDefaults.standard.string(forKey: deviceKey)
        listener.fallbackUIDs = UserDefaults.standard.stringArray(forKey: fallbackKey) ?? []
        listener.configuredMicrophones = config.cfg.microphones.flatMap { $0.isEmpty ? nil : $0 }
        MacWakeShortcuts.registerPause(config.cfg.pauseShortcut)
        MacWakeShortcuts.registerActions(config.cfg.macOS?.actions ?? [])
        let recordings = Platform.expandPath(config.cfg.dictation?.recordingsDir ?? Platform.defaultRecordingsDir)
        // ponytail: 0.5 s CoreAudio poll; a kAudioProcessPropertyIsRunningInput listener if it shows up in CPU.
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in self?.followDirectSuperwhisper(recordings: recordings) }
        meetEnabled = config.cfg.pauseDuringMeet == true
        if meetEnabled { Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in self?.followMeet() } }
        Task { @MainActor in
            do {
                if let ownership = try? MacModeRestoreStore.acquire() {
                    let pending = (try? MacModeRestoreStore.read()) != nil
                    let settled = pending ? await awaitRunsSettled(in: recordings) : true
                    if settled {
                        _ = await recoverSuperwhisperMode(target: nil)
                        if let wanted = config.cfg.dictation?.pasteMode { await selectPasteMode(wanted) }
                    } else {
                        log("superwhisper mode: an earlier run is still in flight; the next handoff restores the mode")
                    }
                    withExtendedLifetime(ownership) {}
                }
                let locale = Locale(identifier: config.cfg.locale ?? "ja_JP")
                try await ensureModel(locale)
                _ = try await transcribe([Float](repeating: 0, count: Int(rate)), locale: locale) // warm up
                guard await AVCaptureDevice.requestAccess(for: .audio) else {
                    fail("マイクへのアクセスが許可されていません。システム設定 > プライバシーとセキュリティ > マイク で許可してください。")
                    return
                }
                captureReady = true
                updatePauseMicrophone()
                let browser = activeMeetBrowser()
                listeningPolicy.configureMeet(inCall: browser != nil)
                if let browser { log("meet: \(browser) holds the microphone, pausing") }
                reconcileListening(startError: "起動に失敗しました", notify: false)
            } catch {
                fail("起動に失敗しました: \(error)")
            }
            refresh()
        }
    }

    private var directSince: Int?
    private var directGeneration = 0

    /// superwhisper started with its own key; the user turns its HUD off, so ours shows the recording and the run after it.
    private func followDirectSuperwhisper(recordings: String) {
        let recording = micInUse(by: ["com.superduper.superwhisper"]) != nil
        if recording, directSince == nil {
            // superwhisper names the run folder after the second recording began and creates it then, so a folder
            // snapshot here already holds this run (2026-10-17: run 1792208408 was excluded and ⏳ never ended).
            // The 0.5 s poll sees the mic at most a second late.
            directSince = Int(Date().timeIntervalSince1970) - 1
            directGeneration += 1
            log("superwhisper direct: recording")
            hud.deliver(.recording)
        } else if !recording, let since = directSince {
            directSince = nil
            let generation = directGeneration
            log("superwhisper direct: processing")
            hud.deliver(.processing)
            Task { @MainActor [weak self, hud] in
                // A later recording owns the HUD; this run's outcome must not overwrite it.
                let end = await awaitDirectRun(in: recordings, since: since, current: { self?.directGeneration == generation })
                guard end != .superseded else { return }
                log("superwhisper direct: \(end)")
                hud.deliver(end == .done ? .done : end == .failed ? .failed : nil)
            }
        }
    }

    func refresh() {
        let on = listener?.running ?? false
        let count = triageReviewCount
        item.length = count == 0 ? NSStatusItem.squareLength : NSStatusItem.variableLength
        item.button?.title = (on ? "🔔" : "🔕") + (count == 0 ? "" : " \(count)")
        let accessibility = on ? "voice-switch: 待ち受け中" : "voice-switch: 停止中"
        item.button?.setAccessibilityLabel(accessibility + (count == 0 ? "" : "、確認が要る依頼 \(count) 件"))
        if listeningPolicy.microphoneConnected {
            toggle.title = listeningPolicy.offersPause ? "切断後の自動再開を停止" : "切断後の自動再開を有効にする"
        } else {
            toggle.title = captureReady && listeningPolicy.offersPause ? "一時停止" : "再開"
        }
        toggle.isEnabled = captureReady
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        sound.state = UserDefaults.standard.bool(forKey: soundKey) ? .on : .off
        let override = stickyTargetOverride()
        let configFlag = listener?.config.cfg.macOS?.stickyTarget
        sticky.state = stickyEnabled(configFlag: configFlag, override: override) ? .on : .off
    }

    @objc func toggleSticky() {
        let override = stickyTargetOverride()
        let configFlag = listener?.config.cfg.macOS?.stickyTarget
        let current = stickyEnabled(configFlag: configFlag, override: override)
        UserDefaults.standard.set(!current, forKey: stickyTargetKey)
        refresh()
    }

    @objc func showHistory() { HistoryPanel.shared.show() }
    @objc func showRequests() { Task { @MainActor in triagePanel?.show() } }

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

    /// Browsers whose microphone input means a Google Meet call (Chrome and Edge only).
    // ponytail: any mic use by these browsers counts, not only Meet; matching the Meet tab needs Screen Recording permission.
    static let meetBrowsers = ["com.google.Chrome", "com.google.Chrome.helper", "com.microsoft.edgemac", "com.microsoft.edgemac.helper"]
    private var meetEnabled = false

    private func activeMeetBrowser() -> String? {
        meetEnabled ? micInUse(by: Self.meetBrowsers) : nil
    }

    private func followMeet() {
        guard captureReady else { return }
        let browser = activeMeetBrowser()
        listeningPolicy.configureMeet(inCall: browser != nil)
        guard listener.wanted != listeningPolicy.shouldListen else { return }
        if let browser {
            log("meet: \(browser) holds the microphone, pausing")
        } else {
            log("meet: the browser released the microphone, resuming")
        }
        reconcileListening()
    }

    private func updatePauseMicrophone() {
        guard let pauseMicrophoneName, !pauseMicrophoneName.isEmpty else { return }
        let connected = inputDeviceIDs().contains { audioDeviceName($0) == pauseMicrophoneName }
        guard listeningPolicy.microphoneConnected != connected else { return }
        listeningPolicy.microphoneConnected = connected
        log("mic pause: \(pauseMicrophoneName) \(connected ? "connected" : "disconnected")")
    }

    private func followPauseMicrophone() {
        let wasConnected = listeningPolicy.microphoneConnected
        updatePauseMicrophone()
        guard wasConnected != listeningPolicy.microphoneConnected else { return }
        if wasConnected { listeningPolicy.configureMeet(inCall: activeMeetBrowser() != nil) }
        reconcileListening()
    }

    private func reconcileListening(startError: String? = nil, notify: Bool = true) {
        defer { refresh() }
        guard captureReady, listener.wanted != listeningPolicy.shouldListen else { return }
        if !listeningPolicy.shouldListen {
            listener.stop()
        } else if let startError {
            do { try listener.start() } catch {
                listeningPolicy.pause()
                fail("\(startError): \(error)")
            }
        } else {
            listener.startRetrying()
        }
        if notify { hud.notify(listener.wanted ? .resumed : .paused) }
    }

    @objc func togglePause() {
        guard captureReady else { return }
        updatePauseMicrophone()
        let inCall = listeningPolicy.offersPause ? nil : activeMeetBrowser() != nil
        listeningPolicy.toggle(inCall: inCall)
        reconcileListening(startError: "再開に失敗しました")
    }

    // Rebuilt on every open so plugged/unplugged devices show up.
    func menuNeedsUpdate(_ menu: NSMenu) {
        let selected = UserDefaults.standard.string(forKey: deviceKey)
        let devices = inputDeviceIDs().compactMap { id in audioDeviceUID(id).map { (audioDeviceName(id), $0) } }
        menu.removeAllItems()
        menu.addItem(NSMenuItem(title: "使用中: \(listener?.activeDeviceName ?? "なし")", action: nil, keyEquivalent: ""))
        menu.addItem(.separator())
        // config.json decides the order, so the menu only shows it; picking here would be ignored.
        if let configured = listener?.configuredMicrophones {
            for (n, name) in configured.enumerated() { menu.addItem(NSMenuItem(title: "\(n + 1). \(name)", action: nil, keyEquivalent: "")) }
            menu.addItem(NSMenuItem(title: "（設定ファイルの microphones で指定）", action: nil, keyEquivalent: ""))
            return
        }
        for (title, uid) in [("システムのデフォルト", nil)] + devices.map({ ($0.0, Optional($0.1)) }) {
            let i = NSMenuItem(title: title, action: #selector(selectDevice), keyEquivalent: "")
            i.target = self
            i.representedObject = uid
            i.state = uid == selected ? .on : .off
            menu.addItem(i)
        }
        // Listed mics first, numbered in the order they are tried; then the plugged-in mics not listed yet.
        // Clicking adds a mic to the end of the list or removes it; reordering is remove and add again.
        let listed = listener?.fallbackUIDs ?? []
        let fallbacks = NSMenu()
        let names = Dictionary(devices.map { ($0.1, $0.0) }, uniquingKeysWith: { a, _ in a })
        for (n, uid) in listed.enumerated() {
            fallbacks.addItem(fallbackItem("\(n + 1). \(names[uid] ?? "未接続 (\(uid))")", uid: uid, on: true))
        }
        for (title, uid) in devices where uid != selected && !listed.contains(uid) {
            fallbacks.addItem(fallbackItem(title, uid: uid, on: false))
        }
        let fb = NSMenuItem(title: "予備マイク（保存したマイクが無いとき）", action: nil, keyEquivalent: "")
        fb.submenu = fallbacks
        menu.addItem(.separator())
        menu.addItem(fb)
    }

    private func fallbackItem(_ title: String, uid: String, on: Bool) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: #selector(toggleFallback), keyEquivalent: "")
        i.target = self
        i.representedObject = uid
        i.state = on ? .on : .off
        return i
    }

    @objc func toggleFallback(_ sender: NSMenuItem) {
        guard let uid = sender.representedObject as? String, let listener else { return }
        var list = listener.fallbackUIDs
        if let at = list.firstIndex(of: uid) { list.remove(at: at) } else { list.append(uid) }
        UserDefaults.standard.set(list, forKey: fallbackKey)
        listener.fallbackUIDs = list
        // Same path as a plug event, so an edit takes effect now rather than at the next plug.
        listener.reroute("fallback mics changed")
        refresh()
    }

    @objc func selectDevice(_ sender: NSMenuItem) {
        let uid = sender.representedObject as? String
        UserDefaults.standard.set(uid, forKey: deviceKey)
        guard let listener else { return }
        if listener.wanted { listener.stop() }
        listener.deviceUID = uid
        updatePauseMicrophone()
        listeningPolicy.configureMeet(inCall: activeMeetBrowser() != nil)
        reconcileListening(startError: "マイクの切り替えに失敗しました", notify: false)
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
