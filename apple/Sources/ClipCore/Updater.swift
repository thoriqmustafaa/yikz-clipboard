import Foundation
import CryptoKit
import Security

public struct SemVer: Comparable, Hashable, Sendable, CustomStringConvertible {
    public let major: Int
    public let minor: Int
    public let patch: Int

    public init(major: Int, minor: Int, patch: Int) {
        self.major = major
        self.minor = minor
        self.patch = patch
    }

    public init?(_ string: String) {
        var s = string.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.hasPrefix("v") || s.hasPrefix("V") { s.removeFirst() }
        if let cut = s.firstIndex(where: { $0 == "-" || $0 == "+" }) { s = String(s[..<cut]) }
        let parts = s.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return nil }
        var nums: [Int] = []
        for p in parts {
            guard !p.isEmpty, p.count <= 9, p.allSatisfy({ $0.isASCII && $0.isNumber }), let n = Int(p) else { return nil }
            nums.append(n)
        }
        self.init(major: nums[0], minor: nums[1], patch: nums[2])
    }

    public var buildNumber: Int { major * 10000 + minor * 100 + patch }

    public var description: String { "\(major).\(minor).\(patch)" }

    public static func < (a: SemVer, b: SemVer) -> Bool {
        (a.major, a.minor, a.patch) < (b.major, b.minor, b.patch)
    }
}

public struct ReleaseAsset: Codable, Sendable, Equatable {
    public var platform: String
    public var file: String
    public var size: Int64
    public var sha256: String
    public var signature: String
    public var url: String?

    public init(platform: String, file: String, size: Int64, sha256: String, signature: String, url: String? = nil) {
        self.platform = platform
        self.file = file
        self.size = size
        self.sha256 = sha256
        self.signature = signature
        self.url = url
    }
}

public struct LatestRelease: Codable, Sendable, Equatable {
    public var version: String
    public var publishedAt: Date?
    public var notesMd: String
    public var asset: ReleaseAsset

    public init(version: String, publishedAt: Date? = nil, notesMd: String = "", asset: ReleaseAsset) {
        self.version = version
        self.publishedAt = publishedAt
        self.notesMd = notesMd
        self.asset = asset
    }

    enum CodingKeys: String, CodingKey {
        case version, publishedAt, notesMd, asset
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        version = try c.decode(String.self, forKey: .version)
        publishedAt = try? c.decodeIfPresent(Date.self, forKey: .publishedAt)
        notesMd = try c.decodeIfPresent(String.self, forKey: .notesMd) ?? ""
        asset = try c.decode(ReleaseAsset.self, forKey: .asset)
    }

    public static func decode(_ data: Data) throws -> LatestRelease {
        try JSONCoding.decoder().decode(LatestRelease.self, from: data)
    }
}

public enum UpdateDecision: Sendable, Equatable {
    case upToDate
    case available(LatestRelease)
    case rejected(String)
}

public enum UpdatePolicy {
    public static let platform = "macos"
    public static let releasePublicKeyB64 = "2Ve8Uwt53+AwbuAiK08Xf2gB5EU7pguqXL7I9yeqGGk="
    public static let firstCheckDelay: TimeInterval = 10
    public static let checkInterval: TimeInterval = 6 * 3600

    public static func isSafeFileName(_ name: String) -> Bool {
        guard (1...128).contains(name.utf8.count), name != ".", name != ".." else { return false }
        return name.utf8.allSatisfy { c in
            (c >= 48 && c <= 57) || (c >= 65 && c <= 90) || (c >= 97 && c <= 122) || c == 46 || c == 95 || c == 45
        }
    }

    public static func decide(currentVersion: String, latest: LatestRelease?, platform: String = UpdatePolicy.platform) -> UpdateDecision {
        guard let latest else { return .upToDate }
        guard let current = SemVer(currentVersion) else { return .rejected("The running version \(currentVersion) is not a release version.") }
        guard let offered = SemVer(latest.version) else { return .rejected("The server offered an invalid version \(latest.version).") }
        guard offered > current else { return .upToDate }
        guard latest.asset.platform == platform else {
            return .rejected("The server offered a \(latest.asset.platform) package instead of \(platform).")
        }
        guard isSafeFileName(latest.asset.file) else { return .rejected("The release file name is invalid.") }
        guard latest.asset.sha256.count == 64, Data(hex: latest.asset.sha256.lowercased()) != nil else {
            return .rejected("The release checksum is invalid.")
        }
        guard let sig = Data(base64Encoded: latest.asset.signature), sig.count == 64 else {
            return .rejected("The release signature is invalid.")
        }
        guard latest.asset.size > 0 else { return .rejected("The release size is invalid.") }
        return .available(latest)
    }

