// Platform hooks (macOS only; the Windows runtime lives in dotnet/).
import Foundation

enum Platform {
    /// Default config `command` / `stopCommand` when examples are installed.
    static var defaultSuperwhisperToggle: String {
        "open -g superwhisper://record"
    }

    static var defaultRecordingsDir: String {
        "~/Documents/superwhisper/recordings"
    }

    static var defaultConfigPath: String {
        if let env = ProcessInfo.processInfo.environment["VOICE_SWITCH_CONFIG"], !env.isEmpty {
            return expandPath(env)
        }
        return NSString(string: "~/.config/voice-switch/config.json").expandingTildeInPath
    }

    static var logURL: URL {
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/voice-switch.log")
    }

    static func expandPath(_ path: String) -> String {
        return NSString(string: path).expandingTildeInPath
    }

    /// Runs the configured action through a shell so users can put any command line in config.
    /// With `stdin`, the text is written as given (no newline added) and then closed, because a reader such as
    /// `keypipe --line` waits for EOF; the command's stdout is discarded and its stderr reaches the log.
    /// `environment` is added over the app's own environment.
    static func runCommand(_ command: String, stdin: String? = nil, environment: [String: String] = [:]) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", command]
        p.terminationHandler = { if $0.terminationStatus != 0 { log("command exited \($0.terminationStatus): \(command)") } }
        if !environment.isEmpty {
            p.environment = ProcessInfo.processInfo.environment.merging(environment) { $1 }
        }
        guard let stdin else {
            do { try p.run() } catch { log("command failed to start: \(error)") }
            return
        }
        let pipe = Pipe()
        p.standardInput = pipe
        p.standardOutput = FileHandle.nullDevice
        do { try p.run() } catch { log("command failed to start: \(error)"); return }
        do {
            try pipe.fileHandleForWriting.write(contentsOf: Data(stdin.utf8))
            try pipe.fileHandleForWriting.close()
        } catch { log("command stdin write failed: \(error): \(command)") }
    }

    /// macOS wake/stop dispatch. A URL override never falls back to a shell on failure.
    @discardableResult
    static func runMacAction(_ cfg: Config, stop: Bool = false, wait: Bool = false) -> Bool {
        if let url = stop ? cfg.macOS?.stopURL : cfg.macOS?.wakeURL {
            log("URL action: \(stop ? "stop" : "wake")")
            return runURLProcess(urlProcess(url), wait: wait)
        }
        runCommand(stop ? (cfg.stopCommand ?? defaultSuperwhisperToggle) : cfg.command)
        return true
    }

    static func urlProcess(_ url: ActionURL) -> Process {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        process.arguments = url.openArguments
        return process
    }

    @discardableResult
    static func runURLProcess(_ process: Process, wait: Bool) -> Bool {
        func report(_ process: Process) {
            if process.terminationStatus != 0 {
                log("URL action failed (open exit \(process.terminationStatus)); check the URL setting and install/register its target app. See open's error above.")
            }
        }
        // Inherit stderr so LaunchServices diagnostics reach the terminal or app log.
        if !wait { process.terminationHandler = { report($0) } }
        do { try process.run() }
        catch { log("URL action could not start /usr/bin/open: \(error)"); return false }
        if wait {
            process.waitUntilExit()
            report(process)
            return process.terminationStatus == 0
        }
        return true
    }
}


/// Wall-clock seconds for one STT call: max(10, audioSeconds + 20).
/// Same formula as Windows `TranscribeDeadline.Seconds` / RESEARCH hyp 2.
func transcribeDeadlineSeconds(audioSeconds: Double) -> TimeInterval {
    max(10, audioSeconds + 20)
}
