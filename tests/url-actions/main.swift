import Foundation

var checks = 0
func expect(_ condition: @autoclosure () -> Bool, _ message: String) {
    checks += 1
    if !condition() { fatalError(message) }
}
func config(_ mac: [String: Any]? = nil) throws -> Config {
    var value: [String: Any] = ["wakeWords": ["start"], "command": "legacy", "stopCommand": "legacy-stop"]
    if let mac { value["macOS"] = mac }
    return try JSONDecoder().decode(Config.self, from: JSONSerialization.data(withJSONObject: value))
}
let plain = try config()
expect(plain.macOS == nil && plain.command == "legacy", "legacy config must keep working")
let nulls = try config(["wakeURL": NSNull(), "stopURL": NSNull()])
expect(nulls.macOS?.wakeURL == nil && nulls.macOS?.stopURL == nil, "null URLs use legacy commands")
for value in [42, true, ["url": "app://run"]] as [Any] {
    do { _ = try config(["wakeURL": value]); fatalError("non-string URL accepted") }
    catch DecodingError.typeMismatch { checks += 1 }
}
let raw = "example+app://run?text=a%20b&value=$(touch%20/tmp/never);other='quoted'#fragment"
let cfg = try config(["wakeURL": raw, "stopURL": "superwhisper://record"])
expect(cfg.macOS?.wakeURL?.rawValue == raw, "URL bytes must be preserved")
expect(cfg.macOS?.wakeURL?.openArguments == ["-g", "--", raw], "URL must be exactly one argument after --")
expect(cfg.macOS?.stopURL?.rawValue == "superwhisper://record", "stop URL must decode")
let empty = try config([:])
expect(empty.macOS?.wakeURL == nil, "empty macOS object uses legacy commands")
for invalid in ["", " ", "-aCalculator", "/tmp/a", "relative", "1bad://x", "app:", "app://", "app://a b", "app://a\nb", "app://a\u{0}b", "app://a%", "app://a%ZZ", "file:///tmp/a", "javascript:alert(1)", "data:text/plain,hi"] {
    for field in ["wakeURL", "stopURL"] {
        do { _ = try config([field: invalid]); fatalError("accepted invalid \(field)") }
        catch DecodingError.dataCorrupted(let context) {
            expect(context.codingPath.last?.stringValue == field, "error must identify setting")
        }
    }
}
for valid in ["superwhisper://record", "shortcuts://run-shortcut?name=My%20Shortcut", "https://example.com/a?x=1&y=2", "app:opaque", "app://日本語", "app://a%0A"] {
    let parsed = try config(["wakeURL": valid])
    expect(parsed.macOS?.wakeURL?.rawValue == valid, "valid URL rejected")
}
let process = Platform.urlProcess(cfg.macOS!.wakeURL!)
expect(process.executableURL?.path == "/usr/bin/open", "must use absolute open executable")
expect(process.arguments == ["-g", "--", raw], "must not use a shell")
// Exercise launch failures and exit status without opening any application.
let missing = Process()
missing.executableURL = URL(fileURLWithPath: "/nonexistent/voice-switch-url-test")
expect(!Platform.runURLProcess(missing, wait: true), "failed spawn must fail")
#if !os(Windows)
let failed = Process()
failed.executableURL = URL(fileURLWithPath: "/usr/bin/false")
expect(!Platform.runURLProcess(failed, wait: true), "nonzero exit must fail")
let success = Process()
success.executableURL = URL(fileURLWithPath: "/usr/bin/true")
expect(Platform.runURLProcess(success, wait: true), "zero exit must succeed")
#endif
let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
defer { try? FileManager.default.removeItem(at: directory) }
let file = directory.appendingPathComponent("config.json")
try Data(#"{"wakeWords":["start"],"command":"legacy","macOS":{"wakeURL":"app://valid"}}"#.utf8).write(to: file)
let loaded = try ConfigFile(path: file.path)
try Data(#"{"wakeWords":["changed"],"command":"bad","macOS":{"wakeURL":"not a URL"}}"#.utf8).write(to: file)
try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSinceNow: 5)], ofItemAtPath: file.path)
loaded.reloadIfChanged()
expect(loaded.cfg.command == "legacy" && loaded.cfg.macOS?.wakeURL?.rawValue == "app://valid", "invalid reload must keep previous config")
try Data(#"{"wakeWords":["changed"],"command":"updated","macOS":{"wakeURL":"app://fixed"}}"#.utf8).write(to: file)
try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSinceNow: 10)], ofItemAtPath: file.path)
loaded.reloadIfChanged()
expect(loaded.cfg.command == "updated" && loaded.cfg.macOS?.wakeURL?.rawValue == "app://fixed", "corrected reload must take effect")
print("URL actions: \(checks) checks passed")