    public static func assetURL(base: URL, release: LatestRelease) -> URL? {
        if let raw = release.asset.url, !raw.isEmpty {
            if let abs = URL(string: raw), let scheme = abs.scheme?.lowercased() {
                guard scheme == "https" || scheme == "http" else { return nil }
                return abs
            }
            guard raw.hasPrefix("/") else { return nil }
            var comps = URLComponents(url: base, resolvingAgainstBaseURL: false)
            let basePath = comps?.path.hasSuffix("/") == true ? String(comps!.path.dropLast()) : (comps?.path ?? "")
            let parts = raw.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false)
            comps?.percentEncodedPath = basePath + String(parts[0])
            comps?.percentEncodedQuery = parts.count > 1 ? String(parts[1]) : nil
            return comps?.url
        }
        var comps = URLComponents(url: base, resolvingAgainstBaseURL: false)
        let basePath = comps?.path.hasSuffix("/") == true ? String(comps!.path.dropLast()) : (comps?.path ?? "")
        comps?.path = basePath + "/api/releases/\(release.version)/assets/\(release.asset.file)"
        comps?.query = nil
        return comps?.url
    }

    public static func sameOrigin(_ a: URL, _ b: URL) -> Bool {
        a.scheme?.lowercased() == b.scheme?.lowercased() && a.host?.lowercased() == b.host?.lowercased() && a.port == b.port
    }
}

public enum ReleaseVerificationError: Error, Equatable, CustomStringConvertible {
    case invalidPublicKey
    case invalidSignatureEncoding
    case invalidChecksumEncoding
    case sizeMismatch(expected: Int64, actual: Int64)
    case checksumMismatch
    case signatureMismatch
    case unreadable(String)

    public var description: String {
        switch self {
        case .invalidPublicKey: return "The embedded release key is invalid."
        case .invalidSignatureEncoding: return "The release signature is malformed."
        case .invalidChecksumEncoding: return "The release checksum is malformed."
        case .sizeMismatch(let e, let a): return "The download has \(a) bytes, expected \(e)."
        case .checksumMismatch: return "The download does not match its SHA-256 checksum."
        case .signatureMismatch: return "The download signature is not valid."
        case .unreadable(let m): return "The download cannot be read: \(m)"
        }
    }
}

public enum ReleaseVerifier {
    public static func sha256(of data: Data) -> Data {
        Data(SHA256.hash(data: data))
    }

    public static func sha256(ofFile url: URL) throws -> Data {
        let handle: FileHandle
        do {
            handle = try FileHandle(forReadingFrom: url)
        } catch {
            throw ReleaseVerificationError.unreadable(error.localizedDescription)
        }
        defer { try? handle.close() }
        var hasher = SHA256()
        while true {
            let chunk: Data?
            do {
                chunk = try handle.read(upToCount: 1_048_576)
            } catch {
                throw ReleaseVerificationError.unreadable(error.localizedDescription)
            }
            guard let chunk, !chunk.isEmpty else { break }
            hasher.update(data: chunk)
        }
        return Data(hasher.finalize())
    }

    public static func verify(digest: Data, sha256Hex: String, signatureB64: String, publicKeyB64: String = UpdatePolicy.releasePublicKeyB64) throws {
        guard let expected = Data(hex: sha256Hex.lowercased()), expected.count == 32 else {
            throw ReleaseVerificationError.invalidChecksumEncoding
        }
        guard digest == expected else { throw ReleaseVerificationError.checksumMismatch }
        guard let keyData = Data(base64Encoded: publicKeyB64), keyData.count == 32,
              let key = try? Curve25519.Signing.PublicKey(rawRepresentation: keyData) else {
            throw ReleaseVerificationError.invalidPublicKey
        }
        guard let sig = Data(base64Encoded: signatureB64), sig.count == 64 else {
            throw ReleaseVerificationError.invalidSignatureEncoding
        }
        guard key.isValidSignature(sig, for: digest) else { throw ReleaseVerificationError.signatureMismatch }
    }

