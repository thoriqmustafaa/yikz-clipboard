import SwiftUI
import AppKit
import ClipCore

@main
struct YikzClipboardApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @ViewState private var model = AppModel.shared

    var body: some Scene {
        MenuBarExtra(isInserted: Binding(
            get: { model.settings.showInMenuBar },
            set: { AppVisibility.setShowInMenuBar($0) }
        )) {
            MenuBarView()
                .environment(model)
        } label: {
            Image(systemName: model.menuBarSymbol)
                .accessibilityLabel("Yikz Clipboard, \(model.statusTitle)")
        }
        .menuBarExtraStyle(.window)
        .commands {
            CommandGroup(replacing: .appInfo) {
                Button("About Yikz Clipboard") { AppVisibility.showAbout() }
                Button("Check for Updates\u{2026}") {
                    AppModel.shared.updates.checkNow()
                    WindowManager.shared.showSettings(tab: .updates)
                }
            }
            CommandGroup(replacing: .appSettings) {
                Button("Settings\u{2026}") { WindowManager.shared.showSettings() }
                    .keyboardShortcut(",", modifiers: .command)
            }
            CommandGroup(before: .windowList) {
                Button("Clipboard History") { WindowManager.shared.showHistory() }
                Divider()
            }
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationWillFinishLaunching(_ notification: Notification) {
        MainActor.assumeIsolated {
            if AppVisibility.handOffToRunningInstance() {
                exit(0)
            }
        }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        MainActor.assumeIsolated {
            AppVisibility.applyActivationPolicy()
            AppVisibility.observeReopenRequests()
            AppModel.shared.start()
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        MainActor.assumeIsolated {
            AppVisibility.handleReopen()
        }
        return false
    }

    func applicationWillTerminate(_ notification: Notification) {
        Log.info("Quitting", "app")
        Log.shared.flush()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }
}
