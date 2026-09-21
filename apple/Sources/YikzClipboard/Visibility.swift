import AppKit
import ClipCore

@MainActor
enum AppVisibility {
    static let reopenNotification = Notification.Name("dev.yikz.clipboard.reopen")
    private static var observer: NSObjectProtocol?

    private static var settings: AppSettings { AppModel.shared.settings }

    static func setShowInMenuBar(_ on: Bool) {
        guard on != settings.showInMenuBar else { return }
        if !on && !settings.showInDock && !confirmHidingEverything() {
            settings.showInMenuBar = settings.showInMenuBar
            return
        }
        settings.showInMenuBar = on
        Log.info(on ? "Menu bar icon shown" : "Menu bar icon hidden", "app")
    }

    static func setShowInDock(_ on: Bool) {
        guard on != settings.showInDock else { return }
        if !on && !settings.showInMenuBar && !confirmHidingEverything() {
            settings.showInDock = settings.showInDock
            return
        }
        settings.showInDock = on
        Log.info(on ? "Dock icon shown" : "Dock icon hidden", "app")
        applyActivationPolicy(keepFront: true)
    }

    static func confirmHidingEverything() -> Bool {
        let alert = NSAlert()
        alert.alertStyle = .informational
        alert.messageText = "Hide Yikz Clipboard from the menu bar and the Dock?"
        alert.informativeText = "Yikz Clipboard keeps running and syncing in the background. To reach it, press \(settings.historyShortcut.display) to open Clipboard History, or open Yikz Clipboard again from Finder, Spotlight or Launchpad to show Settings."
        alert.addButton(withTitle: "Hide Both")
        alert.addButton(withTitle: "Cancel")
        NSApp.activate()
        return alert.runModal() == .alertFirstButtonReturn
    }

    static func applyActivationPolicy(keepFront: Bool = false) {
        let wanted: NSApplication.ActivationPolicy = settings.showInDock ? .regular : .accessory
        if NSApp.activationPolicy() != wanted {
            NSApp.setActivationPolicy(wanted)
        }
        if wanted == .regular {
            MainMenu.installIfNeeded()
        }
        if keepFront {
            DispatchQueue.main.async {
                NSApp.activate()
                WindowManager.shared.bringSettingsToFront()
            }
        }
    }

    static func handleReopen() {
        if !settings.showInMenuBar && !settings.showInDock {
            WindowManager.shared.showSettings(tab: .general)
        } else {
            NSApp.activate()
            WindowManager.shared.showHistory()
        }
    }

    static func observeReopenRequests() {
        guard observer == nil else { return }
        observer = DistributedNotificationCenter.default().addObserver(forName: reopenNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { handleReopen() }
        }
    }

    static func handOffToRunningInstance() -> Bool {
        guard let id = Bundle.main.bundleIdentifier else { return false }
        let me = ProcessInfo.processInfo.processIdentifier
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: id)
            .filter { $0.processIdentifier != me && !$0.isTerminated }
        guard let other = others.first else { return false }
        DistributedNotificationCenter.default().postNotificationName(reopenNotification, object: nil, userInfo: nil, deliverImmediately: true)
        other.activate()
        return true
    }

    static func showAbout() {
        NSApp.activate()
        NSApp.orderFrontStandardAboutPanel(nil)
    }
}

@MainActor
final class MenuActions: NSObject {
    static let shared = MenuActions()

    @objc func about(_ sender: Any?) { AppVisibility.showAbout() }
    @objc func settings(_ sender: Any?) { WindowManager.shared.showSettings() }
    @objc func history(_ sender: Any?) { WindowManager.shared.showHistory() }
    @objc func checkForUpdates(_ sender: Any?) {
        AppModel.shared.updates.checkNow()
        WindowManager.shared.showSettings(tab: .updates)
    }
}

@MainActor
enum MainMenu {
    static func installIfNeeded() {
        if let menu = NSApp.mainMenu, menu.items.count > 1 { return }
        NSApp.mainMenu = build()
    }

    static func build() -> NSMenu {
        let main = NSMenu()
        let actions = MenuActions.shared

        let appMenu = NSMenu(title: "Yikz Clipboard")
        appMenu.addItem(item("About Yikz Clipboard", #selector(MenuActions.about(_:)), "", target: actions))
        appMenu.addItem(item("Check for Updates\u{2026}", #selector(MenuActions.checkForUpdates(_:)), "", target: actions))
        appMenu.addItem(.separator())
        appMenu.addItem(item("Settings\u{2026}", #selector(MenuActions.settings(_:)), ",", target: actions))
        appMenu.addItem(.separator())
        appMenu.addItem(item("Hide Yikz Clipboard", #selector(NSApplication.hide(_:)), "h"))
        let hideOthers = item("Hide Others", #selector(NSApplication.hideOtherApplications(_:)), "h")
        hideOthers.keyEquivalentModifierMask = [.command, .option]
        appMenu.addItem(hideOthers)
        appMenu.addItem(item("Show All", #selector(NSApplication.unhideAllApplications(_:)), ""))
        appMenu.addItem(.separator())
        appMenu.addItem(item("Quit Yikz Clipboard", #selector(NSApplication.terminate(_:)), "q"))
        main.addItem(submenu(appMenu))

        let edit = NSMenu(title: "Edit")
        edit.addItem(item("Undo", Selector(("undo:")), "z"))
        let redo = item("Redo", Selector(("redo:")), "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(redo)
        edit.addItem(.separator())
        edit.addItem(item("Cut", #selector(NSText.cut(_:)), "x"))
        edit.addItem(item("Copy", #selector(NSText.copy(_:)), "c"))
        edit.addItem(item("Paste", #selector(NSText.paste(_:)), "v"))
        edit.addItem(item("Select All", #selector(NSText.selectAll(_:)), "a"))
        main.addItem(submenu(edit))

        let window = NSMenu(title: "Window")
        window.addItem(item("Minimize", #selector(NSWindow.performMiniaturize(_:)), "m"))
        window.addItem(item("Zoom", #selector(NSWindow.performZoom(_:)), ""))
        window.addItem(.separator())
        window.addItem(item("Clipboard History", #selector(MenuActions.history(_:)), "", target: actions))
        window.addItem(.separator())
        window.addItem(item("Bring All to Front", #selector(NSApplication.arrangeInFront(_:)), ""))
        main.addItem(submenu(window))
        NSApp.windowsMenu = window
        return main
    }

    private static func item(_ title: String, _ action: Selector, _ key: String, target: AnyObject? = nil) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: action, keyEquivalent: key)
        i.target = target
        return i
    }

    private static func submenu(_ menu: NSMenu) -> NSMenuItem {
        let i = NSMenuItem(title: menu.title, action: nil, keyEquivalent: "")
        i.submenu = menu
        return i
    }
}