    public static func verify(data: Data, sha256Hex: String, signatureB64: String, publicKeyB64: String = UpdatePolicy.releasePublicKeyB64) throws {
        try verify(digest: sha256(of: data), sha256Hex: sha256Hex, signatureB64: signatureB64, publicKeyB64: publicKeyB64)
    }

    public static func verify(file: URL, asset: ReleaseAsset, publicKeyB64: String = UpdatePolicy.releasePublicKeyB64) throws {
        let attrs = try? FileManager.default.attributesOfItem(atPath: file.path)
        let size = (attrs?[.size] as? NSNumber)?.int64Value ?? -1
        guard size == asset.size else { throw ReleaseVerificationError.sizeMismatch(expected: asset.size, actual: size) }
        try verify(digest: sha256(ofFile: file), sha256Hex: asset.sha256, signatureB64: asset.signature, publicKeyB64: publicKeyB64)
    }
}

public enum ReleaseNotes {
    public enum Block: Equatable, Sendable {
        case heading(String)
        case bullet(String)
        case paragraph(String)
    }

    public static func parse(_ markdown: String) -> [Block] {
        var blocks: [Block] = []
        for raw in markdown.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty { continue }
            if line.hasPrefix("#") {
                let text = line.drop(while: { $0 == "#" }).trimmingCharacters(in: .whitespaces)
                if !text.isEmpty { blocks.append(.heading(text)) }
            } else if line.hasPrefix("- ") || line.hasPrefix("* ") {
                blocks.append(.bullet(String(line.dropFirst(2)).trimmingCharacters(in: .whitespaces)))
            } else {
                blocks.append(.paragraph(line))
            }
        }
        return blocks
    }

    public static func inline(_ text: String) -> AttributedString {
        let options = AttributedString.MarkdownParsingOptions(interpretedSyntax: .inlineOnlyPreservingWhitespace)
        return (try? AttributedString(markdown: text, options: options)) ?? AttributedString(text)
    }
}

extension APIClient {
    public func latestRelease(platform: String) async throws -> LatestRelease? {
        let (data, response) = try await perform(request("GET", "/api/releases/latest", query: [URLQueryItem(name: "platform", value: platform)]))
        if response.statusCode == 204 || data.isEmpty { return nil }
        do {
            return try LatestRelease.decode(data)
        } catch {
            throw APIError.decoding(String(describing: error))
        }
    }
}

public final class ReleaseDownloader: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private let destination: URL
    private let progress: @Sendable (Int64, Int64) -> Void
    private let expectedSize: Int64
    private var continuation: CheckedContinuation<Void, Error>?
    private var failure: Error?

    private init(destination: URL, expectedSize: Int64, progress: @escaping @Sendable (Int64, Int64) -> Void) {
        self.destination = destination
        self.expectedSize = expectedSize
        self.progress = progress
    }

    public static func download(_ request: URLRequest, to destination: URL, expectedSize: Int64, progress: @escaping @Sendable (Int64, Int64) -> Void) async throws {
        let d = ReleaseDownloader(destination: destination, expectedSize: expectedSize, progress: progress)
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = 60
        cfg.timeoutIntervalForResource = 3600
        cfg.urlCache = nil
        cfg.requestCachePolicy = .reloadIgnoringLocalCacheData
        let session = URLSession(configuration: cfg, delegate: d, delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }
        let task = session.downloadTask(with: request)
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
                d.lock.lock()
                d.continuation = c
                d.lock.unlock()
                task.resume()
            }
        } onCancel: {
            task.cancel()
        }
    }

    public func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64, totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        let total = totalBytesExpectedToWrite > 0 ? totalBytesExpectedToWrite : expectedSize
        progress(totalBytesWritten, total)
    }

    public func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        let status = (downloadTask.response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status) else {
            setFailure(APIError.http(status: status, code: "http_\(status)", message: HTTPURLResponse.localizedString(forStatusCode: status), details: nil, retryAfter: nil))
            return
        }
        do {
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: location, to: destination)
        } catch {
            setFailure(error)
        }
    }

    public func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock()
        let c = continuation
        continuation = nil
        let stored = failure
        lock.unlock()
        if let error {
            if (error as? URLError)?.code == .cancelled {
                c?.resume(throwing: CancellationError())
            } else {
                c?.resume(throwing: APIError.network(error.localizedDescription))
            }
        } else if let stored {
            c?.resume(throwing: stored)
        } else {
            c?.resume()
        }
    }

    private func setFailure(_ e: Error) {
        lock.lock()
        failure = e
        lock.unlock()
    }
}

