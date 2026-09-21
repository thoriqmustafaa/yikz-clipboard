package dev.yikz.clipboard.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.random.Random

class LogicTest {
    @Test
    fun reconnectBackoffDoublesToThirtySeconds() {
        val ceilings = (0..8).map { Backoff.reconnectCeilingMs(it) }
        assertEquals(listOf(500L, 1000L, 2000L, 4000L, 8000L, 16000L, 30000L, 30000L, 30000L), ceilings)
        assertEquals(30_000L, Backoff.reconnectCeilingMs(1000))
        val random = Random(42)
        repeat(1000) {
            val attempt = it % 12
            val d = Backoff.reconnectDelayMs(attempt, random)
            assertTrue(d >= 0 && d <= Backoff.reconnectCeilingMs(attempt))
        }
        val spread = (0 until 200).map { Backoff.reconnectDelayMs(10, random) }
        assertTrue(spread.min() < 5_000 && spread.max() > 25_000)
    }

    @Test
    fun httpBackoffDoublesToSixtySeconds() {
        assertEquals(listOf(1000L, 2000L, 4000L, 8000L, 16000L, 32000L, 60000L, 60000L), (0..7).map { Backoff.httpRetryCeilingMs(it) })
    }

    private fun cached(seq: Long, device: String = Fixtures.OTHER, createdMs: Long = 1_000_000L, chunkCount: Int = 0, size: Long = 10) = CachedItem(
        id = Uuid7.generate(), seq = seq, deviceId = device, kind = Kind.TEXT, size = size, chunkCount = chunkCount,
        createdAt = Timestamps.format(createdMs), createdAtMs = createdMs, pinned = false, contentHash = "h$seq",
        hasThumb = false, storedBytes = 0, sealedMeta = "", meta = null, metaError = null,
    )

    @Test
    fun autoApplyEligibility() {
        val now = 1_000_000L
        assertTrue(AutoApply.isEligible(cached(5, createdMs = now - 299_000), Fixtures.OWN, 4, now))
        assertTrue(AutoApply.isEligible(cached(5, createdMs = now - 300_000), Fixtures.OWN, 4, now))
        assertFalse(AutoApply.isEligible(cached(5, createdMs = now - 300_001), Fixtures.OWN, 4, now))
        assertFalse(AutoApply.isEligible(cached(5, device = Fixtures.OWN, createdMs = now), Fixtures.OWN, 4, now))
        assertFalse(AutoApply.isEligible(cached(4, createdMs = now), Fixtures.OWN, 4, now))
        val items = listOf(cached(6, createdMs = now), cached(8, device = Fixtures.OWN, createdMs = now), cached(7, createdMs = now), cached(9, createdMs = now - 400_000))
        assertEquals(7L, AutoApply.pickAfterCatchUp(items, Fixtures.OWN, 5, now)?.seq)
        assertEquals(null, AutoApply.pickAfterCatchUp(items, Fixtures.OWN, 9, now))
    }

    @Test
    fun autoApplyActionByThreshold() {
        assertEquals(ApplyAction.WRITE_INLINE, AutoApply.action(cached(1), 10))
        assertEquals(ApplyAction.DOWNLOAD_AND_WRITE, AutoApply.action(cached(1, chunkCount = 3, size = 10_000_000), 52_428_800))
        assertEquals(ApplyAction.DOWNLOAD_AND_WRITE, AutoApply.action(cached(1, chunkCount = 13, size = 52_428_800), 52_428_800))
        assertEquals(ApplyAction.OFFER_DOWNLOAD, AutoApply.action(cached(1, chunkCount = 13, size = 52_428_801), 52_428_800))
    }

    @Test
    fun echoGuardKeepsLast32() {
        val guard = EchoGuard()
        (1..40).forEach { guard.add("h$it") }
        assertFalse("h8" in guard)
        assertTrue("h9" in guard)
        assertTrue("h40" in guard)
        guard.add("h9")
        (41..71).forEach { guard.add("h$it") }
        assertTrue("h9" in guard)
        assertFalse("h10" in guard)
    }

    @Test
    fun echoRules() {
        val key = Fixtures.key
        val guard = EchoGuard()
        val content = "hello\r\nworld".toByteArray()
        val hash = key.contentHash(content)
        assertEquals(UploadDecision.Upload(hash), EchoRules.decide(key, content, Kind.TEXT, guard, null))
        assertEquals(UploadDecision.Skip("sensitive"), EchoRules.decide(key, content, Kind.TEXT, guard, null, sensitive = true))
        assertEquals(UploadDecision.Skip("own write"), EchoRules.decide(key, content, Kind.TEXT, guard, null, hasOriginMarker = true))
        assertEquals(UploadDecision.Skip("empty"), EchoRules.decide(key, ByteArray(0), Kind.TEXT, guard, null))
        assertEquals(UploadDecision.Skip("already current"), EchoRules.decide(key, content, Kind.TEXT, guard, hash))
        guard.add(key.contentHash("hello\nworld".toByteArray()))
        assertEquals(UploadDecision.Skip("recently synced"), EchoRules.decide(key, content, Kind.TEXT, guard, null))
        val guard2 = EchoGuard().apply { add(hash) }
        assertEquals(UploadDecision.Skip("recently synced"), EchoRules.decide(key, content, Kind.TEXT, guard2, null))
        assertEquals(null, EchoRules.normalizedTextHash(key, content, Kind.FILES))
    }
}
