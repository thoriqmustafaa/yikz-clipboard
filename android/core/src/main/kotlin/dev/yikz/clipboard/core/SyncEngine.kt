package dev.yikz.clipboard.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlin.random.Random

interface SyncHandler {
    suspend fun onWelcome(welcome: WelcomeMsg)
    suspend fun onMessage(message: WsMessage)
    suspend fun onDisconnected()
}

interface PreparedClip {
    suspend fun write()
}

interface ItemApplier {
    fun autoDownloadLimit(): Long
    fun isKindEnabled(kind: String): Boolean
    suspend fun prepare(item: CachedItem): PreparedClip?
    fun offerDownload(item: CachedItem)
}

interface SyncCallbacks {
    fun onItemsChanged() {}
    fun onUnauthorized() {}
    fun onKeyInvalid() {}
    fun onStorageWarning(message: StorageWarningMsg) {}
    fun onDevicesChanged() {}
    fun onApplied(item: CachedItem) {}
    fun onReleaseAvailable(version: String) {}
}

class SyncEngine(
    private val api: SyncApi,
    private val store: SyncStore,
    private val codec: ItemCodec,
    private val ownDeviceId: String,
    private val saltB64: String,
    private val applier: ItemApplier,
    private val echo: EchoGuard,
    private val scope: CoroutineScope,
    private val clock: () -> Long = System::currentTimeMillis,
    private val log: Logger = NoLog,
    private val callbacks: SyncCallbacks = object : SyncCallbacks {},
    private val initialHistoryTarget: Int = 1000,
    private val random: Random = Random.Default,
) : SyncHandler {
    private val mutex = Mutex()
    private val reconcileMutex = Mutex()
    private var catchingUp = false
    private val liveDuringCatchUp = ArrayList<CachedItem>()
    private var catchUpJob: Job? = null
    private var highestWrittenSeq = 0L
    private var reconciling = false
    private val revsDuringReconcile = HashSet<Long>()
    private val pinsDuringReconcile = ArrayList<ClipPinnedMsg>()
    private val _syncing = MutableStateFlow(false)

    @Volatile
    var clockOffsetMs: Long = 0
        private set

    val syncing: StateFlow<Boolean> = _syncing.asStateFlow()

    fun serverNow(): Long = clock() + clockOffsetMs

    override suspend fun onWelcome(welcome: WelcomeMsg) {
        catchUpJob?.cancelAndJoin()
        mutex.withLock {
            Timestamps.parseOrNull(welcome.serverTime)?.let { clockOffsetMs = it - clock() }
            catchingUp = true
            liveDuringCatchUp.clear()
        }
        _syncing.value = true
        catchUpJob = scope.launch { catchUpWithRetry(welcome) }
    }

    override suspend fun onDisconnected() {
        catchUpJob?.cancelAndJoin()
        catchUpJob = null
        mutex.withLock {
            catchingUp = false
            liveDuringCatchUp.clear()
        }
        _syncing.value = false
    }

    override suspend fun onMessage(message: WsMessage) {
        when (message) {
            is ClipMsg -> onClip(message.item)
            is ClipDeletedMsg -> onDeleted(message)
            is ClipPinnedMsg -> onPinned(message)
            is StorageWarningMsg -> callbacks.onStorageWarning(message)
            is DevicesChangedMsg -> callbacks.onDevicesChanged()
            is ReleaseAvailableMsg -> callbacks.onReleaseAvailable(message.version)
            else -> Unit
        }
    }

    suspend fun awaitCatchUp() {
        catchUpJob?.join()
    }

    private suspend fun catchUpWithRetry(welcome: WelcomeMsg) {
        var attempt = 0
        while (true) {
            try {
                runCatchUp(welcome)
                return
            } catch (e: CancellationException) {
                throw e
            } catch (e: ApiException) {
                if (e.isUnauthorized) {
                    callbacks.onUnauthorized()
                    return
                }
                log.w(TAG, "catch-up failed: ${e.code} ${e.message}")
            } catch (e: StopSync) {
                return
            } catch (e: Exception) {
                log.e(TAG, "catch-up failed", e)
            }
            delay(Backoff.httpRetryDelayMs(attempt++, random))
        }
    }

    private class StopSync : Exception()

    private suspend fun runCatchUp(welcome: WelcomeMsg) {
        var state = mutex.withLock { store.state() }
        if (state.serverId != null && state.serverId != welcome.serverId) {
            log.w(TAG, "server id changed, clearing local cache")
            mutex.withLock {
                store.clearAll()
                state = SyncState(serverId = welcome.serverId)
                store.saveState(state)
                highestWrittenSeq = 0
            }
            callbacks.onItemsChanged()
            if (!verifyKeyAgainstServer()) {
                callbacks.onKeyInvalid()
                throw StopSync()
            }
        } else if (state.serverId == null) {
            mutex.withLock {
                state = store.state().copy(serverId = welcome.serverId)
                store.saveState(state)
            }
        }

        val initial = state.lastSeq == 0L
        val inserted = ArrayList<CachedItem>()
        var cursor = state.lastSeq
        if (initial) {
            var before = welcome.currentSeq + 1
            var count = 0
            while (welcome.currentSeq > 0) {
                val page = api.history(before = before, limit = Protocol.HISTORY_DEFAULT_LIMIT)
                val items = page.items.map(codec::decode)
                mutex.withLock { store.upsert(items) }
                items.forEach { echo.add(it.contentHash) }
                callbacks.onItemsChanged()
                count += items.size
                if (!page.hasMore || items.isEmpty() || count >= initialHistoryTarget) break
                before = items.minOf { it.seq }
            }
            cursor = welcome.currentSeq
            log.i(TAG, "initial sync loaded $count items")
        } else if (welcome.currentSeq > cursor) {
            while (true) {
                val page = api.history(after = cursor, limit = Protocol.HISTORY_MAX_LIMIT)
                val items = page.items.map(codec::decode)
                mutex.withLock { store.upsert(items) }
                items.forEach { echo.add(it.contentHash) }
                inserted += items
                if (items.isNotEmpty()) cursor = items.maxOf { it.seq }
                callbacks.onItemsChanged()
                if (!page.hasMore || items.isEmpty()) break
            }
            log.i(TAG, "catch-up loaded ${inserted.size} items after seq ${state.lastSeq}")
        }

        if (welcome.stateRev != state.stateRev) reconcile()

        val pick = mutex.withLock {
            val current = store.state()
            val live = liveDuringCatchUp.toList()
            val candidates = if (initial) live else inserted + live
            val maxSeen = maxOf(candidates.maxOfOrNull { it.seq } ?: 0L, if (initial) welcome.currentSeq else 0L)
            val chosen = AutoApply.pickAfterCatchUp(candidates, ownDeviceId, current.appliedSeq, serverNow())
            store.saveState(
                current.copy(
                    lastSeq = maxOf(current.lastSeq, cursor, live.maxOfOrNull { it.seq } ?: 0L),
                    appliedSeq = maxOf(current.appliedSeq, maxSeen),
                ),
            )
            catchingUp = false
            liveDuringCatchUp.clear()
            chosen
        }
        _syncing.value = false
        pick?.let { launchApply(it) }
    }

    private suspend fun verifyKeyAgainstServer(): Boolean {
        val me = api.me()
        if (me.salt != saltB64) return false
        val serverCheck = me.keyCheck
        if (serverCheck == null) {
            try {
                api.putKeyCheck(codec.key.keyCheck)
                return true
            } catch (e: ApiException) {
                if (e.code != "key_check_exists") throw e
                return api.me().keyCheck == codec.key.keyCheck
            }
        }
        return serverCheck == codec.key.keyCheck
    }

    suspend fun reconcile() {
        reconcileMutex.withLock {
            mutex.withLock {
                reconciling = true
                revsDuringReconcile.clear()
                pinsDuringReconcile.clear()
            }
            try {
                val index = api.historyIndex()
                mutex.withLock {
                    store.reconcile(index.items.associate { it.id to it.pinned }, index.currentSeq)
                    for (pin in pinsDuringReconcile) {
                        if (pin.stateRev > index.stateRev) store.setPinned(pin.id, pin.pinned)
                    }
                    var rev = index.stateRev
                    while (rev + 1 in revsDuringReconcile) rev++
                    store.saveState(store.state().copy(stateRev = rev))
                }
                log.i(TAG, "reconciled with state_rev ${index.stateRev}, ${index.items.size} items on server")
                callbacks.onItemsChanged()
            } finally {
                mutex.withLock {
                    reconciling = false
                    revsDuringReconcile.clear()
                    pinsDuringReconcile.clear()
                }
            }
        }
    }

    private suspend fun onClip(header: ItemHeader) {
        val item = codec.decode(header)
        if (item.metaError != null) log.e(TAG, "cannot decrypt meta of ${item.id}: ${item.metaError}")
        echo.add(item.contentHash)
        val toApply = mutex.withLock {
            store.upsert(listOf(item))
            if (catchingUp) {
                liveDuringCatchUp += item
                null
            } else {
                val state = store.state()
                val eligible = AutoApply.isEligible(item, ownDeviceId, state.appliedSeq, serverNow())
                store.saveState(
                    state.copy(
                        lastSeq = maxOf(state.lastSeq, item.seq),
                        appliedSeq = maxOf(state.appliedSeq, item.seq),
                    ),
                )
                if (eligible) item else null
            }
        }
        callbacks.onItemsChanged()
        toApply?.let { launchApply(it) }
    }

    private suspend fun onDeleted(message: ClipDeletedMsg) {
        val needReconcile = mutex.withLock {
            store.delete(message.ids)
            if (reconciling) revsDuringReconcile += message.stateRev
            handleRev(message.stateRev)
        }
        callbacks.onItemsChanged()
        if (needReconcile) launchReconcile()
    }

    private suspend fun onPinned(message: ClipPinnedMsg) {
        val needReconcile = mutex.withLock {
            store.setPinned(message.id, message.pinned)
            if (reconciling) {
                revsDuringReconcile += message.stateRev
                pinsDuringReconcile += message
            }
            handleRev(message.stateRev)
        }
        callbacks.onItemsChanged()
        if (needReconcile) launchReconcile()
    }

    private fun handleRev(rev: Long): Boolean {
        val state = store.state()
        val current = state.stateRev
        return when {
            current != null && rev == current + 1 -> {
                store.saveState(state.copy(stateRev = rev))
                false
            }
            current != null && rev <= current -> false
            else -> !catchingUp && !reconciling
        }
    }

    private fun launchReconcile() {
        scope.launch {
            try {
                reconcile()
            } catch (e: CancellationException) {
                throw e
            } catch (e: ApiException) {
                if (e.isUnauthorized) callbacks.onUnauthorized() else log.w(TAG, "reconcile failed: ${e.code}")
            } catch (e: Exception) {
                log.e(TAG, "reconcile failed", e)
            }
        }
    }

    private fun launchApply(item: CachedItem) {
        if (item.meta == null) return
        if (!applier.isKindEnabled(item.kind)) {
            log.i(TAG, "not applying ${item.id}: ${item.kind} sync is off")
            return
        }
        when (AutoApply.action(item, applier.autoDownloadLimit())) {
            ApplyAction.OFFER_DOWNLOAD -> applier.offerDownload(item)
            else -> scope.launch { apply(item, guard = true) }
        }
    }

    suspend fun applyNow(item: CachedItem): Boolean = apply(item, guard = false)

    private suspend fun apply(item: CachedItem, guard: Boolean): Boolean {
        val prepared = try {
            applier.prepare(item)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            log.e(TAG, "could not prepare ${item.id}", e)
            null
        } ?: return false
        val allowed = mutex.withLock {
            if (guard && item.seq < highestWrittenSeq) {
                false
            } else {
                highestWrittenSeq = maxOf(highestWrittenSeq, item.seq)
                true
            }
        }
        if (!allowed) {
            log.i(TAG, "skipping ${item.id}: a newer item was applied meanwhile")
            return false
        }
        echo.add(item.contentHash)
        prepared.write()
        callbacks.onApplied(item)
        return true
    }

    suspend fun loadOlder(): Boolean {
        val oldest = mutex.withLock { store.oldestSeq() } ?: return false
        if (oldest <= 1) return false
        val page = api.history(before = oldest, limit = Protocol.HISTORY_DEFAULT_LIMIT)
        val items = page.items.map(codec::decode)
        mutex.withLock { store.upsert(items) }
        callbacks.onItemsChanged()
        return page.hasMore && items.isNotEmpty()
    }

    companion object {
        private const val TAG = "sync"
    }
}