public enum UpdateInstallError: Error, Equatable, CustomStringConvertible {
    case unzipFailed(String)
    case bundleMissing
    case wrongBundle(String)
    case signatureInvalid(String)
    case requirementMismatch(String)
    case adHocInstall
    case locationNotWritable(String)
    case translocated
    case helperFailed(String)

    public var description: String {
        switch self {
        case .unzipFailed(let m): return "The update could not be unpacked: \(m)"
        case .bundleMissing: return "The update does not contain the app."
        case .wrongBundle(let m): return "The update package is not valid: \(m)"
        case .signatureInvalid(let m): return "The update code signature is not valid: \(m)"
        case .requirementMismatch(let m): return "The update is not signed by the same developer as this app: \(m)"
        case .adHocInstall:
            return "This copy of Yikz Clipboard was built without the release certificate, so it cannot update itself. Download the latest version once and replace the app in Applications; later updates install automatically."
        case .locationNotWritable(let path):
            return "Yikz Clipboard cannot replace itself at \(path) because that location is not writable. Move the app to the Applications folder (or a folder you own) and try again."
        case .translocated:
            return "macOS is running Yikz Clipboard from a temporary location. Move the app to the Applications folder, open it from there, and try again."
        case .helperFailed(let m): return "The update helper could not start: \(m)"
        }
    }
}

public enum CodeSignature {
    public static func runningRequirement() -> (SecRequirement, String)? {
        var code: SecCode?
        guard SecCodeCopySelf([], &code) == errSecSuccess, let code else { return nil }
        var staticCode: SecStaticCode?
        guard SecCodeCopyStaticCode(code, [], &staticCode) == errSecSuccess, let staticCode else { return nil }
        var req: SecRequirement?
        guard SecCodeCopyDesignatedRequirement(staticCode, [], &req) == errSecSuccess, let req else { return nil }
        var text: CFString?
        SecRequirementCopyString(req, [], &text)
        return (req, (text as String?) ?? "")
    }

    public static func runningIsAdHoc() -> Bool {
        guard let (_, text) = runningRequirement() else { return true }
        return text.contains("cdhash") || !text.contains("certificate")
    }

    public static func checkSatisfiesRunningRequirement(_ app: URL) throws {
        guard let (req, text) = runningRequirement() else {
            throw UpdateInstallError.requirementMismatch("the running app has no designated requirement")
        }
        if text.contains("cdhash") || !text.contains("certificate") { throw UpdateInstallError.adHocInstall }
        var staticCode: SecStaticCode?
        guard SecStaticCodeCreateWithPath(app as CFURL, [], &staticCode) == errSecSuccess, let staticCode else {
            throw UpdateInstallError.signatureInvalid("cannot read the new bundle")
        }
        let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSStrictValidate | kSecCSCheckNestedCode)
        let status = SecStaticCodeCheckValidity(staticCode, flags, req)
        guard status == errSecSuccess else {
            let msg = (SecCopyErrorMessageString(status, nil) as String?) ?? "OSStatus \(status)"
            throw UpdateInstallError.requirementMismatch(msg)
        }
    }
}

public enum UpdateInstaller {
    public static func shellQuote(_ s: String) -> String {
        "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    public static func helperScript(pid: Int32, appPath: String, stagedPath: String, backupPath: String, logPath: String) -> String {
        let app = shellQuote(appPath)
        let staged = shellQuote(stagedPath)
        let backup = shellQuote(backupPath)
        let log = shellQuote(logPath)
        return """
        PID=\(pid)
        APP=\(app)
        NEW=\(staged)
        OLD=\(backup)
        exec >>\(log) 2>&1
        echo "$(date) installing update into $APP"
        n=0
        while kill -0 "$PID" 2>/dev/null; do
          n=$((n + 1))
          if [ "$n" -ge 300 ]; then
            echo "the app did not quit, update cancelled"
            exit 1
          fi
          sleep 0.2
        done
        rollback() {
          echo "$1, rolling back"
          if [ -e "$OLD" ]; then
            rm -rf "$APP"
            mv "$OLD" "$APP"
          fi
          /usr/bin/open "$APP"
          exit 1
        }
        rm -rf "$OLD"
        mkdir -p "$(dirname "$OLD")"
        if ! mv "$APP" "$OLD"; then
          echo "cannot move the current app aside"
          /usr/bin/open "$APP"
          exit 1
        fi
        mv "$NEW" "$APP" || rollback "cannot move the new app into place"
        /usr/bin/xattr -dr com.apple.quarantine "$APP" 2>/dev/null
        /usr/bin/codesign --verify --deep --strict "$APP" || rollback "the installed app failed verification"
        rm -rf "$OLD"
        echo "$(date) update installed"
        /usr/bin/open "$APP"
        exit 0

        """
    }

