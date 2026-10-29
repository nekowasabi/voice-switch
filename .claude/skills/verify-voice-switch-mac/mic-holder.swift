import AVFoundation
import CoreAudio

// Holds the mic the way voice-switch would: "vpio" = AVAudioEngine voice processing, "vad" = HAL voice activity detection only.
let mode = CommandLine.arguments[1]
let seconds = Double(CommandLine.arguments[2])!
var engine: AVAudioEngine?
var vadFires = 0
var polledOn = 0
// The app opens an automatic session only after one unbroken on-run of Listener.minVoiceSeconds, so the longest run matters, not the total.
var onRun = 0
var longestRun = 0
if mode == "vpio" {
    let e = AVAudioEngine()
    try! e.inputNode.setVoiceProcessingEnabled(true)
    e.inputNode.installTap(onBus: 0, bufferSize: 4096, format: e.inputNode.inputFormat(forBus: 0)) { _, _ in }
    try! e.start()
    engine = e
    print("holder vpio running")
} else {
    let id = AudioDeviceID(UInt32(CommandLine.arguments[3])!)
    var addr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyVoiceActivityDetectionEnable, mScope: kAudioDevicePropertyScopeInput, mElement: kAudioObjectPropertyElementMain)
    var on: UInt32 = 1
    let has = AudioObjectHasProperty(id, &addr)
    let st = AudioObjectSetPropertyData(id, &addr, 0, nil, 4, &on)
    var state = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyVoiceActivityDetectionState, mScope: kAudioDevicePropertyScopeInput, mElement: kAudioObjectPropertyElementMain)
    AudioObjectAddPropertyListenerBlock(id, &state, .main) { _, _ in
        var v: UInt32 = 0; var s = UInt32(4)
        AudioObjectGetPropertyData(id, &state, 0, nil, &s, &v)
        if v != 0 { vadFires += 1 }
    }
    // HAL only runs detection while some client is doing input IO on the device.
    let e = AVAudioEngine()
    var dev = id
    AudioUnitSetProperty(e.inputNode.audioUnit!, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &dev, 4)
    e.inputNode.installTap(onBus: 0, bufferSize: 4096, format: e.inputNode.inputFormat(forBus: 0)) { _, _ in }
    try! e.start()
    engine = e
    var readBack: UInt32 = 0; var rs = UInt32(4)
    AudioObjectGetPropertyData(id, &addr, 0, nil, &rs, &readBack)
    print("holder vad has=\(has) set=\(st) enabled=\(readBack)")
    Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { _ in
        var v: UInt32 = 0; var s = UInt32(4)
        if AudioObjectGetPropertyData(id, &state, 0, nil, &s, &v) == noErr, v != 0 {
            polledOn += 1; onRun += 1; longestRun = max(longestRun, onRun)
        } else { onRun = 0 }
    }
}
RunLoop.main.run(until: Date().addingTimeInterval(seconds))
if mode == "vad" { print("holder vad activity events=\(vadFires) polled-on=\(polledOn)/\(Int(seconds / 0.05)) longest-on-ms=\(longestRun * 50)") }
_ = engine
