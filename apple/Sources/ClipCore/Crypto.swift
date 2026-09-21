import Foundation
import CryptoKit
import CommonCrypto

public enum CryptoError: Error, Sendable, Equatable, CustomStringConvertible {
    case invalidKey
    case invalidNonce
    case tooShort
    case authenticationFailed
    case kdfFailed(Int32)
    case hashMismatch
    case contentHashMismatch

    public var description: String {
        switch self {
        case .invalidKey: return "invalid key"
        case .invalidNonce: return "invalid nonce"
        case .tooShort: return "sealed box shorter than 28 bytes"
        case .authenticationFailed: return "authentication failed"
        case .kdfFailed(let s): return "key derivation failed (\(s))"
        case .hashMismatch: return "sha256 mismatch"
        case .contentHashMismatch: return "content_hash mismatch"
        }
    }
}

public struct MasterKey: Sendable, Equatable {
    public let data: Data

    public init?(data: Data) {
        guard data.count == Proto.keyLength else { return nil }
        self.data = data
    }

    var symmetric: SymmetricKey { SymmetricKey(data: data) }

    public static func normalizedPasswordBytes(_ password: String) -> Data {
        Data(password.precomposedStringWithCanonicalMapping.utf8)
    }

    public static func derive(password: String, salt: Data, iterations: UInt32 = Proto.kdfIterations) throws -> MasterKey {
        let pw = normalizedPasswordBytes(password)
        var derived = [UInt8](repeating: 0, count: Proto.keyLength)
        let status: Int32 = pw.withUnsafeBytes { pwBuf in
            salt.withUnsafeBytes { saltBuf in
                CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2),
                    pwBuf.baseAddress?.assumingMemoryBound(to: CChar.self),
                    pwBuf.count,
                    saltBuf.baseAddress?.assumingMemoryBound(to: UInt8.self),
                    saltBuf.count,
                    CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256),
                    iterations,
                    &derived,
                    derived.count
                )
            }
        }
        guard status == Int32(kCCSuccess) else { throw CryptoError.kdfFailed(status) }
        return MasterKey(data: Data(derived))!
    }

    public var contentHashKey: Data {
        Data(HMAC<SHA256>.authenticationCode(for: Data(Proto.contentHashLabel.utf8), using: symmetric))
    }

    public var keyCheck: String {
        Data(HMAC<SHA256>.authenticationCode(for: Data(Proto.keyCheckLabel.utf8), using: symmetric)).hex
    }

    public func contentHash(_ content: Data) -> String {
        Data(HMAC<SHA256>.authenticationCode(for: content, using: SymmetricKey(data: contentHashKey))).hex
    }

    public func digester() -> ContentDigester {
        ContentDigester(contentHashKey: contentHashKey)
    }
}

public struct ContentDigest: Sendable, Equatable {
    public let contentHash: String
    public let sha256: String
    public let size: Int64
}

public struct ContentDigester {
    private var hmac: HMAC<SHA256>
    private var sha = SHA256()
    private var size: Int64 = 0

    init(contentHashKey: Data) {
        hmac = HMAC<SHA256>(key: SymmetricKey(data: contentHashKey))
    }

    public mutating func update(_ data: Data) {
        hmac.update(data: data)
        sha.update(data: data)
        size += Int64(data.count)
    }

    public func finalize() -> ContentDigest {
        ContentDigest(
            contentHash: Data(hmac.finalize()).hex,
            sha256: Data(sha.finalize()).hex,
            size: size
        )
    }
}

public enum AAD {
    public static func meta(_ id: String) -> Data { Data("yc1|meta|\(id)".utf8) }
    public static func payload(_ id: String) -> Data { Data("yc1|payload|\(id)".utf8) }
    public static func thumb(_ id: String) -> Data { Data("yc1|thumb|\(id)".utf8) }
    public static func chunk(_ id: String, index: Int, count: Int) -> Data {
        Data("yc1|chunk|\(id)|\(index)|\(count)".utf8)
    }
}

public enum AEAD {
    public static func seal(_ plaintext: Data, key: MasterKey, aad: Data, nonce: Data? = nil) throws -> Data {
        let n: AES.GCM.Nonce
        if let nonce {
            guard nonce.count == Proto.nonceLength, let v = try? AES.GCM.Nonce(data: nonce) else {
                throw CryptoError.invalidNonce
            }
            n = v
        } else {
            n = AES.GCM.Nonce()
        }
        let box = try AES.GCM.seal(plaintext, using: key.symmetric, nonce: n, authenticating: aad)
        guard let combined = box.combined else { throw CryptoError.invalidNonce }
        return combined
    }

    public static func open(_ sealed: Data, key: MasterKey, aad: Data) throws -> Data {
        guard sealed.count >= Proto.sealOverhead else { throw CryptoError.tooShort }
        do {
            let box = try AES.GCM.SealedBox(combined: sealed)
            return try AES.GCM.open(box, using: key.symmetric, authenticating: aad)
        } catch {
            throw CryptoError.authenticationFailed
        }
    }

    public static func sha256Hex(_ data: Data) -> String {
        Data(SHA256.hash(data: data)).hex
    }

    public static func sha256Hex(ascii string: String) -> String {
        sha256Hex(Data(string.utf8))
    }
}
