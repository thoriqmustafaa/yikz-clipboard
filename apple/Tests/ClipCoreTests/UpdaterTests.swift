import Foundation
import Testing
@testable import ClipCore

@Suite("Updater")
struct UpdaterTests {
    struct SignatureVector {
        let publicKey: String
        let file: Data
        let sha256: String
        let signature: String

        static func load() throws -> SignatureVector {
            let o = try Vectors.object("release_signature.json")
            return SignatureVector(
                publicKey: o.str("public_key_b64"),
                file: Data(base64Encoded: o.str("file_b64")) ?? Data(),
                sha256: o.str("sha256_hex"),
                signature: o.str("signature_b64")
            )
        }
    }

    @Test func releaseSignatureVectorVerifies() throws {
        let v = try SignatureVector.load()
        #expect(ReleaseVerifier.sha256(of: v.file).hex == v.sha256)
        try ReleaseVerifier.verify(data: v.file, sha256Hex: v.sha256, signatureB64: v.signature, publicKeyB64: v.publicKey)
    }

    @Test func releaseSignatureVectorVerifiesFromFile() throws {
        let v = try SignatureVector.load()
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("yikz-sig-\(UUID().uuidString).zip")
        try v.file.write(to: url)
        defer { try? FileManager.default.removeItem(at: url) }
        let asset = ReleaseAsset(platform: "macos", file: "x.zip", size: Int64(v.file.count), sha256: v.sha256, signature: v.signature)
        try ReleaseVerifier.verify(file: url, asset: asset, publicKeyB64: v.publicKey)
        let wrongSize = ReleaseAsset(platform: "macos", file: "x.zip", size: Int64(v.file.count + 1), sha256: v.sha256, signature: v.signature)
        #expect(throws: ReleaseVerificationError.sizeMismatch(expected: Int64(v.file.count + 1), actual: Int64(v.file.count))) {
            try ReleaseVerifier.verify(file: url, asset: wrongSize, publicKeyB64: v.publicKey)
        }
    }

    @Test func tamperedFileFailsChecksum() throws {
        let v = try SignatureVector.load()
        for i in [0, v.file.count / 2, v.file.count - 1] {
            var bytes = v.file
            bytes[i] ^= 0x01
            #expect(throws: ReleaseVerificationError.checksumMismatch) {
                try ReleaseVerifier.verify(data: bytes, sha256Hex: v.sha256, signatureB64: v.signature, publicKeyB64: v.publicKey)
            }
        }
    }

    @Test func tamperedFileWithMatchingChecksumFailsSignature() throws {
        let v = try SignatureVector.load()
        var bytes = v.file
        bytes[0] ^= 0x01
        let forgedHex = ReleaseVerifier.sha256(of: bytes).hex
        #expect(throws: ReleaseVerificationError.signatureMismatch) {
            try ReleaseVerifier.verify(data: bytes, sha256Hex: forgedHex, signatureB64: v.signature, publicKeyB64: v.publicKey)
        }
    }

    @Test func tamperedSignatureFails() throws {
        let v = try SignatureVector.load()
        let sig = Data(base64Encoded: v.signature)!
        for i in [0, 31, 63] {
            var s = sig
            s[i] ^= 0x01
            #expect(throws: ReleaseVerificationError.signatureMismatch) {
                try ReleaseVerifier.verify(data: v.file, sha256Hex: v.sha256, signatureB64: s.base64EncodedString(), publicKeyB64: v.publicKey)
            }
        }
    }

    @Test func wrongKeyFails() throws {
        let v = try SignatureVector.load()
        #expect(throws: ReleaseVerificationError.signatureMismatch) {
            try ReleaseVerifier.verify(data: v.file, sha256Hex: v.sha256, signatureB64: v.signature, publicKeyB64: UpdatePolicy.releasePublicKeyB64)
        }
    }

    @Test func malformedInputsAreRejected() throws {
        let v = try SignatureVector.load()
        #expect(throws: ReleaseVerificationError.invalidSignatureEncoding) {
            try ReleaseVerifier.verify(data: v.file, sha256Hex: v.sha256, signatureB64: "AAAA", publicKeyB64: v.publicKey)
        }
        #expect(throws: ReleaseVerificationError.invalidChecksumEncoding) {
            try ReleaseVerifier.verify(data: v.file, sha256Hex: "zz", signatureB64: v.signature, publicKeyB64: v.publicKey)
        }
        #expect(throws: ReleaseVerificationError.invalidPublicKey) {
            try ReleaseVerifier.verify(data: v.file, sha256Hex: v.sha256, signatureB64: v.signature, publicKeyB64: "AAAA")
        }
    }

    @Test func embeddedReleaseKeyIsValid() {
        let raw = Data(base64Encoded: UpdatePolicy.releasePublicKeyB64)
        #expect(raw?.count == 32)
    }

    @Test func semverParsingAndOrdering() {
        #expect(SemVer("1.2.3") == SemVer(major: 1, minor: 2, patch: 3))
        #expect(SemVer("v1.2.3") == SemVer(major: 1, minor: 2, patch: 3))
        #expect(SemVer(" 1.0.0\n") == SemVer(major: 1, minor: 0, patch: 0))
        #expect(SemVer("1.2.3-beta.1") == SemVer(major: 1, minor: 2, patch: 3))
        #expect(SemVer("1.2") == nil)
        #expect(SemVer("1.2.3.4") == nil)
        #expect(SemVer("1.x.3") == nil)
        #expect(SemVer("") == nil)
        #expect(SemVer("dev") == nil)
        #expect(SemVer("1..3") == nil)
        #expect(SemVer("-1.0.0") == nil)
        #expect(SemVer("1.10.0")! > SemVer("1.9.9")!)
        #expect(SemVer("2.0.0")! > SemVer("1.99.99")!)
        #expect(SemVer("1.0.10")! > SemVer("1.0.9")!)
        #expect(SemVer("1.1.0")! == SemVer("1.1.0")!)
        #expect(!(SemVer("1.1.0")! < SemVer("1.1.0")!))
        #expect(SemVer("1.1.0")!.buildNumber == 10100)
        #expect(SemVer("2.13.7")!.buildNumber == 21307)
        #expect(SemVer("1.1.0")!.description == "1.1.0")
    }

    static let latestJSON = """
    {
      "version": "1.1.0",
      "published_at": "2026-09-22T10:00:00.000Z",
      "notes_md": "### macOS\\n- Dock icon toggle\\n",
      "asset": {
        "platform": "macos",
        "file": "YikzClipboard-1.1.0-macos.zip",
        "size": 2400000,
        "sha256": "979c9f8f4fa29fe42adad7a79ccba083a4ee7faf2d9b8d22a26b0097dc08df60",
        "signature": "GQaoiK5wfcLPxW5w9GLFrOODSd0KRdG3ga9/iT0jQX0LXdPXcr1S3Ud231ebMyxNSTWlh6PCPyFq/F5BSU5PAg==",
        "url": "/api/releases/1.1.0/assets/YikzClipboard-1.1.0-macos.zip"
      }
    }
    """

    func release(_ version: String = "1.1.0", platform: String = "macos", file: String? = nil) throws -> LatestRelease {
        var r = try LatestRelease.decode(Data(Self.latestJSON.utf8))
        r.version = version
        r.asset.platform = platform
        r.asset.file = file ?? "YikzClipboard-\(version)-macos.zip"
        return r
    }

    @Test func decodesLatestRelease() throws {
        let r = try LatestRelease.decode(Data(Self.latestJSON.utf8))
        #expect(r.version == "1.1.0")
        #expect(r.publishedAt == Timestamp.parse("2026-09-22T10:00:00.000Z"))
        #expect(r.publishedAt != nil)
        #expect(r.notesMd == "### macOS\n- Dock icon toggle\n")
        #expect(r.asset.platform == "macos")
        #expect(r.asset.file == "YikzClipboard-1.1.0-macos.zip")
        #expect(r.asset.size == 2_400_000)
        #expect(r.asset.sha256.count == 64)
        #expect(r.asset.url == "/api/releases/1.1.0/assets/YikzClipboard-1.1.0-macos.zip")
    }

    @Test func decodesLatestReleaseWithoutOptionalFields() throws {
        let json = #"{"version":"1.2.0","asset":{"platform":"macos","file":"a.zip","size":1,"sha256":"00","signature":"AA=="},"extra":true}"#
        let r = try LatestRelease.decode(Data(json.utf8))
        #expect(r.version == "1.2.0")
        #expect(r.notesMd == "")
        #expect(r.publishedAt == nil)
        #expect(r.asset.url == nil)
    }

    @Test func decisionNewerEqualOlder() throws {
        let r = try release("1.1.0")
        #expect(UpdatePolicy.decide(currentVersion: "1.0.0", latest: r) == .available(r))
        #expect(UpdatePolicy.decide(currentVersion: "1.0.9", latest: r) == .available(r))
        #expect(UpdatePolicy.decide(currentVersion: "1.1.0", latest: r) == .upToDate)
        #expect(UpdatePolicy.decide(currentVersion: "1.2.0", latest: r) == .upToDate)
        #expect(UpdatePolicy.decide(currentVersion: "2.0.0", latest: r) == .upToDate)
        #expect(UpdatePolicy.decide(currentVersion: "1.0.0", latest: nil) == .upToDate)
        let r2 = try release("1.10.0")
        #expect(UpdatePolicy.decide(currentVersion: "1.9.0", latest: r2) == .available(r2))
    }

    @Test func decisionRejectsWrongPlatform() throws {
        for p in ["android", "windows-x64", "windows-arm64", "macOS", ""] {
            let r = try release("1.1.0", platform: p)
            guard case .rejected = UpdatePolicy.decide(currentVersion: "1.0.0", latest: r) else {
                Issue.record("platform \(p) was accepted")
                continue
            }
        }
        let r = try release("1.1.0", platform: "windows-x64")
        #expect(UpdatePolicy.decide(currentVersion: "1.0.0", latest: r, platform: "windows-x64") == .available(r))
    }

    @Test func decisionRejectsInvalidData() throws {
        func rejected(_ d: UpdateDecision) -> Bool {
            if case .rejected = d { return true }
            return false
        }
        #expect(rejected(UpdatePolicy.decide(currentVersion: "dev", latest: try release("1.1.0"))))
        #expect(rejected(UpdatePolicy.decide(currentVersion: "1.0.0", latest: try release("1.1"))))
        #expect(rejected(UpdatePolicy.decide(currentVersion: "1.0.0", latest: try release("1.1.0", file: "../evil.zip"))))
        #expect(rejected(UpdatePolicy.decide(currentVersion: "1.0.0", latest: try release("1.1.0", file: "a/b.zip"))))
        var bad = try release("1.1.0")
        bad.asset.sha256 = "abc"
        #expect(rejected(UpdatePolicy.decide(currentVersion: "1.0.0", latest: bad)))
        bad = try release("1.1.0")
        bad.asset.signature = "AAAA"
        #expect(rejected(UpdatePolicy.decide(currentVersion: "1.0.0", latest: bad)))
        bad = try release("1.1.0")
        bad.asset.size = 0
        #expect(rejected(UpdatePolicy.decide(currentVersion: "1.0.0", latest: bad)))
    }

    @Test func assetURLResolution() throws {
        let r = try release("1.1.0")
        let base = URL(string: "https://clip.yikz.dev")!
        #expect(UpdatePolicy.assetURL(base: base, release: r)?.absoluteString == "https://clip.yikz.dev/api/releases/1.1.0/assets/YikzClipboard-1.1.0-macos.zip")
        let prefixed = URL(string: "https://example.com/clip/")!
        #expect(UpdatePolicy.assetURL(base: prefixed, release: r)?.absoluteString == "https://example.com/clip/api/releases/1.1.0/assets/YikzClipboard-1.1.0-macos.zip")
        var noURL = r
        noURL.asset.url = nil
        #expect(UpdatePolicy.assetURL(base: base, release: noURL)?.absoluteString == "https://clip.yikz.dev/api/releases/1.1.0/assets/YikzClipboard-1.1.0-macos.zip")
        var abs = r
        abs.asset.url = "https://cdn.example.com/x.zip"
        let absURL = UpdatePolicy.assetURL(base: base, release: abs)!
        #expect(absURL.absoluteString == "https://cdn.example.com/x.zip")
        #expect(!UpdatePolicy.sameOrigin(absURL, base))
        #expect(UpdatePolicy.sameOrigin(UpdatePolicy.assetURL(base: base, release: r)!, base))
        var ftp = r
        ftp.asset.url = "file:///etc/passwd"
        #expect(UpdatePolicy.assetURL(base: base, release: ftp) == nil)
    }

    @Test func latestReleaseEndpoint() async throws {
        let transport = ScriptedTransport { req in
            #expect(req.url?.path == "/api/releases/latest")
            #expect(req.url?.query == "platform=macos")
            #expect(req.value(forHTTPHeaderField: "Authorization") == "Bearer tok")
            return (200, Data(UpdaterTests.latestJSON.utf8))
        }
        let api = APIClient(baseURL: URL(string: "https://clip.yikz.dev")!, token: "tok", transport: transport)
        let r = try await api.latestRelease(platform: "macos")
        #expect(r?.version == "1.1.0")
        let empty = APIClient(baseURL: URL(string: "https://clip.yikz.dev")!, token: "tok", transport: ScriptedTransport { _ in (204, Data()) })
        #expect(try await empty.latestRelease(platform: "macos") == nil)
    }

    @Test func releaseAvailableMessage() throws {
        #expect(try ServerMessage.decode(#"{"type":"release_available","version":"1.1.0"}"#) == .releaseAvailable("1.1.0"))
        #expect(try ServerMessage.decode(#"{"type":"release_available"}"#) == .releaseAvailable(""))
    }

    @Test func shellQuoting() {
        #expect(UpdateInstaller.shellQuote("/Applications/YikzClipboard.app") == "'/Applications/YikzClipboard.app'")
        #expect(UpdateInstaller.shellQuote("a b") == "'a b'")
        #expect(UpdateInstaller.shellQuote("it's") == #"'it'\''s'"#)
        #expect(UpdateInstaller.shellQuote("$(rm -rf ~)`x`\"") == "'$(rm -rf ~)`x`\"'")
    }

    @Test func helperScriptContainsQuotedPaths() {
        let app = "/Users/o'brien/My Apps/Yikz Clipboard.app"
        let staged = "/Users/o'brien/Library/Caches/dev.yikz.clipboard/updates/1.1.0/extracted/YikzClipboard.app"
        let backup = "/Users/o'brien/Library/Caches/dev.yikz.clipboard/updates/previous-1.0.0/Yikz Clipboard.app"
        let log = "/Users/o'brien/Library/Caches/dev.yikz.clipboard/updates/install.log"
        let s = UpdateInstaller.helperScript(pid: 4242, appPath: app, stagedPath: staged, backupPath: backup, logPath: log)
        #expect(s.contains("PID=4242\n"))
        #expect(s.contains("APP='/Users/o'\\''brien/My Apps/Yikz Clipboard.app'\n"))
        #expect(s.contains("NEW='/Users/o'\\''brien/Library/Caches/dev.yikz.clipboard/updates/1.1.0/extracted/YikzClipboard.app'\n"))
        #expect(s.contains("OLD='/Users/o'\\''brien/Library/Caches/dev.yikz.clipboard/updates/previous-1.0.0/Yikz Clipboard.app'\n"))
        #expect(s.contains("exec >>'/Users/o'\\''brien/Library/Caches/dev.yikz.clipboard/updates/install.log' 2>&1"))
        #expect(s.contains(#"kill -0 "$PID""#))
        #expect(s.contains(#"mv "$APP" "$OLD""#))
        #expect(s.contains(#"mv "$NEW" "$APP""#))
        #expect(s.contains(#"mv "$OLD" "$APP""#))
        #expect(s.contains(#"/usr/bin/xattr -dr com.apple.quarantine "$APP""#))
        #expect(s.contains(#"/usr/bin/codesign --verify --deep --strict "$APP""#))
        #expect(s.contains(#"/usr/bin/open "$APP""#))
        #expect(!s.contains("#"))
        #expect(!s.contains("\u{2014}"))
        let unquoted = s.components(separatedBy: "\n").filter { $0.hasPrefix("APP=") || $0.hasPrefix("NEW=") || $0.hasPrefix("OLD=") }
        #expect(unquoted.count == 3)
        #expect(unquoted.allSatisfy { $0.dropFirst(4).hasPrefix("'") && $0.hasSuffix("'") })
    }

    @Test func helperScriptIsValidShell() throws {
        let s = UpdateInstaller.helperScript(pid: 1, appPath: "/tmp/a b.app", stagedPath: "/tmp/it's.app", backupPath: "/tmp/$(x).app", logPath: "/tmp/l.log")
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("yikz-helper-\(UUID().uuidString).sh")
        try s.write(to: url, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: url) }
        let (status, out) = try UpdateInstaller.run("/bin/sh", ["-n", url.path])
        #expect(status == 0, "\(out)")
    }

    @Test func releaseNotesParsing() {
        let md = "### macOS\n- Dock icon **toggle**\n- Uses `ditto`\n\n### All\nSee [site](https://clip.yikz.dev)\n"
        let blocks = ReleaseNotes.parse(md)
        #expect(blocks == [
            .heading("macOS"),
            .bullet("Dock icon **toggle**"),
            .bullet("Uses `ditto`"),
            .heading("All"),
            .paragraph("See [site](https://clip.yikz.dev)")
        ])
        let a = ReleaseNotes.inline("Dock icon **toggle** and [site](https://clip.yikz.dev)")
        #expect(String(a.characters) == "Dock icon toggle and site")
        #expect(a.runs.contains { $0.link == URL(string: "https://clip.yikz.dev") })
    }

    @Test func installLocationChecks() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("yikz-loc-\(UUID().uuidString)", isDirectory: true)
        let app = dir.appendingPathComponent("YikzClipboard.app", isDirectory: true)
        try FileManager.default.createDirectory(at: app.appendingPathComponent("Contents"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        try UpdateInstaller.checkInstallLocation(app)
        #expect(throws: UpdateInstallError.translocated) {
            try UpdateInstaller.checkInstallLocation(URL(fileURLWithPath: "/private/var/folders/x/AppTranslocation/ABC/d/YikzClipboard.app"))
        }
        #expect(throws: UpdateInstallError.locationNotWritable("/System/Applications")) {
            try UpdateInstaller.checkInstallLocation(URL(fileURLWithPath: "/System/Applications/Calculator.app"))
        }
    }
}

struct ScriptedTransport: HTTPTransport {
    let handler: @Sendable (URLRequest) -> (Int, Data)

    init(_ handler: @escaping @Sendable (URLRequest) -> (Int, Data)) {
        self.handler = handler
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let (status, body) = handler(request)
        let resp = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: "HTTP/1.1", headerFields: nil)!
        return (body, resp)
    }
}
