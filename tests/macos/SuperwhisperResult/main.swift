// Build and run (on macOS):
//   swiftc Sources/voice-switch/SuperwhisperModes.swift tests/macos/SuperwhisperResult/main.swift -o /tmp/superwhisper-result
//   /tmp/superwhisper-result; echo "exit=$?"
// The meta.json sequences are what superwhisper 3.0.1 wrote on 2026-10-16 (observed every 20 ms).
import Foundation

func check(_ ok: Bool, _ what: String) {
    print((ok ? "PASS " : "FAIL ") + what)
    if !ok { exit(1) }
}

let root = FileManager.default.temporaryDirectory.appendingPathComponent("superwhisper-result-\(UUID().uuidString)").path
func write(_ run: String, _ json: String) {
    try! FileManager.default.createDirectory(atPath: "\(root)/\(run)", withIntermediateDirectories: true)
    try! json.write(toFile: "\(root)/\(run)/meta.json", atomically: true, encoding: .utf8)
}

func run(_ name: String, waitForLLM: Bool, steps: [(ms: UInt64, json: String)]) async -> String? {
    let dir = "\(root)/\(name)"
    try! FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
    let writer = Task {
        for step in steps {
            try? await Task.sleep(nanoseconds: step.ms * 1_000_000)
            try! FileManager.default.createDirectory(atPath: "\(dir)/100", withIntermediateDirectories: true)
            try! step.json.write(toFile: "\(dir)/100/meta.json", atomically: true, encoding: .utf8)
        }
    }
    let result = await awaitResult(in: dir, since: 100, waitForLLM: waitForLLM)
    writer.cancel()
    return result
}

let placeholder = #"{"result":"","processingTime":0,"duration":0}"#
let transcribed = #"{"result":"ああなるほど。","processingTime":1006,"duration":6261}"#
let polished = #"{"result":"ああなるほど。","processingTime":1006,"duration":6261,"llmResult":"なるほど。","languageModelProcessingTime":1398}"#
let heardNothing = #"{"result":"","processingTime":18,"duration":3000}"#

Task {
    check(await run("llm", waitForLLM: true, steps: [(0, placeholder), (700, transcribed), (1400, polished)]) == "なるほど。",
          "the placeholder write is not a finished run; the language model result is awaited")
    check(await run("plain", waitForLLM: false, steps: [(0, placeholder), (700, transcribed)]) == "ああなるほど。",
          "without a language model the transcribed result is final")
    check(await run("silent", waitForLLM: true, steps: [(0, placeholder), (500, heardNothing)]) == "",
          "a filled duration with an empty result is a run that heard no words")
    try? FileManager.default.removeItem(atPath: root)
    exit(0)
}
dispatchMain()
