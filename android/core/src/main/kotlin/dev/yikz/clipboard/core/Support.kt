package dev.yikz.clipboard.core

import kotlin.random.Random

enum class LogLevel { DEBUG, INFO, WARN, ERROR }

interface Logger {
    fun log(level: LogLevel, tag: String, message: String, error: Throwable? = null)

    fun d(tag: String, message: String) = log(LogLevel.DEBUG, tag, message)
    fun i(tag: String, message: String) = log(LogLevel.INFO, tag, message)
    fun w(tag: String, message: String, error: Throwable? = null) = log(LogLevel.WARN, tag, message, error)
    fun e(tag: String, message: String, error: Throwable? = null) = log(LogLevel.ERROR, tag, message, error)
}

object NoLog : Logger {
    override fun log(level: LogLevel, tag: String, message: String, error: Throwable?) {}
}

object Backoff {
    const val RECONNECT_BASE_MS = 500L
    const val RECONNECT_MAX_MS = 30_000L
    const val HTTP_BASE_MS = 1_000L
    const val HTTP_MAX_MS = 60_000L

    fun ceilingMs(attempt: Int, baseMs: Long, maxMs: Long): Long {
        val shift = attempt.coerceIn(0, 30)
        val raw = baseMs shl shift
        return if (raw <= 0 || raw > maxMs) maxMs else raw
    }

    fun reconnectCeilingMs(attempt: Int): Long = ceilingMs(attempt, RECONNECT_BASE_MS, RECONNECT_MAX_MS)

    fun reconnectDelayMs(attempt: Int, random: Random = Random.Default): Long =
        (random.nextDouble() * reconnectCeilingMs(attempt)).toLong()

    fun httpRetryCeilingMs(attempt: Int): Long = ceilingMs(attempt, HTTP_BASE_MS, HTTP_MAX_MS)

    fun httpRetryDelayMs(attempt: Int, random: Random = Random.Default): Long =
        (random.nextDouble() * httpRetryCeilingMs(attempt)).toLong()
}

class EchoGuard(private val capacity: Int = Protocol.RECENT_HASHES) {
    private val hashes = ArrayDeque<String>()

    @Synchronized
    fun add(hash: String) {
        hashes.remove(hash)
        hashes.addLast(hash)
        while (hashes.size > capacity) hashes.removeFirst()
    }

    @Synchronized
    operator fun contains(hash: String): Boolean = hash in hashes

    @Synchronized
    fun snapshot(): List<String> = hashes.toList()

    @Synchronized
    fun clear() = hashes.clear()
}

sealed interface UploadDecision {
    data class Upload(val contentHash: String) : UploadDecision
    data class Skip(val reason: String) : UploadDecision
}

object EchoRules {
    fun decide(
        key: MasterKey,
        content: ByteArray,
        kind: String,
        recent: EchoGuard,
        newestCachedHash: String?,
        sensitive: Boolean = false,
        hasOriginMarker: Boolean = false,
    ): UploadDecision {
        if (sensitive) return UploadDecision.Skip("sensitive")
        if (hasOriginMarker) return UploadDecision.Skip("own write")
        if (content.isEmpty()) return UploadDecision.Skip("empty")
        val hash = key.contentHash(content)
        return decide(hash, normalizedTextHash(key, content, kind), recent, newestCachedHash)
    }

    fun decide(
        contentHash: String,
        normalizedHash: String?,
        recent: EchoGuard,
        newestCachedHash: String?,
    ): UploadDecision {
        if (contentHash in recent) return UploadDecision.Skip("recently synced")
        if (normalizedHash != null && normalizedHash in recent) return UploadDecision.Skip("recently synced")
        if (contentHash == newestCachedHash) return UploadDecision.Skip("already current")
        return UploadDecision.Upload(contentHash)
    }

    fun normalizedTextHash(key: MasterKey, content: ByteArray, kind: String): String? {
        if (kind != Kind.TEXT) return null
        val text = String(content, Charsets.UTF_8)
        if (!text.contains("\r\n")) return null
        return key.contentHash(text.replace("\r\n", "\n").toByteArray(Charsets.UTF_8))
    }
}

enum class ApplyAction { WRITE_INLINE, DOWNLOAD_AND_WRITE, OFFER_DOWNLOAD }

object AutoApply {
    fun isEligible(item: CachedItem, ownDeviceId: String, appliedSeq: Long, serverNowMs: Long): Boolean {
        if (item.deviceId == ownDeviceId) return false
        if (item.seq <= appliedSeq) return false
        val created = item.createdAtMs ?: return false
        return serverNowMs - created <= Protocol.AUTO_APPLY_MAX_AGE_MS
    }

    fun pickAfterCatchUp(
        items: Collection<CachedItem>,
        ownDeviceId: String,
        appliedSeq: Long,
        serverNowMs: Long,
    ): CachedItem? = items.filter { isEligible(it, ownDeviceId, appliedSeq, serverNowMs) }.maxByOrNull { it.seq }

    fun action(item: CachedItem, autoDownloadLimit: Long): ApplyAction = when {
        item.chunkCount == 0 -> ApplyAction.WRITE_INLINE
        item.size <= autoDownloadLimit -> ApplyAction.DOWNLOAD_AND_WRITE
        else -> ApplyAction.OFFER_DOWNLOAD
    }
}
