// Windows helpers. Entry is in main.swift (#if os(Windows)).
#if os(Windows)
import Foundation

enum WindowsApp {
    static func printHelp(configPath: String) {
        print("""
        voice-switch (Windows)

          voice-switch                 legacy Swift stub; use dotnet/VoiceSwitch.Windows for the functional runtime
          voice-switch --fire          run config `command` once (tests superwhisper:// hook)
          voice-switch --vad-selftest  pure-Swift VAD/segmenter self-test (no mic/STT)
          voice-switch --help          this text

        Config: \(configPath)
        Default command: \(Platform.defaultSuperwhisperToggle)
        Windows runtime build: PC=wsl make win-build
        """)
    }

    static func ensureLogDir(logURL: URL) {
        let dir = logURL.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    static func run(args: [String]) {
        setvbuf(stdout, nil, _IOLBF, 0)
        var args = args
        let mode = args.first.flatMap { ["--vad-selftest", "--fire", "--help"].contains($0) ? $0 : nil }
        if mode != nil { args.removeFirst() }
        let configPath = Platform.defaultConfigPath
        let logURL = Platform.logURL

        if mode == "--help" {
            printHelp(configPath: configPath)
            exit(0)
        } else if mode == "--vad-selftest" {
            vadSelftest()
            exit(0)
        } else if mode == "--fire" {
            do {
                let cfg = try ConfigFile(path: configPath).cfg
                log("firing command: \(cfg.command)")
                Platform.runCommand(cfg.command)
                Thread.sleep(forTimeInterval: 1)
                exit(0)
            } catch {
                log("fatal: \(error)")
                log("hint: copy config.example.windows.json to \(configPath)")
                exit(1)
            }
        } else {
            ensureLogDir(logURL: logURL)
            if isatty(STDOUT_FILENO) == 0 {
                if !FileManager.default.fileExists(atPath: logURL.path) {
                    FileManager.default.createFile(atPath: logURL.path, contents: nil)
                }
                freopen(logURL.path, "a", stdout)
                setvbuf(stdout, nil, _IOLBF, 0)
            }
            do {
                let config = try ConfigFile(path: configPath)
                log("voice-switch Windows: config \(configPath)")
                log("wake words: \(config.cfg.wakeWords)")
                log("command: \(config.cfg.command)")
                log("Swift Windows entry is a legacy stub; use dotnet/VoiceSwitch.Windows for microphone/STT runtime")
                while true {
                    config.reloadIfChanged()
                    Thread.sleep(forTimeInterval: 2)
                }
            } catch {
                log("fatal: \(error)")
                log("hint: copy config.example.windows.json to \(configPath)")
                exit(1)
            }
        }
    }
}
#endif
