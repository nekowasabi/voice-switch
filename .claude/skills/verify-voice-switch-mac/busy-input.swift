// Keeps macOS "input idle" near zero by posting a mouse move to the cursor's current position.
// Nothing is typed and the cursor does not move, so the frontmost app is untouched by the helper itself.
// It also samples the frontmost app on every tick: any change is a focus steal the run must explain.
// usage: swift busy-input.swift <seconds> [intervalMs]
import AppKit
import CoreGraphics

let args = CommandLine.arguments
let seconds = Double(args.count > 1 ? args[1] : "10") ?? 10
let interval = (Double(args.count > 2 ? args[2] : "200") ?? 200) / 1000
let end = Date().addingTimeInterval(seconds)
var maxIdle = 0.0
var fronts: [String] = []
func front() -> String { NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "unknown" }
while Date() < end {
    let here = CGEvent(source: nil)?.location ?? .zero
    CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: here, mouseButton: .left)?.post(tap: .cghidEventTap)
    // frontmostApplication only refreshes while the run loop turns.
    RunLoop.current.run(until: Date().addingTimeInterval(interval))
    maxIdle = max(maxIdle, CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: .mouseMoved))
    let f = front()
    if fronts.last != f { fronts.append(f); print("busy-input: frontmost \(f) at \(ISO8601DateFormatter().string(from: Date()))") }
}
print(String(format: "busy-input: %.1f s, max mouseMoved idle %.3f s, frontmost changes %d", seconds, maxIdle, fronts.count - 1))
