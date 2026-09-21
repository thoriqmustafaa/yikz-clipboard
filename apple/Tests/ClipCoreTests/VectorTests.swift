import Foundation
import Testing
import CryptoKit
@testable import ClipCore

@Suite("Protocol vectors")
struct VectorTests {
    @Test func kdfFastAndUnicode() throws {
        let v = try Vectors.object("kdf.json")
        #expect(v.str("algorithm") == Proto.kdfAlgorithm)
        #expect(v.int("key_length") == Proto.keyLength)
        for c in v.list("cases") where c.int("iterations") < 100_000 {
            let input = c.str("password_input")
            #expect(MasterKey.normalizedPasswordBytes(input) == c.hex("password_utf8_hex"), "\(c.str("name"))")
            #expect(Data(strictBase64: c.str("salt_b64")) == c.hex("salt_hex"))
            let k = try MasterKey.derive(password: input, salt: c.hex("salt_hex"), iterations: UInt32(c.int("iterations")))
            #expect(k.data.hex == c.str("key_hex"), "\(c.str("name"))")
        }
    }

    @Test func kdfPrimary600k() throws {
        let v = try Vectors.object("kdf.json")
        let c = try #require(v.list("cases").first { $0.str("name") == "primary" })
        #expect(c.int("iterations") == 600_000)
        let k = try MasterKey.derive(password: c.str("password_input"), salt: Data(strictBase64: c.str("salt_b64"))!)
        #expect(k.data.hex == c.str("key_hex"))
    }

    @Test func subkeysAndContentHash() throws {
        let v = try Vectors.object("keys.json")
        #expect(v.str("content_hash_label_utf8") == Proto.contentHashLabel)
        #expect(v.str("key_check_label_utf8") == Proto.keyCheckLabel)
        for k in v.list("keys") {
            let mk = key(k.str("key_hex"))
            #expect(mk.contentHashKey.hex == k.str("content_hash_key_hex"))
            #expect(mk.keyCheck == k.str("key_check"))
            for s in k.list("content_hash_samples") {
                let content = s.hex("content_hex")
                #expect(Data(s.str("content_utf8").utf8) == content)
                #expect(mk.contentHash(content) == s.str("content_hash"))
                #expect(AEAD.sha256Hex(content) == s.str("sha256"))
                var d = mk.digester()
                d.update(content.prefix(1))
                d.update(content.dropFirst())
                let digest = d.finalize()
                #expect(digest.contentHash == s.str("content_hash"))
                #expect(digest.sha256 == s.str("sha256"))
                #expect(digest.size == Int64(content.count))
            }
        }
    }

    @Test func aeadPositive() throws {
        let v = try Vectors.object("aead.json")
        let formats = v.dict("aad_formats")
        #expect(formats.str("meta") == "yc1|meta|<item_id>")
        #expect(formats.str("payload") == "yc1|payload|<item_id>")
        #expect(formats.str("thumb") == "yc1|thumb|<item_id>")
        #expect(formats.str("chunk") == "yc1|chunk|<item_id>|<index>|<chunk_count>")
        let positives = v.list("positive")
        #expect(positives.count >= 5)
        for p in positives {
            let name = p.str("name")
            let aad = p.str("aad_utf8")
            #expect(Data(aad.utf8) == p.hex("aad_hex"), "\(name)")
            let parts = aad.split(separator: "|").map(String.init)
            let built: Data
            switch p.str("purpose") {
            case "meta": built = AAD.meta(parts[2])
            case "payload": built = AAD.payload(parts[2])
            case "thumb": built = AAD.thumb(parts[2])
            case "chunk": built = AAD.chunk(parts[2], index: Int(parts[3])!, count: Int(parts[4])!)
            default:
                Issue.record("unknown purpose \(p.str("purpose"))")
                continue
            }
            #expect(built == Data(aad.utf8), "\(name)")
            let k = key(p.str("key_hex"))
            let sealed = try AEAD.seal(p.hex("plaintext_hex"), key: k, aad: built, nonce: p.hex("nonce_hex"))
            #expect(sealed.hex == p.str("sealed_hex"), "\(name)")
            #expect(sealed.base64EncodedString() == p.str("sealed_b64"), "\(name)")
            #expect(sealed.count == p.int("sealed_length"), "\(name)")
            let opened = try AEAD.open(Data(strictBase64: p.str("sealed_b64"))!, key: k, aad: built)
            #expect(opened == p.hex("plaintext_hex"), "\(name)")
        }
        let purposes = Set(positives.map { $0.str("purpose") })
        #expect(purposes == ["meta", "payload", "thumb", "chunk"])
    }

