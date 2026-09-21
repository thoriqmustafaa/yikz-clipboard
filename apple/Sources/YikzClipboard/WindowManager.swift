import AppKit
import SwiftUI
import ClipCore

final class HistoryPanel: NSPanel {
    var keyHandler: ((NSEvent) -> Bool)?
    var onResignKey: (() -> Void)?

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }

    override func sendEvent(_ event: NSEvent) {
        if event.type == .keyDown, let keyHandler, keyHandler(event) {
            return
        }
        super.sendEvent(event)
    }

    override func resignKey() {
        super.resignKey()
        onResignKey?()
    }

    override func cancelOperation(_ sender: Any?) {
        orderOut(nil)
    }
}

enum SettingsTab: Int {
    case general = 0
    case account = 1
    case sync = 2
    case updates = 3
}

@MainActor
final class WindowManager {
    static let shared = WindowManager()

    let historyModel = HistoryViewModel()
    private var panel: HistoryPanel?
    private var settingsWindow: NSWindow?
    private var tabController: NSTabViewController?
    private var logsWindow: NSWindow?
    private var previousApp: NSRunningApplication?

    var isHistoryVisible: Bool { panel?.isVisible == true }

    func toggleHistory() {
        if isHistoryVisible {
            hideHistory()
        } else {
            showHistory()
        }
    }

    func showHistory() {
        let model = AppModel.shared
        let panel = self.panel ?? makePanel()
        self.panel = panel
        if let front = NSWorkspace.shared.frontmostApplication, front.bundleIdentifier != Bundle.main.bundleIdentifier {
            previousApp = front
        }
        historyModel.prepareForShow(model)
        position(panel)
        panel.alphaValue = 0
        panel.makeKeyAndOrderFront(nil)
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.12
            panel.animator().alphaValue = 1
        }
    }

    func hideHistory(restoreFocus: Bool = false) {
        guard let panel, panel.isVisible else { return }
        panel.orderOut(nil)
        if restoreFocus || NSApp.isActive {
            if let app = previousApp, NSApp.isActive {
                app.activate()
            }
        }
    }

    private func position(_ panel: NSPanel) {
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main
        guard let visible = screen?.visibleFrame else { panel.center(); return }
        let size = panel.frame.size
        let x = visible.midX - size.width / 2
        let y = visible.midY - size.height / 2 + visible.height * 0.08
        panel.setFrameOrigin(NSPoint(x: x.rounded(), y: min(y, visible.maxY - size.height).rounded()))
    }

    private func makePanel() -> HistoryPanel {
        let panel = HistoryPanel(
            contentRect: NSRect(x: 0, y: 0, width: 820, height: 520),
            styleMask: [.nonactivatingPanel, .titled, .fullSizeContentView, .resizable],
            backing: .buffered,
            defer: true
        )
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.standardWindowButton(.closeButton)?.isHidden = true
        panel.standardWindowButton(.miniaturizeButton)?.isHidden = true
        panel.standardWindowButton(.zoomButton)?.isHidden = true
        panel.isMovableByWindowBackground = true
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient]
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .utilityWindow
        panel.minSize = NSSize(width: 680, height: 400)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        let root = HistoryView(vm: historyModel)
            .environment(AppModel.shared)
            .ignoresSafeArea()
        let effect = NSVisualEffectView()
        effect.material = .popover
        effect.blendingMode = .behindWindow
        effect.state = .active
        let hosting = NSHostingView(rootView: root)
        hosting.sizingOptions = []
        hosting.translatesAutoresizingMaskIntoConstraints = false
        effect.addSubview(hosting)
        NSLayoutConstraint.activate([
            hosting.leadingAnchor.constraint(equalTo: effect.leadingAnchor),
            hosting.trailingAnchor.constraint(equalTo: effect.trailingAnchor),
            hosting.topAnchor.constraint(equalTo: effect.topAnchor),
            hosting.bottomAnchor.constraint(equalTo: effect.bottomAnchor)
        ])
        panel.contentView = effect
        panel.keyHandler = { [weak self] event in
            guard let self else { return false }
            return self.historyModel.handleKey(event, AppModel.shared)
        }
        panel.onResignKey = { [weak self] in
            Task { @MainActor in
                guard let self, let panel = self.panel, panel.isVisible, !panel.isKeyWindow else { return }
                if NSApp.modalWindow == nil {
                    panel.orderOut(nil)
                }
            }
        }
        historyModel.panel = panel
        return panel
    }

    func showSettings(tab: SettingsTab? = nil) {
        if settingsWindow == nil {
            let model = AppModel.shared
            let tabs = NSTabViewController()
            tabs.tabStyle = .toolbar
            tabs.transitionOptions = [.crossfade, .allowUserInteraction]
            tabs.canPropagateSelectedChildViewControllerTitle = true
            func add<V: View>(_ view: V, _ label: String, _ symbol: String) {
                let hc = NSHostingController(rootView: view.environment(model))
                hc.sizingOptions = [.preferredContentSize]
                hc.title = label
                let item = NSTabViewItem(viewController: hc)
                item.label = label
                item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: label)
                tabs.addTabViewItem(item)
            }
            add(GeneralPane(), "General", "gearshape")
            add(AccountPane(), "Account", "person.crop.circle")
            add(SyncPane(), "Sync", "arrow.triangle.2.circlepath")
            add(UpdatesPane(), "Updates", "arrow.down.circle")
            let window = NSWindow(contentViewController: tabs)
            window.styleMask = [.titled, .closable, .miniaturizable]
            window.isReleasedWhenClosed = false
            window.toolbarStyle = .preference
            window.center()
            settingsWindow = window
            tabController = tabs
        }
        if let tab {
            tabController?.selectedTabViewItemIndex = tab.rawValue
        }
        AppModel.shared.refreshSystemState()
        NSApp.activate()
        settingsWindow?.makeKeyAndOrderFront(nil)
    }

    func bringSettingsToFront() {
        guard let window = settingsWindow, window.isVisible else { return }
        window.makeKeyAndOrderFront(nil)
    }

    func showLogs() {
        if logsWindow == nil {
            let hc = NSHostingController(rootView: LogsView().environment(AppModel.shared))
            let window = NSWindow(contentViewController: hc)
            window.title = "Yikz Clipboard Logs"
            window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
            window.setContentSize(NSSize(width: 900, height: 560))
            window.minSize = NSSize(width: 640, height: 360)
            window.isReleasedWhenClosed = false
            window.center()
            window.setFrameAutosaveName("YikzLogsWindow")
            logsWindow = window
        }
        NSApp.activate()
        logsWindow?.makeKeyAndOrderFront(nil)
    }
}
