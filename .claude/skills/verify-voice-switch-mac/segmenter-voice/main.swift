// Speech the HAL detector hears but the energy gate does not must still open, cut and end utterances (MS16, MS17e).
// Build and run with segmenter-voice/run.sh; exit 0 only when every check passes.
import Foundation

let cfg = Config(wakeWords: ["test"], command: "true",
                 maxSeconds: 2.5, hangoverMs: 300, prerollMs: 300, minSpeechMs: 300,
                 vadRatio: 3.0, vadMinRMS: 0.005)
let silence = [Float](repeating: 0.0001, count: frameLen)
// RMS 0.0021, under vadMinRMS 0.005: the raw built-in mic's quiet voice under a raised floor.
let quietVoice = (0..<frameLen).map { i in Float(0.003 * sin(Float(i) * 0.5)) }
let loud = (0..<frameLen).map { i in Float(0.2 * sin(Float(i) * 0.5)) }
var failed = 0
func check(_ ok: Bool, _ what: String) {
    print("\(ok ? "ok  " : "FAIL") \(what)")
    if !ok { failed += 1 }
}

do {
    var seg = Segmenter(cfg: cfg)
    for _ in 0..<7 { _ = seg.push(silence, voice: false) }
    let floorBefore = seg.noiseFloor
    var allSpeech = true, early: Segmenter.Event?
    for _ in 0..<34 {
        early = early ?? seg.push(quietVoice, voice: true)
        allSpeech = allSpeech && seg.lastWasSpeech
    }
    check(seg.lastRms < seg.threshold, "quiet voice stays under the energy threshold (\(seg.lastRms) < \(seg.threshold))")
    check(allSpeech, "a frame the detector hears counts as speech (lastWasSpeech)")
    check(seg.noiseFloor == floorBefore, "the floor does not adapt toward detector-voiced frames (\(floorBefore) -> \(seg.noiseFloor))")
    var end: Segmenter.Event?
    for _ in 0..<20 where end == nil { end = seg.push(silence, voice: false) }
    if case .utterance(let u)? = end {
        check(early == nil && u.count >= 34 * frameLen, "detector-voiced speech ends as one utterance of \(u.count / frameLen) frames")
    } else {
        check(false, "detector-voiced speech ends as an utterance (got \(String(describing: end)))")
    }
}

do {
    var seg = Segmenter(cfg: cfg)
    var head = false, tail: [Float]?
    for _ in 0..<7 { _ = seg.push(silence, voice: false) }
    for _ in 0..<110 { if case .head? = seg.push(quietVoice, voice: true) { head = true } }
    for _ in 0..<20 where tail == nil { if case .tail(let t)? = seg.push(silence, voice: false) { tail = t } }
    check(head, "detector-voiced speech past maxSeconds reports a head")
    check((tail?.count ?? 0) >= 83 * frameLen, "it ends with a tail of the last maxSeconds, where a stop word said without a pause lands (\((tail?.count ?? 0) / frameLen) frames)")
}

do {
    var seg = Segmenter(cfg: cfg)
    var event: Segmenter.Event?, anySpeech = false
    for _ in 0..<7 { _ = seg.push(silence, voice: false) }
    for _ in 0..<34 { event = event ?? seg.push(quietVoice, voice: false); anySpeech = anySpeech || seg.lastWasSpeech }
    for _ in 0..<20 { event = event ?? seg.push(silence, voice: false) }
    check(event == nil && !anySpeech, "without the detector the same quiet voice is not speech (energy-only, device 0 and --simulate)")
}

do {
    var seg = Segmenter(cfg: cfg)
    var end: Segmenter.Event?
    for _ in 0..<7 { _ = seg.push(silence, voice: false) }
    for _ in 0..<17 { _ = seg.push(loud, voice: false) }
    for _ in 0..<20 where end == nil { end = seg.push(silence, voice: false) }
    if case .utterance? = end { check(true, "loud speech without the detector is still an utterance") }
    else { check(false, "loud speech without the detector is still an utterance (got \(String(describing: end)))") }
}

print(failed == 0 ? "segmenter-voice: ok" : "segmenter-voice: \(failed) FAILED")
vadSelftest()
exit(failed == 0 ? 0 : 1)