    public static func run(_ tool: String, _ args: [String]) throws -> (Int32, String) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = pipe
        p.standardInput = FileHandle.nullDevice
        try p.run()
        let out = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return (p.terminationStatus, String(decoding: out, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines))
    }

    public static func unzip(_ zip: URL, into dir: URL) throws -> URL {
        try? FileManager.default.removeItem(at: dir)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let (status, out) = try run("/usr/bin/ditto", ["-x", "-k", zip.path, dir.path])
        guard status == 0 else { throw UpdateInstallError.unzipFailed(out.isEmpty ? "ditto exited with \(status)" : out) }
        let items = (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
        guard let app = items.first(where: { $0.pathExtension == "app" }) else { throw UpdateInstallError.bundleMissing }
        return app
    }

    public static func checkBundle(_ app: URL, bundleId: String, version: String) throws {
        guard let info = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist")) as? [String: Any] else {
            throw UpdateInstallError.wrongBundle("Info.plist is missing")
        }
        let id = info["CFBundleIdentifier"] as? String ?? ""
        guard id == bundleId else { throw UpdateInstallError.wrongBundle("bundle identifier \(id)") }
        let v = info["CFBundleShortVersionString"] as? String ?? ""
        guard SemVer(v) != nil, SemVer(v) == SemVer(version) else { throw UpdateInstallError.wrongBundle("version \(v), expected \(version)") }
    }

    public static func verifyCodeSignature(_ app: URL) throws {
        let (status, out) = try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path])
        guard status == 0 else { throw UpdateInstallError.signatureInvalid(out.isEmpty ? "codesign exited with \(status)" : out) }
    }

    public static func checkInstallLocation(_ app: URL) throws {
        let path = app.standardizedFileURL.path
        if path.contains("/AppTranslocation/") { throw UpdateInstallError.translocated }
        let parent = app.deletingLastPathComponent().path
        let fm = FileManager.default
        guard fm.isWritableFile(atPath: parent), fm.isWritableFile(atPath: path),
              fm.isWritableFile(atPath: app.appendingPathComponent("Contents").path) else {
            throw UpdateInstallError.locationNotWritable(parent)
        }
    }

    public static func launchHelper(script: String, scriptURL: URL) throws {
        do {
            try script.write(to: scriptURL, atomically: true, encoding: .utf8)
            chmod(scriptURL.path, 0o700)
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/bin/sh")
            p.arguments = ["-c", "/usr/bin/nohup /bin/sh \(shellQuote(scriptURL.path)) >/dev/null 2>&1 </dev/null &"]
            p.standardInput = FileHandle.nullDevice
            p.standardOutput = FileHandle.nullDevice
            p.standardError = FileHandle.nullDevice
            try p.run()
            p.waitUntilExit()
            guard p.terminationStatus == 0 else { throw UpdateInstallError.helperFailed("sh exited with \(p.terminationStatus)") }
        } catch let e as UpdateInstallError {
            throw e
        } catch {
            throw UpdateInstallError.helperFailed(error.localizedDescription)
        }
    }
}

public struct PreparedUpdate: Sendable, Equatable {
    public var release: LatestRelease
    public var stagedApp: URL

    public init(release: LatestRelease, stagedApp: URL) {
        self.release = release
        self.stagedApp = stagedApp
    }
}

