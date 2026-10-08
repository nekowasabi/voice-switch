import Foundation

// MARK: macrowhisper CLI

/// Thin wrapper around the public macrowhisper CLI
/// (https://github.com/ognistik/macrowhisper — same commands as the Alfred workflow).
enum Macrowhisper {
    /// Resolve `bin` to an absolute path when possible (`which`), else return as-is.
    static func resolveBin(_ bin: String) -> String {
        if bin.contains("/") { return bin }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/which")
        p.arguments = [bin]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = Pipe()
        do {
            try p.run()
            p.waitUntilExit()
            if p.terminationStatus == 0,
               let s = String(data: out.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)?
                .trimmingCharacters(in: .whitespacesAndNewlines), !s.isEmpty {
                return s
            }
        } catch {}
        return bin
    }

    @discardableResult
    static func run(bin: String, arguments: [String]) -> Int32 {
        let path = resolveBin(bin)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = arguments
        p.standardOutput = Pipe()
        p.standardError = Pipe()
        do {
            try p.run()
            p.waitUntilExit()
            if p.terminationStatus != 0 {
                log("macrowhisper exited \(p.terminationStatus): \(path) \(arguments.joined(separator: " "))")
            }
            return p.terminationStatus
        } catch {
            log("macrowhisper failed to start (\(path)): \(error)")
            return -1
        }
    }

    /// Prepare macrowhisper for the Superwhisper session that is about to start.
    /// Priority matches documented mutual exclusion: scheduleAction > autoReturn > activeAction.
    static func prepare(_ cfg: MacrowhisperConfig?) {
        guard let cfg else { return }
        let bin = cfg.bin ?? "macrowhisper"
        if let name = cfg.scheduleAction?.trimmingCharacters(in: .whitespacesAndNewlines), !name.isEmpty {
            if cfg.autoReturn == true {
                log("macrowhisper: scheduleAction set; ignoring autoReturn (CLI treats them as mutually exclusive)")
            }
            _ = run(bin: bin, arguments: ["--schedule-action", name])
            log("macrowhisper: scheduled action \(name)")
        } else if cfg.autoReturn == true {
            _ = run(bin: bin, arguments: ["--auto-return", "true"])
            log("macrowhisper: auto-return armed")
        } else if let name = cfg.activeAction?.trimmingCharacters(in: .whitespacesAndNewlines), !name.isEmpty {
            _ = run(bin: bin, arguments: ["--action", name])
            log("macrowhisper: active action \(name)")
        }
        if let key = cfg.modeKey?.trimmingCharacters(in: .whitespacesAndNewlines), !key.isEmpty {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
            p.arguments = ["-g", "superwhisper://mode?key=\(key)"]
            try? p.run()
        }
    }

    static func shouldPrepareHandoff(_ cfg: MacrowhisperConfig?) -> Bool {
        guard let cfg else { return false }
        let hasHook = (cfg.scheduleAction?.isEmpty == false)
            || cfg.autoReturn == true
            || (cfg.activeAction?.isEmpty == false)
        guard hasHook else { return false }
        return cfg.onDictationHandoff ?? true
    }
}
