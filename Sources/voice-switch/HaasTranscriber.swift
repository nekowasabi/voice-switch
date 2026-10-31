// External Haas-ja-v1.0 runner: 16 kHz mono WAV path in, plain UTF-8 text on stdout, exit 0.
// Foundation only so Linux swiftc can compile it with Config.swift and the journal.
import Foundation

enum HaasRunner {
    static func arguments(command: [String], vocabulary: String?, wavPath: String) -> [String] {
        var argv = command
        if let v = vocabulary, !v.isEmpty {
            argv.append("--vocab")
            argv.append(v)
        }
        argv.append(wavPath)
        return argv
    }

    static func interpret(output: String) -> String? {
        let trimmed = output.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    static func run(command: [String], vocabulary: String?, wavPath: String, timeoutSeconds: Double) -> String? {
        guard !command.isEmpty else { return nil }
        let argv = arguments(command: command, vocabulary: vocabulary, wavPath: wavPath)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: argv[0])
        process.arguments = Array(argv.dropFirst())
        let outPipe = Pipe()
        let errPipe = Pipe()
        process.standardOutput = outPipe
        process.standardError = errPipe
        do {
            try process.run()
        } catch {
            return nil
        }
        let deadline = Date().addingTimeInterval(max(0.05, timeoutSeconds))
        while process.isRunning {
            if Date() > deadline {
                process.terminate()
                Thread.sleep(forTimeInterval: 0.2)
                if process.isRunning {
                    let killer = Process()
                    killer.executableURL = URL(fileURLWithPath: "/bin/kill")
                    killer.arguments = ["-9", "\(process.processIdentifier)"]
                    try? killer.run()
                    killer.waitUntilExit()
                }
                break
            }
            Thread.sleep(forTimeInterval: 0.05)
        }
        let data = outPipe.fileHandleForReading.readDataToEndOfFile()
        let text = String(data: data, encoding: .utf8) ?? ""
        guard process.terminationStatus == 0 else { return nil }
        return interpret(output: text)
    }
}
