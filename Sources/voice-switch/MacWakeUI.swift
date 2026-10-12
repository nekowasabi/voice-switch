#if os(macOS)
import AppKit
import Carbon
import Darwin

final class HistoryCopyButton: NSButton {
    var entryID = UUID()
}

final class HistoryPanel: NSObject {
    static let shared = HistoryPanel()
    private var panel: NSPanel?
    private var refresh: Timer?
    private var renderedIDs: [UUID]?

    override init() {
        super.init()
        refresh = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            guard let self, let panel = self.panel, panel.isVisible else { return }
            self.render(panel)
        }
    }

    @objc func show() {
        let window = panel ?? NSPanel(contentRect: NSRect(x: 0, y: 0, width: 620, height: 380),
                                     styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        panel = window
        window.title = "voice-switch 履歴（30分・最大50件）"
        window.isReleasedWhenClosed = false
        render(window)
        window.center()
        window.makeKeyAndOrderFront(nil)
    }

    private func render(_ window: NSPanel) {
        let entries = DictationHistory.shared.snapshot()
        let ids = entries.map(\.id)
        guard renderedIDs != ids else { return }
        renderedIDs = ids
        let stack = NSStackView()
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 8
        stack.edgeInsets = NSEdgeInsets(top: 12, left: 12, bottom: 12, right: 12)
        let clear = NSButton(title: "すべて消去", target: self, action: #selector(clearAll))
        stack.addArrangedSubview(clear)
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
        stack.frame = NSRect(x: 0, y: 0, width: 600, height: max(340, stack.fittingSize.height))
        stack.autoresizingMask = [.width]
        window.contentView = scroll
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
            if id.id == 2 { Hotkeys.requestStart() }
            if id.id == 3 { MacWakeShortcuts.copyNewestHistory() }
            return noErr
        }, 1, &event, nil, &handler)
        guard status == noErr else { log("history hotkey handler unavailable status=\(status)"); return }
        for (code, id) in [(kVK_F9, UInt32(1)), (kVK_F10, UInt32(2)), (kVK_F11, UInt32(3))] {
            var key: EventHotKeyRef?
            let registered = RegisterEventHotKey(UInt32(code), UInt32(controlKey | shiftKey),
                EventHotKeyID(signature: signature, id: id), GetApplicationEventTarget(), 0, &key)
            if registered == noErr, let key { keys.append(key) }
            else { log("voice-switch hotkey unavailable id=\(id) status=\(registered)") }
        }
    }

    // Copies only the newest retained entry. Pasting stays a manual step, as in the HUD.
    static func copyNewestHistory() {
        guard let text = DictationHistory.shared.snapshot().first?.text else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }
}
func macReading(_ text: String) -> String? {
    let normalized = NoWakePolicy.normalizedReading(text)
    func kanaOnly(_ value: String) -> Bool {
        !value.isEmpty && value.unicodeScalars.allSatisfy { (0x3041...0x3096).contains($0.value) || $0.value == 0x30FC }
    }
    if kanaOnly(normalized) { return normalized }
    guard let tokenizer = CFStringTokenizerCreate(nil, text as CFString, CFRange(location: 0, length: text.utf16.count),
        kCFStringTokenizerUnitWord, CFLocaleCreate(nil, "ja_JP" as CFString)) else { return nil }
    var result = ""
    while true {
        _ = CFStringTokenizerAdvanceToNextToken(tokenizer)
        if CFStringTokenizerGetCurrentTokenRange(tokenizer).location == kCFNotFound { break }
        guard let latin = CFStringTokenizerCopyCurrentTokenAttribute(tokenizer, kCFStringTokenizerAttributeLatinTranscription) as? String,
              let reading = latin.applyingTransform(.latinToHiragana, reverse: false) else { return nil }
        result += NoWakePolicy.normalizedReading(reading)
    }
    return kanaOnly(result) ? result : nil
}

func macRelaxedWake(_ text: String, wakeWords: [String]) -> Bool {
    guard let heard = macReading(text) else { return false }
    return NoWakePolicy.relaxedWake(heard, readings: wakeWords.compactMap(macReading))
}
#endif

#if os(macOS)
final class MacModeOwnership {
    private let descriptor: Int32
    init(_ descriptor: Int32) { self.descriptor = descriptor }
    deinit { _ = Darwin.close(descriptor) }
}

enum MacModeRestoreStore {
    private static var directory: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/voice-switch", isDirectory: true)
    }
    private static var file: URL { directory.appendingPathComponent("mode-restore.json") }
    static func acquire() throws -> MacModeOwnership {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        guard directory.resolvingSymlinksInPath().standardizedFileURL == directory.standardizedFileURL else {
            throw CocoaError(.fileWriteNoPermission)
        }
        let path = directory.appendingPathComponent("mode-owner.lock").path
        let descriptor = Darwin.open(path, O_CREAT | O_RDWR | O_NOFOLLOW, mode_t(0o600))
        guard descriptor >= 0 else { throw CocoaError(.fileWriteNoPermission) }
        guard Darwin.flock(descriptor, LOCK_EX | LOCK_NB) == 0 else {
            _ = Darwin.close(descriptor)
            throw CocoaError(.fileLocking)
        }
        return MacModeOwnership(descriptor)
    }
    static func read() throws -> ModeRestore? {
        guard FileManager.default.fileExists(atPath: file.path) else { return nil }
        guard file.resolvingSymlinksInPath().standardizedFileURL == file.standardizedFileURL,
              let size = try file.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= 8192 else {
            throw CocoaError(.fileReadNoPermission)
        }
        return try JSONDecoder().decode(ModeRestore.self, from: Data(contentsOf: file))
    }
    static func write(_ state: ModeRestore) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        guard directory.resolvingSymlinksInPath().standardizedFileURL == directory.standardizedFileURL else {
            throw CocoaError(.fileWriteNoPermission)
        }
        try JSONEncoder().encode(state).write(to: file, options: .atomic)
    }
    static func clear() throws { try FileManager.default.removeItem(at: file) }
}
#endif
