import Foundation
import Observation
import ClipCore

struct Shortcut: Codable, Equatable, Sendable {
    var keyCode: UInt32
    var modifiers: UInt32
    var key: String

    static let cmd: UInt32 = 1 << 8
    static let shift: UInt32 = 1 << 9
    static let option: UInt32 = 1 << 11
    static let control: UInt32 = 1 << 12

    static let defaultHistory = Shortcut(keyCode: 9, modifiers: option | cmd, key: "V")

    var display: String {
        var s = ""
        if modifiers & Shortcut.control != 0 { s += "\u{2303}" }
        if modifiers & Shortcut.option != 0 { s += "\u{2325}" }
        if modifiers & Shortcut.shift != 0 { s += "\u{21E7}" }
        if modifiers & Shortcut.cmd != 0 { s += "\u{2318}" }
        return s + key
    }
}

@MainActor
@Observable
final class AppSettings {
    private let defaults = UserDefaults.standard
    static let defaultServer = "https://clip.yikz.dev"

    var serverURL: String { didSet { defaults.set(serverURL, forKey: "serverURL") } }
    var username: String { didSet { defaults.set(username, forKey: "username") } }
    var deviceName: String { didSet { defaults.set(deviceName, forKey: "deviceName") } }
    var deviceId: String? { didSet { defaults.set(deviceId, forKey: "deviceId") } }
    var saltB64: String? { didSet { defaults.set(saltB64, forKey: "saltB64") } }
    var lastServerId: String? { didSet { defaults.set(lastServerId, forKey: "lastServerId") } }
    var syncText: Bool { didSet { defaults.set(syncText, forKey: "syncText") } }
    var syncImages: Bool { didSet { defaults.set(syncImages, forKey: "syncImages") } }
    var syncFiles: Bool { didSet { defaults.set(syncFiles, forKey: "syncFiles") } }
    var autoDownloadMB: Int { didSet { defaults.set(autoDownloadMB, forKey: "autoDownloadMB") } }
    var paused: Bool { didSet { defaults.set(paused, forKey: "paused") } }
    var showInMenuBar: Bool { didSet { defaults.set(showInMenuBar, forKey: "showInMenuBar") } }
    var showInDock: Bool { didSet { defaults.set(showInDock, forKey: "showInDock") } }
    var autoInstallUpdates: Bool { didSet { defaults.set(autoInstallUpdates, forKey: "autoInstallUpdates") } }
    var lastUpdateCheck: Date? { didSet { defaults.set(lastUpdateCheck, forKey: "lastUpdateCheck") } }
    var historyShortcut: Shortcut {
        didSet {
            if let d = try? JSONEncoder().encode(historyShortcut) { defaults.set(d, forKey: "historyShortcut") }
        }
    }

    init() {
        let d = UserDefaults.standard
        serverURL = d.string(forKey: "serverURL") ?? AppSettings.defaultServer
        username = d.string(forKey: "username") ?? ""
        deviceName = d.string(forKey: "deviceName") ?? AppSettings.defaultDeviceName
        deviceId = d.string(forKey: "deviceId")
        saltB64 = d.string(forKey: "saltB64")
        lastServerId = d.string(forKey: "lastServerId")
        syncText = d.object(forKey: "syncText") as? Bool ?? true
        syncImages = d.object(forKey: "syncImages") as? Bool ?? true
        syncFiles = d.object(forKey: "syncFiles") as? Bool ?? true
        autoDownloadMB = d.object(forKey: "autoDownloadMB") as? Int ?? 50
        paused = d.bool(forKey: "paused")
        showInMenuBar = d.object(forKey: "showInMenuBar") as? Bool ?? true
        showInDock = d.object(forKey: "showInDock") as? Bool ?? false
        autoInstallUpdates = d.object(forKey: "autoInstallUpdates") as? Bool ?? true
        lastUpdateCheck = d.object(forKey: "lastUpdateCheck") as? Date
        if let data = d.data(forKey: "historyShortcut"), let s = try? JSONDecoder().decode(Shortcut.self, from: data) {
            historyShortcut = s
        } else {
            historyShortcut = .defaultHistory
        }
    }

    static var defaultDeviceName: String {
        let name = Host.current().localizedName ?? ProcessInfo.processInfo.hostName
        return name.isEmpty ? "Mac" : name
    }

    var engineSettings: EngineSettings {
        EngineSettings(
            autoDownloadLimit: Int64(max(1, autoDownloadMB)) * 1_048_576,
            syncText: syncText,
            syncImages: syncImages,
            syncFiles: syncFiles,
            paused: paused
        )
    }
}
