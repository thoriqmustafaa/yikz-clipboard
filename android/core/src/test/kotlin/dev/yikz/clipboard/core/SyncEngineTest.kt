package dev.yikz.clipboard.core

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class SyncEngineTest {
    private val t0 = 1_789_984_800_000L

    private class Harness(val scope: TestScope, val nowMs: Long) {
        val api = FakeApi { nowMs }.apply { keyCheck = Fixtures.key.keyCheck }
        val store = MemoryStore()
        val applier = RecordingApplier()
        val echo = EchoGuard()
        var keyInvalid = false
        var unauthorized = false
        val engine = SyncEngine(
            api = api,
            store = store,
            codec = Fixtures.codec,
            ownDeviceId = Fixtures.OWN,
            saltB64 = api.salt,
            applier = applier,
            echo = echo,
            scope = scope,
            clock = { nowMs },
            callbacks = object : SyncCallbacks {
                override fun onKeyInvalid() {
                    keyInvalid = true
                }

                override fun onUnauthorized() {
                    unauthorized = true
                }
            },
        )

        fun welcome(serverId: String = api.serverId) = WelcomeMsg(
            protocolVersion = 1,
            serverId = serverId,
            serverTime = Timestamps.format(nowMs),
            deviceId = Fixtures.OWN,
            currentSeq = api.seq,
            stateRev = api.stateRev,
        )

        fun serverAdd(text: String, device: String = Fixtures.OTHER, createdMs: Long = nowMs): ItemHeader =
            api.add(Fixtures.textHeader(text, device).copy(createdAt = Timestamps.format(createdMs)))
    }

    @Test
    fun initialSyncLoadsHistoryWithoutApplying() = runTest {
        val h = Harness(this, t0)
        repeat(150) { h.serverAdd("item $it") }
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        assertEquals(150, h.store.items.size)
        assertEquals(150L, h.store.syncState.lastSeq)
        assertEquals(150L, h.store.syncState.appliedSeq)
        assertEquals(h.api.stateRev, h.store.syncState.stateRev)
        assertEquals(h.api.serverId, h.store.syncState.serverId)
        assertTrue(h.applier.written.isEmpty())
        assertTrue(h.api.calls.contains("history before=151 after=null limit=100"))
        assertTrue(h.api.calls.contains("history before=51 after=null limit=100"))
    }

    @Test
    fun catchUpPagesAfterLastSeqAndAppliesNewestEligible() = runTest {
        val h = Harness(this, t0)
        repeat(3) { h.serverAdd("old $it", createdMs = t0 - 3_600_000) }
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        h.engine.onDisconnected()
        repeat(600) { h.serverAdd("offline $it", createdMs = t0 - 3_600_000) }
        val fresh = h.serverAdd("fresh from mac", createdMs = t0 - 60_000)
        h.serverAdd("mine", device = Fixtures.OWN, createdMs = t0 - 10_000)
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        assertTrue(h.api.calls.contains("history before=null after=3 limit=500"))
        assertTrue(h.api.calls.contains("history before=null after=503 limit=500"))
        assertEquals(605, h.store.items.size)
        assertEquals(605L, h.store.syncState.lastSeq)
        assertEquals(605L, h.store.syncState.appliedSeq)
        assertEquals(listOf(fresh.id), h.applier.written)
    }

    @Test
    fun catchUpDoesNotApplyStaleItems() = runTest {
        val h = Harness(this, t0)
        h.serverAdd("seed", createdMs = t0 - 7_200_000)
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        h.serverAdd("overnight", createdMs = t0 - 301_000)
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        assertTrue(h.applier.written.isEmpty())
        assertEquals(2L, h.store.syncState.appliedSeq)
    }

    @Test
    fun liveClipsAreAppliedAndAdvanceSeq() = runTest {
        val h = Harness(this, t0)
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        val a = h.serverAdd("live a")
        h.engine.onMessage(ClipMsg(item = a))
        advanceUntilIdle()
        val own = h.serverAdd("own", device = Fixtures.OWN)
        h.engine.onMessage(ClipMsg(item = own))
        val stale = h.serverAdd("stale", createdMs = t0 - 400_000)
        h.engine.onMessage(ClipMsg(item = stale))
        advanceUntilIdle()
        assertEquals(listOf(a.id), h.applier.written)
        assertEquals(3L, h.store.syncState.lastSeq)
        assertEquals(3L, h.store.syncState.appliedSeq)
        assertTrue(a.contentHash in h.echo)
        assertTrue(own.contentHash in h.echo)
        assertEquals(h.store.items[a.id]?.payload, a.payload)
    }

    @Test
    fun disabledKindsAndLargeItemsAreNotWritten() = runTest {
        val h = Harness(this, t0)
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        val big = h.api.add(Fixtures.chunkedHeader(60_000_000).copy(createdAt = Timestamps.format(t0)))
        h.engine.onMessage(ClipMsg(item = big))
        advanceUntilIdle()
        assertEquals(listOf(big.id), h.applier.offered)
        h.applier.disabledKinds += Kind.TEXT
        val text = h.serverAdd("nope")
        h.engine.onMessage(ClipMsg(item = text))
        advanceUntilIdle()
        assertTrue(h.applier.written.isEmpty())
    }

    @Test
    fun serverIdChangeClearsCache() = runTest {
        val h = Harness(this, t0)
        repeat(5) { h.serverAdd("before $it") }
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        assertEquals(5, h.store.items.size)
        h.api.committed.clear()
        h.api.seq = 0
        h.api.stateRev = 0
        h.api.keyCheck = null
        h.api.serverId = "01a05bfb-7000-7691-98e4-301030971d0d"
        h.serverAdd("after wipe")
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        assertEquals(1, h.store.items.size)
        assertEquals("01a05bfb-7000-7691-98e4-301030971d0d", h.store.syncState.serverId)
        assertEquals(1L, h.store.syncState.lastSeq)
        assertEquals(Fixtures.key.keyCheck, h.api.keyCheck)
        assertFalse(h.keyInvalid)
    }

    @Test
    fun serverIdChangeWithDifferentSaltInvalidatesKey() = runTest {
        val h = Harness(this, t0)
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        h.api.serverId = "01a05bfb-7000-7691-98e4-301030971d0d"
        h.api.salt = "ABEiM0RVZneImaq7zN3u/w=="
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        assertTrue(h.keyInvalid)
    }

    @Test
    fun reconcileRemovesDeletedAndUpdatesPins() = runTest {
        val h = Harness(this, t0)
        val items = (1..5).map { h.serverAdd("r $it") }
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        h.engine.onDisconnected()
        h.api.delete(listOf(items[0].id, items[1].id))
        h.api.setPin(items[2].id, true)
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        assertEquals(setOf(items[2].id, items[3].id, items[4].id), h.store.items.keys)
        assertTrue(h.store.items.getValue(items[2].id).pinned)
        assertEquals(h.api.stateRev, h.store.syncState.stateRev)
        assertTrue(h.api.calls.contains("index"))
    }

    @Test
    fun liveStateRevHandling() = runTest {
        val h = Harness(this, t0)
        val items = (1..4).map { h.serverAdd("s $it") }
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        val indexCalls = h.api.calls.count { it == "index" }
        h.api.delete(listOf(items[0].id))
        h.engine.onMessage(ClipDeletedMsg(ids = listOf(items[0].id), reason = "user", stateRev = h.api.stateRev))
        advanceUntilIdle()
        assertEquals(h.api.stateRev, h.store.syncState.stateRev)
        assertEquals(indexCalls, h.api.calls.count { it == "index" })
        h.api.setPin(items[1].id, true)
        h.engine.onMessage(ClipPinnedMsg(id = items[1].id, pinned = true, stateRev = h.api.stateRev))
        advanceUntilIdle()
        assertTrue(h.store.items.getValue(items[1].id).pinned)
        h.engine.onMessage(ClipPinnedMsg(id = items[1].id, pinned = true, stateRev = h.api.stateRev - 1))
        advanceUntilIdle()
        assertEquals(indexCalls, h.api.calls.count { it == "index" })
        h.api.delete(listOf(items[2].id))
        h.api.delete(listOf(items[3].id))
        h.engine.onMessage(ClipDeletedMsg(ids = listOf(items[3].id), reason = "retention", stateRev = h.api.stateRev))
        advanceUntilIdle()
        assertEquals(indexCalls + 1, h.api.calls.count { it == "index" })
        assertEquals(setOf(items[1].id), h.store.items.keys)
        assertEquals(h.api.stateRev, h.store.syncState.stateRev)
    }

    @Test
    fun reconcileKeepsItemsNewerThanIndex() = runTest {
        val store = MemoryStore()
        val a = Fixtures.codec.decode(Fixtures.textHeader("a").copy(seq = 1))
        val b = Fixtures.codec.decode(Fixtures.textHeader("b").copy(seq = 5))
        store.upsert(listOf(a, b))
        store.reconcile(emptyMap(), upToSeq = 4)
        assertEquals(setOf(b.id), store.items.keys)
    }

    @Test
    fun loadOlderPagesBackwards() = runTest {
        val h = Harness(this, t0)
        repeat(1200) { h.serverAdd("bulk $it") }
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        assertEquals(1000, h.store.items.size)
        assertTrue(h.engine.loadOlder())
        assertEquals(1100, h.store.items.size)
        assertFalse(h.engine.loadOlder())
        assertEquals(1200, h.store.items.size)
    }

    @Test
    fun catchUpRetriesAfterTransientFailure() = runTest {
        val h = Harness(this, t0)
        h.serverAdd("x")
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        h.serverAdd("y")
        h.api.transientFailures["history"] = 2
        h.engine.onWelcome(h.welcome())
        advanceUntilIdle()
        assertEquals(2, h.store.items.size)
        assertEquals(2L, h.store.syncState.lastSeq)
    }
}