    @Test func aeadNegative() throws {
        let v = try Vectors.object("aead.json")
        for n in v.list("negative") {
            #expect(n.str("expect") == "decrypt_error")
            let sealed = Data(strictBase64: n.str("sealed_b64")) ?? n.hex("sealed_hex")
            #expect(throws: CryptoError.self, "\(n.str("name"))") {
                try AEAD.open(sealed, key: key(n.str("key_hex")), aad: n.hex("aad_hex"))
            }
        }
    }

    @Test func randomNonceRoundTrip() throws {
        let k = key(fastKeyHex)
        let a = try AEAD.seal(Data("x".utf8), key: k, aad: AAD.meta("id"))
        let b = try AEAD.seal(Data("x".utf8), key: k, aad: AAD.meta("id"))
        #expect(a.count == 29)
        #expect(a.prefix(12) != b.prefix(12))
        #expect(try AEAD.open(a, key: k, aad: AAD.meta("id")) == Data("x".utf8))
        #expect(throws: CryptoError.tooShort) { try AEAD.open(Data(count: 27), key: k, aad: Data()) }
    }

    @Test func uuidv7() throws {
        let v = try Vectors.object("uuidv7.json")
        #expect(v.str("regex") == UUIDv7.pattern)
        let regex = try Regex(v.str("regex"))
        for g in v.list("generate") {
            let id = UUIDv7.generate(unixMs: UInt64(g.int64("unix_ms")), random: [UInt8](g.hex("random_hex")))
            #expect(id == g.str("expected"))
        }
        for s in v["valid"] as? [String] ?? [] {
            #expect(UUIDv7.isValid(s), "\(s)")
        }
        for i in v.list("invalid") {
            #expect(!UUIDv7.isValid(i.str("value")), "\(i.str("reason"))")
        }
        for _ in 0..<200 {
            let id = UUIDv7.generate()
            #expect(UUIDv7.isValid(id))
            #expect(id.wholeMatch(of: regex) != nil)
        }
        let now = Date()
        let id = UUIDv7.generate(now: now)
        let msHex = id.replacingOccurrences(of: "-", with: "").prefix(12)
        let ms = UInt64(msHex, radix: 16)!
        #expect(ms == UInt64((now.timeIntervalSince1970 * 1000).rounded(.down)))
    }

    @Test func tokens() throws {
        let v = try Vectors.object("token.json")
        for c in v.list("cases") {
            #expect(DeviceToken.isValid(c.str("token")))
            #expect(AEAD.sha256Hex(ascii: c.str("token")) == c.str("token_hash"))
            let random = c.hex("random_hex").base64EncodedString()
                .replacingOccurrences(of: "+", with: "-")
                .replacingOccurrences(of: "/", with: "_")
                .replacingOccurrences(of: "=", with: "")
            #expect("yc_" + random == c.str("token"))
        }
        #expect(!DeviceToken.isValid("yc_short"))
    }

    @Test func filesArchiveThreeFiles() throws {
        let v = try Vectors.object("files_archive.json")
        let c = v.list("cases")[0]
        #expect(c.str("name") == "three_files")
        let entries = c.list("files").map { YCF1.Entry(name: $0.str("name"), data: $0.hex("data_hex")) }
        for f in c.list("files") {
            #expect(Data(f.str("name").utf8) == f.hex("name_utf8_hex"))
        }
        let encoded = try YCF1.encode(entries)
        #expect(encoded.hex == c.str("archive_hex"))
        #expect(encoded.count == c.int("archive_size"))
        #expect(AEAD.sha256Hex(encoded) == c.str("archive_sha256"))
        #expect(try YCF1.decode(encoded) == entries)
        #expect(try YCF1.listing(encoded).map(\.size) == [14, 10, 0])

        #expect(throws: ArchiveError.trailingBytes) { try YCF1.decode(encoded + Data([0])) }
        #expect(throws: ArchiveError.truncated) { try YCF1.decode(encoded.dropLast()) }
        var zero = YCF1.magic
        zero.append(contentsOf: [0, 0, 0, 0])
        #expect(throws: ArchiveError.badFileCount(0)) { try YCF1.decode(zero) }
        var many = YCF1.magic
        many.append(contentsOf: [0, 0, 0x03, 0xe9])
        #expect(throws: ArchiveError.badFileCount(1001)) { try YCF1.decode(many) }
        #expect(throws: ArchiveError.badMagic) { try YCF1.decode(Data("YCF2".utf8) + Data(count: 4)) }

        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("ycf-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: tmp) }
        let src = tmp.appendingPathComponent("src")
        try FileManager.default.createDirectory(at: src, withIntermediateDirectories: true)
        var sources: [YCF1.SourceFile] = []
        for e in entries {
            let u = src.appendingPathComponent(UUID().uuidString)
            try e.data.write(to: u)
            sources.append(YCF1.SourceFile(name: e.name, url: u))
        }
        let archiveURL = tmp.appendingPathComponent("a.ycf")
        let infos = try YCF1.write(files: sources, to: archiveURL)
        #expect(infos.map(\.name) == entries.map(\.name))
        #expect(try Data(contentsOf: archiveURL) == encoded)
        let out = try YCF1.extract(archive: archiveURL, into: tmp.appendingPathComponent("out"))
        #expect(out.map(\.lastPathComponent) == entries.map(\.name))
        for (u, e) in zip(out, entries) {
            #expect(try Data(contentsOf: u) == e.data)
        }
    }

    @Test func filesArchiveLargeHeader() throws {
        let v = try Vectors.object("files_archive.json")
        let c = v.list("cases")[1]
        let f = c.list("files")[0]
        let size = f.int("size")
        let data = largePattern(size)
        let encoded = try YCF1.encode([YCF1.Entry(name: f.str("name"), data: data)])
        #expect(encoded.prefix(c.hex("archive_prefix_hex").count) == c.hex("archive_prefix_hex"))
        #expect(encoded.count == c.int("archive_size"))
        #expect(AEAD.sha256Hex(encoded) == c.str("archive_sha256"))
    }

    @Test func filesArchiveInvalidNames() throws {
        let v = try Vectors.object("files_archive.json")
        for n in v.list("invalid_names") {
            #expect(throws: ArchiveError.self, "\(n.str("reason"))") { try YCF1.validateName(n.str("name")) }
            #expect(throws: ArchiveError.self) { try YCF1.encode([YCF1.Entry(name: n.str("name"), data: Data())]) }
        }
        #expect(throws: ArchiveError.self) { try YCF1.validateName("e\u{0301}.txt") }
        try YCF1.validateName("\u{00e9}.txt")
        #expect(throws: ArchiveError.duplicateName("a")) {
            try YCF1.encode([YCF1.Entry(name: "a", data: Data()), YCF1.Entry(name: "a", data: Data())])
        }
        #expect(YCF1.uniqueNames(["a.txt", "a.txt", "b/c"]) == ["a.txt", "a (2).txt", "b_c"])
    }

    @Test func chunking() throws {
        let v = try Vectors.object("chunking.json")
        #expect(Int64(v.int("inline_max_bytes")) == Proto.inlineMaxBytes)
        #expect(Int64(v.int("chunk_size_bytes")) == Proto.chunkSizeBytes)
        #expect(v.int("seal_overhead_bytes") == Proto.sealOverhead)
        for c in v.list("cases") {
            let plan = ChunkPlan(size: c.int64("size"))
            #expect(plan.isInline == c.bool("inline"), "size \(c.int64("size"))")
            #expect(plan.chunkCount == c.int("chunk_count"))
            if plan.isInline {
                #expect(plan.inlineSealedSize == c.int64("payload_sealed_length"))
            } else {
                let last = c.int("last_chunk_index")
                #expect(last == plan.chunkCount - 1)
                #expect(plan.plaintextSize(of: 0) == min(c.int64("full_chunk_plaintext_size"), plan.size))
                #expect(plan.plaintextSize(of: last) == c.int64("last_chunk_plaintext_size"))
                #expect(plan.sealedSize(of: last) == c.int64("last_chunk_sealed_length"))
                if last > 0 { #expect(plan.sealedSize(of: 0) == c.int64("full_chunk_sealed_length")) }
                #expect(plan.totalSealedChunkBytes == c.int64("total_chunk_sealed_bytes"))
                var total: Int64 = 0
                for i in 0..<plan.chunkCount { total += plan.plaintextSize(of: i) }
                #expect(total == plan.size)
            }
        }
    }

    @Test func preview() throws {
        let v = try Vectors.object("preview.json")
        #expect(v.int("max_code_points") == Proto.previewMaxCodePoints)
        for c in v.list("cases") {
            let input = c.str("input")
            #expect(input.unicodeScalars.count == c.int("input_code_points"))
            #expect(input.utf16.count == c.int("input_utf16_units"))
            let p = Preview.make(input)
            #expect(p == c.str("expected"), "\(c.str("name"))")
            #expect(p.unicodeScalars.count == c.int("expected_code_points"))
            #expect(Array(p.unicodeScalars) == Array(c.str("expected").unicodeScalars))
        }
    }

    @Test func itemsDecryptAndSeal() throws {
        let v = try Vectors.object("items.json")
        let items = v.list("items")
        #expect(items.map { $0.str("name") } == ["text", "image", "files", "large"])
        for it in items {
            let name = it.str("name")
            let k = key(it.str("key_hex"))
            let id = it.str("id")
            #expect(UUIDv7.isValid(id))
            let header = try JSONCoding.decoder().decode(ItemHeader.self, from: try jsonData(it.dict("header")))
            #expect(header.id == id)
            #expect(header.kind.rawValue == it.str("kind"))
            #expect(header.size == it.int64("size"))
            #expect(header.chunkCount == it.int("chunk_count"))
            #expect(header.contentHash == it.str("content_hash"))
            #expect(header.storedBytes == it.int64("stored_bytes"))
            #expect(ChunkPlan(size: header.size).chunkCount == header.chunkCount)

            #expect(AAD.meta(id) == Data(it.str("meta_aad_utf8").utf8))
            let metaSealed = try #require(Data(strictBase64: it.str("meta_sealed_b64")))
            #expect(header.meta == it.str("meta_sealed_b64"))
            let metaPlain = try AEAD.open(metaSealed, key: k, aad: AAD.meta(id))
            #expect(metaPlain == Data(it.str("meta_plaintext_utf8").utf8), "\(name)")
            let reseal = try AEAD.seal(metaPlain, key: k, aad: AAD.meta(id), nonce: it.hex("meta_nonce_hex"))
            #expect(reseal == metaSealed, "\(name)")
            let meta = try ItemMeta.decode(metaPlain)
            #expect(meta.v == 1)
            #expect(meta.sha256 == it.str("content_sha256"))
            let expectedMeta = it.dict("meta")
            #expect(meta.mime == expectedMeta.str("mime"))
            #expect(meta.preview == expectedMeta.str("preview"))
            #expect(meta.sourceApp == expectedMeta["source_app"] as? String)
            #expect(jsonEqual(try meta.encoded(), expectedMeta), "\(name) meta re-encoding")

            let content: Data
            if name == "large" {
                let f = try Vectors.object("files_archive.json").list("cases")[1].list("files")[0]
                content = try YCF1.encode([YCF1.Entry(name: f.str("name"), data: largePattern(f.int("size")))])
            } else {
                content = it.hex("content_hex")
            }
            #expect(Int64(content.count) == header.size)
            #expect(AEAD.sha256Hex(content) == it.str("content_sha256"))
            #expect(k.contentHash(content) == it.str("content_hash"))

            if header.isInline {
                #expect(AAD.payload(id) == Data(it.str("payload_aad_utf8").utf8))
                let payloadSealed = try #require(Data(strictBase64: it.str("payload_sealed_b64")))
                #expect(header.payload == it.str("payload_sealed_b64"))
                #expect(try AEAD.open(payloadSealed, key: k, aad: AAD.payload(id)) == content)
                #expect(try AEAD.seal(content, key: k, aad: AAD.payload(id), nonce: it.hex("payload_nonce_hex")) == payloadSealed)
            } else {
                let chunks = it.list("chunks")
                #expect(chunks.count == header.chunkCount)
                let plan = ChunkPlan(size: header.size)
                for c in chunks {
                    let i = c.int("index")
                    let aad = AAD.chunk(id, index: i, count: header.chunkCount)
                    #expect(aad == Data(c.str("aad_utf8").utf8))
                    let r = plan.range(of: i)
                    let plain = content.subdata(in: Int(r.lowerBound)..<Int(r.upperBound))
                    #expect(Int64(plain.count) == c.int64("plaintext_size"))
                    #expect(AEAD.sha256Hex(plain) == c.str("plaintext_sha256"))
                    let sealed = try AEAD.seal(plain, key: k, aad: aad, nonce: c.hex("nonce_hex"))
                    #expect(sealed.count == c.int("sealed_size"))
                    #expect(AEAD.sha256Hex(sealed) == c.str("sealed_sha256"))
                    #expect(sealed.prefix(44) == c.hex("sealed_prefix_hex"))
                    #expect(sealed.suffix(32) == c.hex("sealed_suffix_hex"))
                    #expect(try AEAD.open(sealed, key: k, aad: aad) == plain)
                }
            }

            if name == "image" {
                #expect(meta.image == ImageDimensions(width: 4, height: 3))
                #expect(header.hasThumb)
                let thumbSealed = try #require(Data(strictBase64: it.str("thumb_sealed_b64")))
                #expect(AAD.thumb(id) == Data(it.str("thumb_aad_utf8").utf8))
                #expect(try AEAD.open(thumbSealed, key: k, aad: AAD.thumb(id)) == it.hex("thumb_plaintext_hex"))
                #expect(try AEAD.seal(it.hex("thumb_plaintext_hex"), key: k, aad: AAD.thumb(id), nonce: it.hex("thumb_nonce_hex")) == thumbSealed)
            }
            if name == "files" {
                #expect(header.pinned)
                #expect(meta.files?.map(\.name) == ["hello.txt", "données é.bin", "empty.txt"])
                #expect(try YCF1.listing(content) == meta.files)
                #expect(meta.preview == Preview.forFiles(meta.files!.map(\.name)))
            }
        }
    }
}

func largePattern(_ size: Int) -> Data {
    var bytes = [UInt8](repeating: 0, count: size)
    for i in 0..<size { bytes[i] = UInt8(i % 251) }
    return Data(bytes)
}
