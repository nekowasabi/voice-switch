#if os(macOS)
import AppKit
import Carbon

final class HistoryCopyButton: NSButton {
    var entryID = UUID()
}

final class HistoryPanel: NSObject {
    static let shared = HistoryPanel()
    private var panel: NSPanel?

    @objc func show() {
        let window = panel ?? NSPanel(contentRect: NSRect(x: 0, y: 0, width: 620, height: 380),
                                     styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        panel = window
        window.title = "voice-switch 履歴（30分・最大50件）"
        window.isReleasedWhenClosed = false
        let stack = NSStackView()
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 8
        stack.edgeInsets = NSEdgeInsets(top: 12, left: 12, bottom: 12, right: 12)
        let clear = NSButton(title: "すべて消去", target: self, action: #selector(clearAll))
        stack.addArrangedSubview(clear)
        let entries = DictationHistory.shared.snapshot()
        if entries.isEmpty { stack.addArrangedSubview(NSTextField(labelWithString: "履歴はありません")) }
        for entry in entries {
            let preview = String(entry.text.replacingOccurrences(of: "\n", with: " ").prefix(100))
            let button = HistoryCopyButton(title: "コピー [\(entry.origin.rule.rawValue)] \(preview)",
                                           target: self, action: #selector(copyEntry(_:)))
            button.entryID = entry.id
            stack.addArrangedSubview(button)
        }
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.documentView = stack
        stack.translatesAutoresizingMaskIntoConstraints = false
        stack.widthAnchor.constraint(equalTo: scroll.contentView.widthAnchor).isActive = true
        window.contentView = scroll
        window.center()
        window.makeKeyAndOrderFront(nil)
    }

    @objc private func clearAll() { DictationHistory.shared.clear(); show() }
    @objc private func copyEntry(_ button: HistoryCopyButton) {
        guard let text = DictationHistory.shared.select(button.entryID) else { show(); return }
        NSPasteboard.general.clearContents()
        let copied = NSPasteboard.general.setString(text, forType: .string)
        panel?.title = copied ? "コピーしました。貼り付けは行いません" : "コピーできませんでした"
    }
}

enum MacInputActivity {
    static func idleSeconds() -> Double? {
        let events: [CGEventType] = [.keyDown, .flagsChanged, .leftMouseDown, .rightMouseDown, .otherMouseDown, .mouseMoved, .leftMouseDragged, .rightMouseDragged, .scrollWheel]
        let seconds = events.map { CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: $0) }.min() ?? -1
        return seconds.isFinite && seconds >= 0 ? seconds : nil
    }
}

enum MacWakeShortcuts {
    private static var handler: EventHandlerRef?
    private static var keys: [EventHotKeyRef] = []
    private static let signature: OSType = 0x5653574B

    static func install() {
        guard handler == nil else { return }
        var event = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let status = InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            guard let event else { return OSStatus(eventNotHandledErr) }
            var id = EventHotKeyID()
            let result = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                           nil, MemoryLayout<EventHotKeyID>.size, nil, &id)
            guard result == noErr, id.signature == signature else { return OSStatus(eventNotHandledErr) }
            if id.id == 1 { HistoryPanel.shared.show() }
            return noErr
        }, 1, &event, nil, &handler)
        guard status == noErr else { log("history hotkey handler unavailable status=\(status)"); return }
        var key: EventHotKeyRef?
        let registered = RegisterEventHotKey(UInt32(kVK_F9), UInt32(controlKey | shiftKey),
                                             EventHotKeyID(signature: signature, id: 1), GetApplicationEventTarget(), 0, &key)
        if registered == noErr, let key { keys.append(key) }
        else { log("history hotkey unavailable status=\(registered)") }
    }
}
#endif
