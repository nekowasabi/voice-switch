// Energy VAD / utterance segmentation — pure Swift, shared by macOS and Windows.
import Foundation

let rate = 16000.0
let frameLen = 480 // 30 ms

func frames(ms: Int) -> Int { max(1, ms * Int(rate) / 1000 / frameLen) }

/// Same state machine as the Python prototype, with an adaptive-floor energy VAD in place of webrtcvad.
struct Segmenter {
    var cfg: Config
    private var floor: Float = 0.001 // typical quiet-room mic level; adapts within a second
    private var ring: [[Float]] = []
    private var utt: [[Float]] = []
    private var silent = 0
    private var skipping = false
    private var lastRms: Float = 0
    private var levelSum: Double = 0
    private var levelCount = 0
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

    var noiseFloor: Float { floor }
    var threshold: Float { max(floor * (cfg.vadRatio ?? 3), cfg.vadMinRMS ?? 0.005) }
    var isSkipping: Bool { skipping }

    // The floor only adapts while quiet, so steady sound above the threshold reads as speech forever. Once the
    // recognizer heard no wake word in the over-cap head, that sound is the room, and its mean level becomes the floor.
    // Mirrors VoiceSwitch.Windows.Core.Segmenter.RebaseFloor.
    mutating func rebaseFloor() {
        if skipping && levelCount > 0 {
            floor = max(floor, Float(levelSum / Double(levelCount)))
        }
    }

    private mutating func isSpeech(_ f: [Float]) -> Bool {
        let rms = (f.reduce(0) { $0 + $1 * $1 } / Float(f.count)).squareRoot()
        lastRms = rms
        let speech = rms > threshold
        // Track the noise floor only while quiet, so speech does not raise it.
        if !speech && adaptFloor { floor = floor * 0.95 + rms * 0.05 }
        return speech
    }

    /// Early wake probe (experimental, cfg.earlyWakeMs). While an utterance is open and under the cap, returns the
    /// buffered audio when a short internal gap (2 silent frames, 60 ms) just closed or `earlyWakeMs` of audio
    /// accumulated since the last probe, so the caller can run STT before the hangover. nil when off or not due.
    private var lastProbe = 0
    /// Silence that closes a wake word which is also the start of a longer one ("音声" / "音声入力"). 150 ms is longer
    /// than the ~90 ms gaps inside 音声入力, so "音声" followed by this much silence is the whole word.
    // ponytail: fixed 150 ms from synthetic speech; make it a config knob if real voices pause longer inside the word.
    static let prefixGapFrames = 5
    mutating func probe() -> [Float]? {
        guard let every = cfg.earlyWakeMs, !utt.isEmpty, !skipping else { return nil }
        let minFrames = frames(ms: cfg.minSpeechMs ?? 300)
        guard utt.count >= minFrames else { return nil }
        let gap = silent == 2 || silent == Segmenter.prefixGapFrames
        guard gap || utt.count - lastProbe >= frames(ms: every) else { return nil }
        lastProbe = utt.count
        return Array(utt.joined())
    }

    /// Frames of silence at the tail of the open utterance (0 while speech continues).
    var tailSilentFrames: Int { silent }

    /// Early-probe verdict. A transcript that is the start of a longer wake word ("音声入" or "音声" for "音声入力")
    /// may be mid-word: it fires only as a bare wake word, and only after `prefixGapFrames` of silence.
    static func earlyWake(_ tr: Transcript, cfg: Config, tailSilentFrames: Int) -> (cutAt: Double, rest: String)?? {
        let words = cfg.wakeWords.map(normalize)
        if words.contains(where: { $0.count > tr.text.count && $0.hasPrefix(tr.text) }) {
            return tailSilentFrames >= prefixGapFrames && words.contains(tr.text) ? .some(nil) : nil
        }
        if let start = dictationStart(tr, wakeWords: cfg.wakeWords) { return .some(start) }
        if tailSilentFrames > 0, words.contains(tr.text) { return .some(nil) }
        return nil
    }

