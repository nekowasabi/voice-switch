import AVFoundation
import CoreAudio

// Stands in for another app (superwhisper) opening the mic without voice processing.
let seconds = Double(CommandLine.arguments[1])!
let pinID = CommandLine.arguments.count > 2 ? AudioDeviceID(UInt32(CommandLine.arguments[2])!) : nil
let e = AVAudioEngine()
if var id = pinID {
    AudioUnitSetProperty(e.inputNode.audioUnit!, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &id, 4)
}
var current = AudioDeviceID(0); var size = UInt32(4)
AudioUnitGetProperty(e.inputNode.audioUnit!, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &current, &size)
let fmt = e.inputNode.inputFormat(forBus: 0)
let mono = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: fmt.sampleRate, channels: 1, interleaved: false)!
let down = AVAudioConverter(from: fmt, to: mono)!
var perCh = [[Float]](repeating: [], count: Int(fmt.channelCount)), mixed: [Float] = []
func rms(_ p: UnsafePointer<Float>, _ n: Int) -> Float { var s: Float = 0; for i in 0..<n { s += p[i] * p[i] }; return (s / Float(max(n, 1))).squareRoot() }
e.inputNode.installTap(onBus: 0, bufferSize: 4096, format: fmt) { b, _ in
    for c in 0..<Int(b.format.channelCount) { perCh[c].append(rms(b.floatChannelData![c], Int(b.frameLength))) }
    let out = AVAudioPCMBuffer(pcmFormat: mono, frameCapacity: b.frameLength)!
    var fed = false
    down.convert(to: out, error: nil) { _, st in if fed { st.pointee = .noDataNow; return nil }; fed = true; st.pointee = .haveData; return b }
    mixed.append(rms(out.floatChannelData![0], Int(out.frameLength)))
}
try! e.start()
RunLoop.main.run(until: Date().addingTimeInterval(seconds))
func db(_ a: [Float]) -> String {
    guard !a.isEmpty else { return "no buffers" }
    let s = a.sorted(); let f = { (v: Float) in v > 0 ? String(format: "%.1f", 20 * log10(v)) : "-inf" }
    return "median \(f(s[s.count / 2])) dB, max \(f(s.last!)) dB, zero-buffers \(a.filter { $0 == 0 }.count)/\(a.count)"
}
print("reader device=\(current) format=\(fmt.channelCount)ch \(fmt.sampleRate)Hz")
for (c, a) in perCh.enumerated() { print("  ch\(c): \(db(a))") }
print("  downmix: \(db(mixed))")
