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

/// Synthetic tones through the Segmenter — no Speech/AVFoundation required.
func vadSelftest() {
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
    if sawUtterance {
        print("vad-selftest: ok")
    } else {
        print("vad-selftest: FAILED (no utterance)")
        exit(1)
    }
}
