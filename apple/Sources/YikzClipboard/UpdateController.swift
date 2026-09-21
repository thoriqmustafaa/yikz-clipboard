import AppKit
import Observation
import ClipCore

enum UpdateStatus: Equatable {
    case idle
    case checking
    case upToDate
    case downloading(Double)
    case ready(String)
    case installing
    case failed(String)
}

@MainActor
@Observable
final class UpdateController {
    var status: UpdateStatus = .idle
    var latest: LatestRelease?
    @ObservationIgnored private var prepared: PreparedUpdate?
    @ObservationIgnored private var updater: Updater?
    @ObservationIgnored private var inFlight = false
    @ObservationIgnored private var rejectedVersion: String?
    @ObservationIgnored private var started = false
    @ObservationIgnored private var idleTimer: Timer?

    private var model: AppModel { AppModel.shared }

    var readyVersion: String? {
        if case .ready(let v) = status { return v }
        return nil
    }

    var isBusy: Bool {
        switch status {
        case .checking, .downloading, .installing: return true
        default: return false
        }
    }

    func start() {
        guard !started else { return }
        started = true
        let updater = Updater(
            currentVersion: model.appVersion,
            bundleId: Bundle.main.bundleIdentifier ?? "dev.yikz.clipboard",
            updatesDir: model.paths.updates,
            transport: model.transport
        )
        self.updater = updater
        Task {
            await updater.cleanUp()
            try? await Task.sleep(for: .seconds(UpdatePolicy.firstCheckDelay))
            await self.check(userInitiated: false)
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(UpdatePolicy.checkInterval))
                self.rejectedVersion = nil
                await self.check(userInitiated: false)
            }
        }
    }

    func checkNow() {
        Task { await check(userInitiated: true) }
    }

    func releaseAvailable(_ version: String) {
        if let offered = SemVer(version), let current = SemVer(model.appVersion), offered <= current { return }
        if let p = prepared, p.release.version == version { return }
        Task { await check(userInitiated: false) }
    }

    func autoInstallChanged() {
        if model.settings.autoInstallUpdates {
            scheduleIdleInstall()
        } else {
            idleTimer?.invalidate()
            idleTimer = nil
        }
    }

    private func check(userInitiated: Bool) async {
        guard let updater, !inFlight else { return }
        if case .installing = status { return }
        guard SemVer(model.appVersion) != nil else {
            if userInitiated { status = .failed("Updates are not available for development builds.") }
            return
        }
        guard let creds = Keychain.loadCredentials(), let url = APIClient.normalizedServerURL(model.settings.serverURL) else {
            if userInitiated { status = .failed("Sign in to check for updates.") }
            return
        }
        inFlight = true
        defer { inFlight = false }
        if prepared == nil || userInitiated { status = .checking }
        var candidate: String?
        do {
            let (decision, latest) = try await updater.check(serverURL: url, token: creds.token)
            model.settings.lastUpdateCheck = Date()
            if let latest { self.latest = latest }
            switch decision {
            case .upToDate:
                if let p = prepared {
                    status = .ready(p.release.version)
                } else {
                    status = .upToDate
                    Log.info("Yikz Clipboard \(model.appVersion) is up to date", "update")
                }
            case .rejected(let reason):
                Log.warning("Update check rejected the offered release: \(reason)", "update")
                status = prepared.map { .ready($0.release.version) } ?? .failed(reason)
            case .available(let release):
                candidate = release.version
                if let p = prepared, p.release.version == release.version {
                    status = .ready(p.release.version)
                    return
                }
                if release.version == rejectedVersion && !userInitiated {
                    status = .failed("Update \(release.version) failed verification. It will be retried at the next check.")
                    return
                }
                if CodeSignature.runningIsAdHoc() {
                    throw UpdateInstallError.adHocInstall
                }
                try UpdateInstaller.checkInstallLocation(Bundle.main.bundleURL)
                Log.info("Update \(release.version) available (running \(model.appVersion))", "update")
                status = .downloading(0)
                let p = try await updater.prepare(release, serverURL: url, token: creds.token) { fraction in
                    Task { @MainActor in AppModel.shared.updates.downloadProgress(fraction) }
                }
                prepared = p
                status = .ready(release.version)
                Log.info("Update \(release.version) is ready to install", "update")
                scheduleIdleInstall()
            }
        } catch is CancellationError {
            status = prepared.map { .ready($0.release.version) } ?? .idle
        } catch {
            let message = Self.message(for: error)
            Log.error("Update failed: \(message)", "update")
            if let candidate, error is ReleaseVerificationError || error is UpdateInstallError {
                rejectedVersion = candidate
            }
            status = prepared.map { .ready($0.release.version) } ?? .failed(message)
        }
    }

    fileprivate func downloadProgress(_ fraction: Double) {
        guard case .downloading(let old) = status else { return }
        if Int(old * 100) != Int(fraction * 100) {
            status = .downloading(fraction)
        }
    }

    private func scheduleIdleInstall() {
        idleTimer?.invalidate()
        idleTimer = nil
        guard model.settings.autoInstallUpdates, prepared != nil else { return }
        idleTimer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { _ in
            Task { @MainActor in AppModel.shared.updates.tryAutoInstall() }
        }
    }

    private func tryAutoInstall() {
        guard model.settings.autoInstallUpdates, prepared != nil, readyVersion != nil else {
            idleTimer?.invalidate()
            idleTimer = nil
            return
        }
        guard isIdle else { return }
        Log.info("App is idle; installing the update automatically", "update")
        installNow()
    }

    private var isIdle: Bool {
        NSApp.keyWindow == nil
            && NSApp.modalWindow == nil
            && !WindowManager.shared.isHistoryVisible
            && model.transfers.isEmpty
            && !model.syncing
    }

    func installNow() {
        guard let prepared, let updater else { return }
        idleTimer?.invalidate()
        idleTimer = nil
        status = .installing
        let appURL = Bundle.main.bundleURL
        let pid = ProcessInfo.processInfo.processIdentifier
        Task {
            do {
                try await updater.install(prepared, installedApp: appURL, pid: pid)
                Log.info("Quitting to install \(prepared.release.version)", "update")
                Log.shared.flush()
                NSApp.terminate(nil)
            } catch {
                let message = Self.message(for: error)
                Log.error("Update install failed: \(message)", "update")
                self.status = .failed(message)
            }
        }
    }

    static func message(for error: Error) -> String {
        if let e = error as? APIError {
            if e.status == 404 { return "This server does not offer updates yet." }
            return e.userMessage
        }
        if let e = error as? UpdateInstallError { return e.description }
        if let e = error as? ReleaseVerificationError { return e.description }
        return error.localizedDescription
    }
}