public actor Updater {
    public let currentVersion: String
    public let platform: String
    public let bundleId: String
    public let updatesDir: URL
    private let transport: any HTTPTransport
    private let publicKeyB64: String

    public init(currentVersion: String, bundleId: String, updatesDir: URL, transport: any HTTPTransport, platform: String = UpdatePolicy.platform, publicKeyB64: String = UpdatePolicy.releasePublicKeyB64) {
        self.currentVersion = currentVersion
        self.bundleId = bundleId
        self.updatesDir = updatesDir
        self.transport = transport
        self.platform = platform
        self.publicKeyB64 = publicKeyB64
    }

    public func check(serverURL: URL, token: String) async throws -> (UpdateDecision, LatestRelease?) {
        let api = APIClient(baseURL: serverURL, token: token, transport: transport)
        let latest = try await api.latestRelease(platform: platform)
        return (UpdatePolicy.decide(currentVersion: currentVersion, latest: latest, platform: platform), latest)
    }

    public func cleanUp(keepingLog: Bool = true) {
        let fm = FileManager.default
        let items = (try? fm.contentsOfDirectory(at: updatesDir, includingPropertiesForKeys: nil)) ?? []
        for item in items where !(keepingLog && item.lastPathComponent == "install.log") {
            try? fm.removeItem(at: item)
        }
    }

    public func prepare(_ release: LatestRelease, serverURL: URL, token: String, progress: @escaping @Sendable (Double) -> Void) async throws -> PreparedUpdate {
        let fm = FileManager.default
        guard case .available = UpdatePolicy.decide(currentVersion: currentVersion, latest: release, platform: platform) else {
            throw UpdateInstallError.wrongBundle("release \(release.version) is not installable")
        }
        guard let url = UpdatePolicy.assetURL(base: serverURL, release: release) else {
            throw UpdateInstallError.wrongBundle("invalid download address")
        }
        let dir = updatesDir.appendingPathComponent(release.version, isDirectory: true)
        try? fm.removeItem(at: dir)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let zip = dir.appendingPathComponent(release.asset.file)
        var req = URLRequest(url: url)
        req.setValue("application/octet-stream", forHTTPHeaderField: "Accept")
        if UpdatePolicy.sameOrigin(url, serverURL) {
            req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
        Log.info("Downloading update \(release.version) (\(release.asset.size) bytes)", "update")
        let size = release.asset.size
        try await ReleaseDownloader.download(req, to: zip, expectedSize: size) { done, total in
            let t = total > 0 ? total : size
            progress(t > 0 ? min(1, Double(done) / Double(t)) : 0)
        }
        do {
            try ReleaseVerifier.verify(file: zip, asset: release.asset, publicKeyB64: publicKeyB64)
        } catch {
            try? fm.removeItem(at: dir)
            Log.error("Update \(release.version) failed verification: \(error)", "update")
            throw error
        }
        Log.info("Update \(release.version) checksum and signature verified", "update")
        do {
            let app = try UpdateInstaller.unzip(zip, into: dir.appendingPathComponent("extracted", isDirectory: true))
            try? fm.removeItem(at: zip)
            try UpdateInstaller.checkBundle(app, bundleId: bundleId, version: release.version)
            try UpdateInstaller.verifyCodeSignature(app)
            try CodeSignature.checkSatisfiesRunningRequirement(app)
            Log.info("Update \(release.version) code signature matches this app", "update")
            return PreparedUpdate(release: release, stagedApp: app)
        } catch {
            try? fm.removeItem(at: dir)
            Log.error("Update \(release.version) rejected: \(error)", "update")
            throw error
        }
    }

    public func install(_ prepared: PreparedUpdate, installedApp: URL, pid: Int32) throws {
        try UpdateInstaller.checkInstallLocation(installedApp)
        guard FileManager.default.fileExists(atPath: prepared.stagedApp.path) else { throw UpdateInstallError.bundleMissing }
        let backup = updatesDir
            .appendingPathComponent("previous-\(currentVersion)", isDirectory: true)
            .appendingPathComponent(installedApp.lastPathComponent)
        let script = UpdateInstaller.helperScript(
            pid: pid,
            appPath: installedApp.standardizedFileURL.path,
            stagedPath: prepared.stagedApp.path,
            backupPath: backup.path,
            logPath: updatesDir.appendingPathComponent("install.log").path
        )
        try UpdateInstaller.launchHelper(script: script, scriptURL: updatesDir.appendingPathComponent("install.sh"))
        Log.info("Update helper started for \(prepared.release.version)", "update")
    }
}
