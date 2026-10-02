// Platform hooks shared by macOS and Windows.
// Mac keeps open(1) / LaunchServices behavior; Windows uses cmd start for the same
// superwhisper:// deep links (supported by Superwhisper for Windows).
import Foundation

enum Platform {
    /// Default config `command` / `stopCommand` when examples are installed.
    static var defaultSuperwhisperToggle: String {
        #if os(Windows)
        #"cmd /c start "" superwhisper://record"#
        #else
        "open -g superwhisper://record"
        #endif
    }

    static var defaultRecordingsDir: String {
        #if os(Windows)
        #"%LOCALAPPDATA%\com.superwhisper.app\recordings"#
        #else
        "~/Documents/superwhisper/recordings"
        #endif
    }

    static var defaultConfigPath: String {
        if let env = ProcessInfo.processInfo.environment["VOICE_SWITCH_CONFIG"], !env.isEmpty {
            return expandPath(env)
        }
        #if os(Windows)
        let home = ProcessInfo.processInfo.environment["USERPROFILE"] ?? FileManager.default.homeDirectoryForCurrentUser.path
        return (home as NSString).appendingPathComponent(".config\\voice-switch\\config.json")
        #else
        return NSString(string: "~/.config/voice-switch/config.json").expandingTildeInPath
        #endif
    }

    static var logURL: URL {
        #if os(Windows)
        let base = ProcessInfo.processInfo.environment["LOCALAPPDATA"]
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("AppData/Local").path
        return URL(fileURLWithPath: base).appendingPathComponent("voice-switch/voice-switch.log")
        #else
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/voice-switch.log")
        #endif
    }

    static func expandPath(_ path: String) -> String {
        #if os(Windows)
        var out = path
        for key in ["LOCALAPPDATA", "APPDATA", "USERPROFILE", "HOME", "TEMP"] {
            if let val = ProcessInfo.processInfo.environment[key] {
                out = out.replacingOccurrences(of: "%\(key)%", with: val, options: .caseInsensitive)
            }
        }
        if out.hasPrefix("~") {
            let home = ProcessInfo.processInfo.environment["USERPROFILE"]
                ?? FileManager.default.homeDirectoryForCurrentUser.path
            out = home + out.dropFirst()
        }
        return out
        #else
        return NSString(string: path).expandingTildeInPath
        #endif
    }

    /// Runs the configured action through a shell so users can put any command line in config.
    static func runCommand(_ command: String) {
        let p = Process()
        #if os(Windows)
        p.executableURL = URL(fileURLWithPath: ProcessInfo.processInfo.environment["ComSpec"] ?? #"C:\Windows\System32\cmd.exe"#)
        p.arguments = ["/c", command]
        #else
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", command]
        #endif
        p.terminationHandler = { if $0.terminationStatus != 0 { log("command exited \($0.terminationStatus): \(command)") } }
        do { try p.run() } catch { log("command failed to start: \(error)") }
    }

    /// Open a file with the default handler (config / log).
    static func openFile(_ path: String) {
        #if os(Windows)
        runCommand("start \"\" \"\(path)\"")
        #else
        // NSWorkspace used from AppDelegate on macOS; this fallback covers non-UI paths.
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = [path]
        try? p.run()
        #endif
    }
}
