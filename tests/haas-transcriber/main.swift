import Foundation

func decodeConfig(_ json: String) throws -> Config {
    try JSONDecoder().decode(Config.self, from: Data(json.utf8))
}

// 1. Absent transcriber stays superwhisper.
let absentJSON = """
{"wakeWords": ["test"], "command": "true", "dictation": {"recordingsDir": "rec"}}
"""
do {
    let cfg = try decodeConfig(absentJSON)
    let mode = cfg.dictation?.transcriber ?? "superwhisper"
    guard mode == "superwhisper" else {
        fputs("FAIL absent mode=\(mode)\n", stderr); exit(1)
    }
    print("PASS haas swift absent transcriber stays superwhisper")
} catch {
    fputs("FAIL absent threw \(error)\n", stderr); exit(1)
}

// 2. Decodes command timeout and vocabulary.
let fullJSON = """
{"wakeWords": ["test"], "command": "true", "dictation": {
  "transcriber": "haas",
  "haas": {"command": ["/bin/echo", "hi"], "timeoutSeconds": 45, "vocabulary": "/tmp/v.json"}
}}
"""
do {
    let cfg = try decodeConfig(fullJSON)
    guard cfg.dictation?.transcriber == "haas" else {
        fputs("FAIL transcriber not haas\n", stderr); exit(1)
    }
    guard cfg.dictation?.haas?.command == ["/bin/echo", "hi"] else {
        fputs("FAIL command mismatch\n", stderr); exit(1)
    }
    guard cfg.dictation?.haas?.timeoutSeconds == 45 else {
        fputs("FAIL timeout mismatch\n", stderr); exit(1)
    }
    guard cfg.dictation?.haas?.vocabulary == "/tmp/v.json" else {
        fputs("FAIL vocab mismatch\n", stderr); exit(1)
    }
    print("PASS haas swift decodes command timeout and vocabulary")
} catch {
    fputs("FAIL full threw \(error)\n", stderr); exit(1)
}

// 3. Rejects an unknown transcriber.
let badJSON = """
{"wakeWords": ["test"], "command": "true", "dictation": {"transcriber": "local"}}
"""
do {
    _ = try decodeConfig(badJSON)
    fputs("FAIL unknown transcriber accepted\n", stderr); exit(1)
} catch {
    let text = "\(error)"
    guard text.contains("dictation.transcriber") else {
        fputs("FAIL error missing marker: \(error)\n", stderr); exit(1)
    }
    print("PASS haas swift rejects an unknown transcriber")
}

// 4. Mock text.
if let text = HaasRunner.run(command: ["/bin/sh", "-c", "printf hello"], vocabulary: nil, wavPath: "/tmp/fake.wav", timeoutSeconds: 5), text == "hello" {
    print("PASS haas swift mock text")
} else {
    fputs("FAIL mock text\n", stderr); exit(1)
}

// 5. Mock non-zero does not return text.
if HaasRunner.run(command: ["/bin/sh", "-c", "exit 1"], vocabulary: nil, wavPath: "/tmp/fake.wav", timeoutSeconds: 5) == nil {
    print("PASS haas swift mock non-zero does not return text")
} else {
    fputs("FAIL mock non-zero returned text\n", stderr); exit(1)
}

// 6. Mock empty does not return text.
if HaasRunner.run(command: ["/bin/true"], vocabulary: nil, wavPath: "/tmp/fake.wav", timeoutSeconds: 5) == nil {
    print("PASS haas swift mock empty does not return text")
} else {
    fputs("FAIL mock empty returned text\n", stderr); exit(1)
}

// 7. Mock timeout.
let start = Date()
if HaasRunner.run(command: ["/bin/sleep", "5"], vocabulary: nil, wavPath: "/tmp/fake.wav", timeoutSeconds: 0.3) == nil {
    let elapsed = Date().timeIntervalSince(start)
    guard elapsed < 4 else {
        fputs("FAIL mock timeout too slow: \(elapsed)\n", stderr); exit(1)
    }
    print("PASS haas swift mock timeout")
} else {
    fputs("FAIL mock timeout returned text\n", stderr); exit(1)
}