    /// Drop the open utterance after an early wake fired, so the hangover does not report it a second time.
    mutating func reset() {
        utt = []; ring = []; skipping = false; silent = 0; lastProbe = 0
        levelSum = 0; levelCount = 0
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
            if speech {
                utt = ring
                silent = 0
                levelSum = Double(lastRms)
                levelCount = 1
            }
            return nil
        }
        silent = speech ? 0 : silent + 1
        levelSum += Double(lastRms)
        levelCount += 1
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
        utt = []; ring = []; skipping = false; lastProbe = 0
        levelSum = 0; levelCount = 0
        return done ?? head
    }
}

/// Synthetic tones through the Segmenter — no Speech/AVFoundation required.
func vadSelftest() {
    // RESEARCH hyp 2: deadline helper matches Windows TranscribeDeadline (no Apple Speech needed).
    guard transcribeDeadlineSeconds(audioSeconds: 0) == 20,
          transcribeDeadlineSeconds(audioSeconds: 5) == 25,
          transcribeDeadlineSeconds(audioSeconds: -15) == 10,
          abs(transcribeDeadlineSeconds(audioSeconds: 0.5) - 20.5) < 0.001,
          transcribeDeadlineSeconds(audioSeconds: Double(16000) / rate) == 21 else {
        print("vad-selftest: FAILED (transcribe deadline formula)")
        exit(1)
    }
    let cfg = Config(wakeWords: ["test"], command: "true",
                     maxSeconds: 2.5, hangoverMs: 300, prerollMs: 300, minSpeechMs: 300,
                     vadRatio: 3.0, vadMinRMS: 0.005)
    var seg = Segmenter(cfg: cfg)
    var sawUtterance = false
    // 200 ms silence, 500 ms loud tone, 500 ms silence → one utterance.
    let quiet = [Float](repeating: 0.0001, count: frameLen)
    let loud = (0..<frameLen).map { i in Float(0.2 * sin(Float(i) * 0.5)) }
    for _ in 0..<7 { _ = seg.push(quiet) }
    for _ in 0..<17 { _ = seg.push(loud) }
    for _ in 0..<20 {
        if case .utterance(let u)? = seg.push(quiet) {
            sawUtterance = u.count > frameLen
            break
        }
    }
    guard sawUtterance else {
        print("vad-selftest: FAILED (no utterance)")
        exit(1)
    }

    // Windows DictationSegmenterRebasesFloorAfterSteadyNoiseFromFirstFrame: steady above-threshold
    // noise traps skipping until RebaseFloor lifts the floor to the mean level.
    seg = Segmenter(cfg: cfg)
    let noise = [Float](repeating: 0.01, count: frameLen)
    let voice = [Float](repeating: 0.1, count: frameLen)
    var head: Segmenter.Event?
    var headAt = -1
    for i in 0..<120 where head == nil {
        head = seg.push(noise)
        headAt = i
    }
    var stillLocked = true
    for _ in 0..<30 {
        stillLocked = stillLocked && seg.push(noise) == nil && seg.lastWasSpeech
    }
    guard case .head? = head, (90...100).contains(headAt), stillLocked else {
        print("vad-selftest: FAILED (steady noise did not trap skipping; headAt=\(headAt) locked=\(stillLocked))")
        exit(1)
    }
    let floorBefore = seg.noiseFloor
    seg.rebaseFloor()
    guard seg.noiseFloor >= floorBefore, seg.noiseFloor >= 0.01 * 0.9 else {
        print("vad-selftest: FAILED (rebase did not raise floor; \(floorBefore)->\(seg.noiseFloor))")
        exit(1)
    }
    var quietAfter = 0
    for _ in 0..<12 {
        _ = seg.push(noise)
        if !seg.lastWasSpeech { quietAfter += 1 }
    }
    for _ in 0..<12 { _ = seg.push(voice) }
    var wake: Segmenter.Event?
    for _ in 0..<11 where wake == nil {
        wake = seg.push(noise)
    }
    guard quietAfter == 12, case .utterance(let u)? = wake, u.count >= frameLen * 12 else {
        print("vad-selftest: FAILED (after rebase: quietAfter=\(quietAfter) wake=\(String(describing: wake)))")
        exit(1)
    }
    print("vad-selftest: ok")
}
