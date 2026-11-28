#if os(macOS)
import AppKit
import VoiceSwitchCore

private extension TriageKind {
    var title: String {
        switch self {
        case .selfTask: return "自分の作業"
        case .lookUp: return "調べもの"
        case .writeText: return "下書き"
        case .computerChange: return "PC への変更"
        case .delegate: return "作業依頼"
        }
    }
}

@MainActor
final class TriagePanel {
    private let service: TriageService
    private var panel: NSPanel?
    private var countdowns: [(NSTextField, Date)] = []
    private var countdownTimer: Timer?
    private var lastDay = Calendar.current.startOfDay(for: Date())
    private var showsSent = false
    // 一番上の「確認が要るもの」の番号付きボタンを数字キーで押す
    private var digitAction: ((Int) -> Void)?

    init(service: TriageService) { self.service = service }

    func show() {
        let window = panel ?? TriageWindow(contentRect: NSRect(x: 0, y: 0, width: 640, height: 460),
                                           styleMask: [.titled, .closable], backing: .buffered, defer: false)
        (window as? TriageWindow)?.onDigit = { [weak self] digit in self?.digitAction?(digit) }
        panel = window
        window.title = "voice-switch 依頼"
        window.isReleasedWhenClosed = false
        render(window)
        window.center()
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        if countdownTimer == nil {
            countdownTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] timer in
                MainActor.assumeIsolated {
                    guard let self, let panel = self.panel, panel.isVisible else {
                        timer.invalidate()
                        self?.countdownTimer = nil
                        return
                    }
                    let day = Calendar.current.startOfDay(for: Date())
                    if self.lastDay != day {
                        self.lastDay = day
                        self.render(panel)
                    }
                    self.tick()
                }
            }
        }
    }

    func refresh() {
        if let panel, panel.isVisible { render(panel) }
    }

    private func render(_ window: NSPanel) {
        let previousOffset = (window.contentView as? NSScrollView)?.contentView.bounds.origin ?? .zero
        countdowns.removeAll(keepingCapacity: true)
        let stack = NSStackView(views: [])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 10
        stack.edgeInsets = NSEdgeInsets(top: 12, left: 12, bottom: 12, right: 12)
        stack.addArrangedSubview(button(service.paused ? "自動送信を再開" : "自動送信を一時停止") { [weak service] in
            service?.togglePause()
        })
        if service.storeError { stack.addArrangedSubview(label("依頼ファイルを読み書きできません。", secondary: true)) }

        let review = service.review
        let choices = sendChoices()
        digitAction = nil
        section("確認が要るもの", count: review.count, in: stack)
        for proposal in review {
            stack.addArrangedSubview(label(proposal.text))
            let stage = service.stage(of: proposal.id)
            if stage == .unknown || stage == .failed {
                let reason = proposal.failureReason.map { "送信に失敗（\($0)）" } ?? "送信に失敗"
                stack.addArrangedSubview(label(stage == .unknown ? "送ったか不明" : reason, secondary: true))
                var buttons = [
                    button("やり直す", enabled: service.canRetry(proposal)) { [weak service] in service?.retry(proposal.id) },
                    button("送らない") { [weak service] in service?.dismiss(proposal.id) },
                ]
                if stage == .unknown {
                    buttons.insert(button("送ったことにする") { [weak service] in service?.confirmDone(proposal.id) }, at: 0)
                }
                stack.addArrangedSubview(NSStackView(views: buttons))
                continue
            }
            let reasons = proposal.stage == .pending ? ["送り先の設定が変わりました"] : proposal.reasons
            stack.addArrangedSubview(label((reasons + [service.routeName(proposal)].compactMap { $0 }).joined(separator: " · "),
                                           secondary: true))
            let numbered = digitAction == nil && !service.storeError
            var keys: [Int: () -> Void] = [:]
            var groups: [[NSView]] = []
            for (offset, choice) in choices.enumerated() {
                let digit = offset + 1
                let send: () -> Void = { [weak service] in service?.send(proposal.id, as: choice.kind, routeIndex: choice.route) }
                let view = button(numbered && digit <= 9 ? "\(digit) \(choice.kind.title)" : choice.kind.title, action: send)
                if let icon = choice.icon {
                    view.image = icon
                    view.imagePosition = .imageRight
                }
                view.toolTip = choice.name
                if offset > 0, choices[offset - 1].kind == choice.kind { groups[groups.count - 1].append(view) } else { groups.append([view]) }
                if digit <= 9 { keys[digit] = send }
            }
            let dismiss: () -> Void = { [weak service] in service?.dismiss(proposal.id) }
            groups.append([button(numbered ? "0 送らない" : "送らない", action: dismiss)])
            keys[0] = dismiss
            if numbered { digitAction = { keys[$0]?() } }
            // 同じ種類のボタンは行をまたがせない（1 行 3 つまで、種類ごとまとめて折り返す）
            var rows: [[NSView]] = [[]]
            for group in groups {
                if !rows[rows.count - 1].isEmpty, rows[rows.count - 1].count + group.count > 3 { rows.append([]) }
                rows[rows.count - 1] += group
            }
            for row in rows { stack.addArrangedSubview(NSStackView(views: row)) }
        }

        let pending = service.pending
        section("まもなく送る", count: pending.count, in: stack)
        for proposal in pending {
            stack.addArrangedSubview(label(proposal.text))
            let countdown = label("", secondary: true)
            if let deadline = proposal.deadline { countdowns.append((countdown, deadline)) }
            if let name = service.routeName(proposal) { stack.addArrangedSubview(label(name, secondary: true)) }
            var controls: [NSView] = [
                countdown,
                button("取消") { [weak service] in service?.dismiss(proposal.id) },
                button("すぐ送る") { [weak service] in service?.send(proposal.id) },
            ]
            if let kind = proposal.kind {
                let alternatives = service.routeNames(kind).enumerated().filter { $0.offset != (proposal.routeIndex ?? 0) }
                if !alternatives.isEmpty {
                    controls.append(menu("送り先を変更", options: alternatives.map(\.element)) { [weak service] index in
                        service?.switchDestination(proposal.id, to: alternatives[index].offset)
                    })
                }
            }
            stack.addArrangedSubview(NSStackView(views: controls))
        }

        let today = service.today
        let sentToggle = button("送った（今日 \(today.count)）\(showsSent ? " ▾" : " ▸")") { [weak self] in
            guard let self else { return }
            self.showsSent.toggle()
            self.refresh()
        }
        sentToggle.isEnabled = true
        stack.addArrangedSubview(sentToggle)
        for proposal in today where showsSent {
            stack.addArrangedSubview(label(proposal.text))
            let result = proposal.kind?.title ?? "送信済み"
            let correction = NSPopUpButton()
            correction.isEnabled = !service.storeError
            correction.addItem(withTitle: "種類を直す")
            for kind in TriageKind.allCases {
                correction.addItem(withTitle: kind.title)
                let item = correction.lastItem
                let action = TriageButtonAction { [weak service, weak correction] in
                    service?.correct(proposal.id, to: kind)
                    correction?.selectItem(at: 0)
                }
                item?.target = action
                item?.action = #selector(TriageButtonAction.press)
                item?.representedObject = action
            }
            stack.addArrangedSubview(NSStackView(views: [label(result, secondary: true), correction]))
        }

        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        let document = TriageDocumentView(frame: NSRect(x: 0, y: 0, width: 620, height: 460))
        document.addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: document.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: document.trailingAnchor),
            stack.topAnchor.constraint(equalTo: document.topAnchor),
        ])
        tick()
        document.frame.size.height = max(460, stack.fittingSize.height)
        scroll.documentView = document
        window.contentView = scroll
        scroll.contentView.scroll(to: scroll.contentView.constrainBoundsRect(
            NSRect(origin: previousOffset, size: scroll.contentView.bounds.size)).origin)
    }

    private struct SendChoice {
        let kind: TriageKind
        let route: Int
        let name: String
        let icon: NSImage?
    }

    // 下書きを 1 に固定する。自分の作業は自動の振り分けにだけ任せ、ボタンは出さない
    private func sendChoices() -> [SendChoice] {
        let kinds = [TriageKind.writeText] + TriageKind.allCases.filter { $0 != .writeText && $0 != .selfTask }
        return kinds.flatMap { kind in
            service.routeNames(kind).enumerated().map { index, name in
                let app = name.split(whereSeparator: { $0 == "（" || $0 == "(" }).first
                    .map { $0.trimmingCharacters(in: .whitespaces) } ?? name
                let path = "/Applications/\(app).app"
                var icon: NSImage?
                if FileManager.default.fileExists(atPath: path) {
                    icon = NSWorkspace.shared.icon(forFile: path)
                    icon?.size = NSSize(width: 16, height: 16)
                }
                return SendChoice(kind: kind, route: index, name: name, icon: icon)
            }
        }
    }

    private func section(_ title: String, count: Int, in stack: NSStackView) {
        let heading = label("\(title) (\(count))")
        heading.font = .boldSystemFont(ofSize: NSFont.systemFontSize)
        stack.addArrangedSubview(heading)
    }

    private func label(_ text: String, secondary: Bool = false) -> NSTextField {
        let view = NSTextField(wrappingLabelWithString: text)
        view.preferredMaxLayoutWidth = 590
        view.isSelectable = !secondary
        if secondary { view.textColor = .secondaryLabelColor }
        return view
    }

    private func button(_ title: String, enabled: Bool = true, action: @escaping () -> Void) -> NSButton {
        let button = TriageButton(title: title, target: nil, action: #selector(TriageButtonAction.press))
        button.handler = TriageButtonAction(action)
        button.target = button.handler
        button.isEnabled = enabled && !service.storeError
        return button
    }

    private func menu(_ title: String, options: [String], action: @escaping (Int) -> Void) -> NSPopUpButton {
        let menu = NSPopUpButton(frame: .zero, pullsDown: true)
        menu.isEnabled = !options.isEmpty && !service.storeError
        menu.addItem(withTitle: title)
        for (index, option) in options.enumerated() {
            let item = NSMenuItem(title: option, action: #selector(TriageButtonAction.press), keyEquivalent: "")
            let handler = TriageButtonAction { action(index) }
            item.target = handler
            item.representedObject = handler
            menu.menu?.addItem(item)
        }
        return menu
    }

    private func tick() {
        for (label, deadline) in countdowns {
            let seconds = max(0, min(359999, Int(min(359999, ceil(deadline.timeIntervalSinceNow)))))
            label.stringValue = service.paused ? "一時停止中" : String(format: "残り %d:%02d", seconds / 60, seconds % 60)
        }
    }
}

private final class TriageWindow: NSPanel {
    var onDigit: ((Int) -> Void)?

    override func keyDown(with event: NSEvent) {
        let mods = event.modifierFlags.intersection(.deviceIndependentFlagsMask).subtracting([.numericPad, .function])
        if mods.isEmpty, let chars = event.charactersIgnoringModifiers, chars.count == 1, let digit = Int(chars) {
            onDigit?(digit)
        } else {
            super.keyDown(with: event)
        }
    }
}

private final class TriageDocumentView: NSView {
    override var isFlipped: Bool { true }
}

private final class TriageButton: NSButton {
    var handler: TriageButtonAction?
}

private final class TriageButtonAction: NSObject {
    let action: () -> Void
    init(_ action: @escaping () -> Void) { self.action = action }
    @objc func press() { action() }
}
#endif
