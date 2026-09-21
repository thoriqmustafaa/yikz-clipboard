import Foundation
import Testing
@testable import ClipCore

@Suite("Pure logic")
struct LogicTests {
    @Test func reconnectBackoffSchedule() {
        let b = Backoff.reconnect
        let ceilings = (0..<10).map { b.ceiling(attempt: $0) }
        #expect(ceilings == [0.5, 1, 2, 4, 8, 16, 30, 30, 30, 30])
        #expect(b.delay(attempt: 3, unit: 0) == 0)
        #expect(b.delay(attempt: 3, unit: 0.5) == 2)
        #expect(b.delay(attempt: 50, unit: 1) == 30)
        for attempt in 0..<12 {
            for _ in 0..<50 {
                let d = b.delay(attempt: attempt)
                #expect(d >= 0 && d <= b.ceiling(attempt: attempt))
            }
        }
        let http = Backoff.http
        #expect((0..<8).map { http.ceiling(attempt: $0) } == [1, 2, 4, 8, 16, 32, 60, 60])
    }

    func header(seq: Int64, device: String, age: TimeInterval, now: Date) -> ItemHeader {
        ItemHeader(id: UUIDv7.generate(), seq: seq, deviceId: device, kind: .text, size: 1, chunkCount: 0, createdAt: now.addingTimeInterval(-age), pinned: false, contentHash: String(repeating: "0", count: 64), hasThumb: false, storedBytes: 29, meta: "")
    }

    @Test func autoApplyEligibility() {
        let now = Date(timeIntervalSince1970: 1_790_000_000)
        let own = "own"
        #expect(AutoApply.isEligible(header(seq: 5, device: "other", age: 10, now: now), ownDeviceId: own, appliedSeq: 4, localNow: now, offset: 0))
        #expect(!AutoApply.isEligible(header(seq: 5, device: own, age: 10, now: now), ownDeviceId: own, appliedSeq: 4, localNow: now, offset: 0))
        #expect(!AutoApply.isEligible(header(seq: 4, device: "other", age: 10, now: now), ownDeviceId: own, appliedSeq: 4, localNow: now, offset: 0))
        #expect(AutoApply.isEligible(header(seq: 5, device: "other", age: 300, now: now), ownDeviceId: own, appliedSeq: 4, localNow: now, offset: 0))
        #expect(!AutoApply.isEligible(header(seq: 5, device: "other", age: 301, now: now), ownDeviceId: own, appliedSeq: 4, localNow: now, offset: 0))
        let item = header(seq: 5, device: "other", age: 200, now: now)
        #expect(!AutoApply.isEligible(item, ownDeviceId: own, appliedSeq: 4, localNow: now, offset: 120))
        #expect(AutoApply.isEligible(item, ownDeviceId: own, appliedSeq: 4, localNow: now.addingTimeInterval(1000), offset: -1000))
    }

    @Test func autoApplyAfterCatchUpPicksNewestEligible() {
        let now = Date(timeIntervalSince1970: 1_790_000_000)
        let items = [
            header(seq: 11, device: "a", age: 30, now: now),
            header(seq: 12, device: "b", age: 20, now: now),
            header(seq: 13, device: "own", age: 5, now: now),
            header(seq: 10, device: "a", age: 3600, now: now)
        ]
        let r = AutoApply.afterCatchUp(items, ownDeviceId: "own", appliedSeq: 9, localNow: now, offset: 0)
        #expect(r.apply?.seq == 12)
        #expect(r.appliedSeq == 13)
        let none = AutoApply.afterCatchUp([header(seq: 20, device: "a", age: 900, now: now)], ownDeviceId: "own", appliedSeq: 9, localNow: now, offset: 0)
        #expect(none.apply == nil)
        #expect(none.appliedSeq == 20)
        let empty = AutoApply.afterCatchUp([], ownDeviceId: "own", appliedSeq: 9, localNow: now, offset: 0)
        #expect(empty.apply == nil)
        #expect(empty.appliedSeq == 9)
    }

