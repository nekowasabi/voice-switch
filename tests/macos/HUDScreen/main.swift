import AppKit
import Darwin

let logURL = Platform.logURL

struct ProofFailure: Error, CustomStringConvertible {
    let description: String
}

func require(_ condition: Bool, _ message: String) throws {
    if !condition { throw ProofFailure(description: message) }
}

setvbuf(stdout, nil, _IOLBF, 0)
if let marker = CommandLine.arguments.firstIndex(of: "--focus-window") {
    let app = NSApplication.shared
    let screen = NSScreen.screens[Int(CommandLine.arguments[marker + 1])!]
    let area = screen.visibleFrame
    app.setActivationPolicy(.regular)
    let frame = NSRect(x: area.midX - 200, y: area.midY - 100, width: 400, height: 200)
    let window = NSWindow(contentRect: frame, styleMask: [.titled, .closable], backing: .buffered, defer: false)
    window.title = "voice-switch HUD 表示先検証"
    window.isReleasedWhenClosed = false
    window.setFrame(frame, display: true)
    DispatchQueue.main.async {
        app.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            print(window.isKeyWindow && window.screen == screen ? "READY" : "FAILED key=\(window.isKeyWindow) screen=\(String(describing: window.screen?.frame)) expected=\(screen.frame)")
        }
    }
    app.run()
    exit(0)
}
guard CommandLine.arguments.contains("--live") else {
    print("実画面の検証には --live を指定してください")
    exit(0)
}

let app = NSApplication.shared
let originalApp = NSWorkspace.shared.frontmostApplication
let screens = NSScreen.screens
let pointer = NSEvent.mouseLocation
guard screens.count >= 2,
      let pointerIndex = screens.firstIndex(where: { $0.frame.contains(pointer) }),
      let otherIndex = screens.indices.first(where: { $0 != pointerIndex }) else {
    fputs("FAIL: 実画面の検証には複数のディスプレイが必要です\n", stderr)
    exit(1)
}

app.setActivationPolicy(.accessory)
app.finishLaunching()
let hud = HUD()

func pump(until deadline: Date) {
    while Date() < deadline {
        if let event = app.nextEvent(matching: .any, until: min(deadline, Date().addingTimeInterval(0.05)),
                                     inMode: .default, dequeue: true) {
            app.sendEvent(event)
        }
    }
}

func checkScreen(_ index: Int) throws {
    let target = screens[index]
    let focusApp = Process()
    focusApp.executableURL = URL(fileURLWithPath: CommandLine.arguments[0])
    focusApp.arguments = ["--focus-window", String(index)]
    let ready = Pipe()
    focusApp.standardOutput = ready
    let fd = ready.fileHandleForReading.fileDescriptor
    _ = fcntl(fd, F_SETFL, O_NONBLOCK)
    try focusApp.run()
    defer {
        if focusApp.isRunning { focusApp.terminate() }
        let deadline = Date().addingTimeInterval(2)
        while focusApp.isRunning && Date() < deadline { pump(until: Date().addingTimeInterval(0.05)) }
        if focusApp.isRunning { kill(focusApp.processIdentifier, SIGKILL) }
        try? ready.fileHandleForReading.close()
    }
    var response = ""
    let deadline = Date().addingTimeInterval(5)
    while !response.contains("\n") && Date() < deadline && focusApp.isRunning {
        var bytes = [UInt8](repeating: 0, count: 64)
        let count = Darwin.read(fd, &bytes, bytes.count)
        if count > 0 { response += String(decoding: bytes.prefix(count), as: UTF8.self) }
        pump(until: Date().addingTimeInterval(0.05))
    }
    try require(response == "READY\n", "別プロセスの検証ウィンドウが指定画面でフォーカスを取得できませんでした: \(response)")
    try require(NSWorkspace.shared.frontmostApplication?.processIdentifier == focusApp.processIdentifier,
                "検証アプリが最前面ではありません")
    try require(screens[pointerIndex].frame.contains(NSEvent.mouseLocation), "検証中にマウスポインターが別画面へ移動しました")
    let area = target.visibleFrame
    let expected = NSRect(x: area.midX - 80, y: area.maxY - 52, width: 160, height: 40)
    for phase in [Listener.Phase.waiting, .recording, .ended] {
        hud.show(phase)
        guard let panel = app.windows.first(where: { $0 is NSPanel && $0.isVisible }) else {
            throw ProofFailure(description: "HUD が表示されませんでした")
        }
        print("focused=\(target.frame) pointer=\(pointer) phase=\(phase) HUD=\(panel.frame) expected=\(expected)")
        try require(panel.frame == expected, "HUD がフォーカスのある画面に表示されません")
        try require(NSWorkspace.shared.frontmostApplication?.processIdentifier == focusApp.processIdentifier,
                    "HUD がウィンドウのフォーカスを奪いました")
    }
    pump(until: Date().addingTimeInterval(1.7))
    try require(!app.windows.contains(where: { $0 is NSPanel && $0.isVisible }), "終了後の HUD が非表示になりません")
}

var result: Int32 = 0
do {
    try checkScreen(otherIndex)
    try checkScreen(pointerIndex)
    print("PASS: 実 HUD は別プロセスのフォーカスに従って画面を切り替え、フォーカスを維持し、終了後に非表示になります")
} catch {
    fputs("FAIL: \(error)\n", stderr)
    result = 1
}
hud.show(.idle)
originalApp?.activate(options: [])
pump(until: Date().addingTimeInterval(0.2))
exit(result)
