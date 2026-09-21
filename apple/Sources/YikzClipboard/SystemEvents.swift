import AppKit
import Network
import ApplicationServices
import UserNotifications
import ClipCore

@MainActor
final class SystemEvents {
    private var observers: [NSObjectProtocol] = []
    private let pathMonitor = NWPathMonitor()
    private var lastPathSignature: String?
    var onWake: (() -> Void)?
    var onSleep: (() -> Void)?
    var onProbe: ((String) -> Void)?
    var onNetworkChange: ((Bool) -> Void)?

    func start() {
        let nc = NSWorkspace.shared.notificationCenter
        observers.append(nc.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                Log.info("System woke from sleep", "system")
                self?.onWake?()
            }
        })
        observers.append(nc.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                Log.info("System is going to sleep", "system")
                self?.onSleep?()
            }
        })
        observers.append(nc.addObserver(forName: NSWorkspace.screensDidWakeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                Log.info("Screens woke", "system")
                self?.onProbe?("screens woke")
            }
        })
        observers.append(nc.addObserver(forName: NSWorkspace.sessionDidBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                Log.info("User session became active", "system")
                self?.onProbe?("session active")
            }
        })
        pathMonitor.pathUpdateHandler = { [weak self] path in
            let satisfied = path.status == .satisfied
            let names = path.availableInterfaces.map { "\($0.type)-\($0.name)" }.sorted().joined(separator: ",")
            let signature = "\(path.status)|\(names)|\(path.isExpensive)|\(path.isConstrained)"
            Task { @MainActor in
                self?.handlePath(satisfied: satisfied, signature: signature, interfaces: names)
            }
        }
        pathMonitor.start(queue: DispatchQueue(label: "dev.yikz.clipboard.path"))
    }

    private func handlePath(satisfied: Bool, signature: String, interfaces: String) {
        guard let previous = lastPathSignature else {
            lastPathSignature = signature
            return
        }
        guard previous != signature else { return }
        lastPathSignature = signature
        Log.info("Network changed: \(satisfied ? "available" : "unavailable") [\(interfaces)]", "system")
        onNetworkChange?(satisfied)
    }

    func stop() {
        for o in observers { NSWorkspace.shared.notificationCenter.removeObserver(o) }
        observers = []
        pathMonitor.cancel()
    }
}

@MainActor
final class ActivityGuard {
    private var token: NSObjectProtocol?

    func hold(_ on: Bool) {
        if on, token == nil {
            token = ProcessInfo.processInfo.beginActivity(
                options: [.userInitiatedAllowingIdleSystemSleep],
                reason: "Keeping the clipboard sync connection alive"
            )
            Log.debug("App Nap prevention on", "system")
        } else if !on, let t = token {
            ProcessInfo.processInfo.endActivity(t)
            token = nil
            Log.debug("App Nap prevention off", "system")
        }
    }
}

@MainActor
enum Accessibility {
    static var isTrusted: Bool { AXIsProcessTrusted() }

    @discardableResult
    static func requestIfNeeded() -> Bool {
        if AXIsProcessTrusted() { return true }
        let opts = ["AXTrustedCheckOptionPrompt": true] as CFDictionary
        return AXIsProcessTrustedWithOptions(opts)
    }

    static func openSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }

    static func pasteToFrontmostApp() {
        let src = CGEventSource(stateID: .combinedSessionState)
        let vKey: CGKeyCode = 9
        let down = CGEvent(keyboardEventSource: src, virtualKey: vKey, keyDown: true)
        down?.flags = .maskCommand
        let up = CGEvent(keyboardEventSource: src, virtualKey: vKey, keyDown: false)
        up?.flags = .maskCommand
        down?.post(tap: .cghidEventTap)
        up?.post(tap: .cghidEventTap)
    }
}

final class NotificationBridge: NSObject, UNUserNotificationCenterDelegate, @unchecked Sendable {
    static let category = "LARGE_ITEM"
    static let downloadAction = "DOWNLOAD"
    var onDownload: (@Sendable (String) -> Void)?
    private(set) var available = false

    static var canUseNotifications: Bool {
        Bundle.main.bundleIdentifier != nil && Bundle.main.bundleURL.pathExtension == "app"
    }

    func setUp() {
        guard NotificationBridge.canUseNotifications else {
            Log.info("Notifications unavailable outside an app bundle", "notify")
            return
        }
        let center = UNUserNotificationCenter.current()
        center.delegate = self
        let download = UNNotificationAction(identifier: NotificationBridge.downloadAction, title: "Download", options: [])
        let category = UNNotificationCategory(identifier: NotificationBridge.category, actions: [download], intentIdentifiers: [], options: [])
        center.setNotificationCategories([category])
        center.requestAuthorization(options: [.alert, .sound]) { [weak self] granted, error in
            self?.available = granted
            if let error {
                Log.warning("Notification permission unavailable: \(error.localizedDescription)", "notify")
            } else {
                Log.info("Notifications \(granted ? "allowed" : "not allowed")", "notify")
            }
        }
    }

    func postLargeItem(id: String, title: String, size: String, device: String) {
        guard NotificationBridge.canUseNotifications else { return }
        let content = UNMutableNotificationContent()
        content.title = "\(title) (\(size))"
        content.body = "From \(device). Download to put it on the clipboard."
        content.categoryIdentifier = NotificationBridge.category
        content.userInfo = ["id": id]
        let req = UNNotificationRequest(identifier: id, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(req) { error in
            if let error { Log.warning("Cannot show notification: \(error.localizedDescription)", "notify") }
        }
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse, withCompletionHandler completionHandler: @escaping () -> Void) {
        let id = response.notification.request.content.userInfo["id"] as? String
        if let id, response.actionIdentifier == NotificationBridge.downloadAction || response.actionIdentifier == UNNotificationDefaultActionIdentifier {
            onDownload?(id)
        }
        completionHandler()
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification, withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound])
    }
}