    @Test func recentHashSet() {
        var r = RecentHashes()
        for i in 0..<40 { r.insert("h\(i)") }
        #expect(r.hashes.count == 32)
        #expect(!r.contains("h0"))
        #expect(!r.contains("h7"))
        #expect(r.contains("h8"))
        #expect(r.contains("h39"))
        r.insert("h8")
        #expect(r.hashes.last == "h8")
        #expect(r.hashes.count == 32)
    }

    @Test func echoDecision() {
        var r = RecentHashes()
        r.insert("applied")
        #expect(EchoGuard.decide(contentHash: "applied", normalizedTextHash: nil, recent: r, newestCachedHash: nil) == .skipRecent)
        #expect(EchoGuard.decide(contentHash: "crlf", normalizedTextHash: "applied", recent: r, newestCachedHash: nil) == .skipRecent)
        #expect(EchoGuard.decide(contentHash: "newest", normalizedTextHash: nil, recent: r, newestCachedHash: "newest") == .skipNewest)
        #expect(EchoGuard.decide(contentHash: "fresh", normalizedTextHash: nil, recent: r, newestCachedHash: "newest") == .upload)
        #expect(EchoGuard.normalizedCRLF("a\r\nb\r\n") == "a\nb\n")
        #expect(EchoGuard.normalizedCRLF("a\rb") == "a\rb")
    }

    @Test func linkDetection() {
        #expect(HistoryEntry.linkURL(in: " https://yikz.dev/path?q=1 ") != nil)
        #expect(HistoryEntry.linkURL(in: "see https://yikz.dev") == nil)
        #expect(HistoryEntry.linkURL(in: "yikz.dev") == nil)
        #expect(HistoryEntry.linkURL(in: "mailto:a@b.c") != nil)
    }

    @Test func historyStoreRoundTrip() throws {
        let store = try HistoryStore(path: ":memory:")
        let k = key(fastKeyHex)
        let now = Date(timeIntervalSince1970: 1_790_000_000)
        let a = FakeServer.makeItem(key: k, text: "alpha", seq: 1, device: "d", createdAt: now)
        let b = FakeServer.makeItem(key: k, text: "beta", seq: 2, device: "d", createdAt: now, pinned: true)
        let meta = ItemMeta(mime: Proto.textMime, preview: "alpha", sha256: "x")
        try store.upsert(a, meta: meta, metaState: .ok, payloadSealed: Data(strictBase64: a.payload!))
        try store.upsert(b, meta: nil, metaState: .corrupt, payloadSealed: nil)
        #expect(store.allEntries().map(\.seq) == [2, 1])
        #expect(store.entry(id: a.id)?.meta == meta)
        #expect(store.entry(id: a.id)?.hasCachedPayload == true)
        #expect(store.entry(id: b.id)?.metaState == .corrupt)
        #expect(store.newestContentHash() == b.contentHash)
        #expect(store.minSeq() == 1)
        try store.upsert(a, meta: meta, metaState: .ok, payloadSealed: nil)
        #expect(store.payloadSealed(id: a.id) == Data(strictBase64: a.payload!))
        store.setPinned(id: a.id, pinned: true)
        #expect(store.entry(id: a.id)?.pinned == true)
        #expect(store.header(id: b.id)?.pinned == true)
        store.delete(ids: [a.id])
        #expect(store.count() == 1)
        store.clear()
        #expect(store.count() == 0)
    }

    @Test func pruneReceivedCache() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("yikz-prune-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let paths = AppPaths(support: root.appendingPathComponent("s"), caches: root.appendingPathComponent("c"))
        paths.prepare()
        let fm = FileManager.default
        let old = paths.received.appendingPathComponent("old")
        let fresh = paths.received.appendingPathComponent("fresh")
        for d in [old, fresh] {
            try fm.createDirectory(at: d, withIntermediateDirectories: true)
            try Data(count: 1000).write(to: d.appendingPathComponent("f.bin"))
        }
        try fm.setAttributes([.modificationDate: Date().addingTimeInterval(-8 * 86400)], ofItemAtPath: old.path)
        paths.pruneReceived()
        #expect(!fm.fileExists(atPath: old.path))
        #expect(fm.fileExists(atPath: fresh.path))
        paths.pruneReceived(maxBytes: 10)
        #expect(!fm.fileExists(atPath: fresh.path))
    }
}
