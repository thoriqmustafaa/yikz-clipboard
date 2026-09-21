package dev.yikz.clipboard.core

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray

class ApiException(
    val status: Int,
    val code: String,
    message: String,
    val details: JsonObject? = null,
    val retryAfterSeconds: Long? = null,
    cause: Throwable? = null,
) : Exception(message, cause) {
    val isNetwork: Boolean get() = status == 0
    val isUnauthorized: Boolean get() = status == 401 && code != "invalid_credentials"
    val isRetryable: Boolean get() = status == 0 || status == 500 || status == 502 || status == 503 || status == 504
    val isNotFound: Boolean get() = status == 404

    fun missingChunks(): List<Int> = try {
        details?.get("missing")?.jsonArray?.mapNotNull { (it as? JsonPrimitive)?.intOrNull } ?: emptyList()
    } catch (_: Exception) {
        emptyList()
    }

    override fun toString(): String = "ApiException(status=$status, code=$code, message=$message)"
}

interface SyncApi {
    suspend fun me(): MeResponse
    suspend fun putKeyCheck(keyCheck: String)
    suspend fun history(before: Long? = null, after: Long? = null, limit: Int? = null): HistoryResponse
    suspend fun historyIndex(): HistoryIndex
    suspend fun getItem(id: String): ItemHeader
    suspend fun downloadChunk(id: String, index: Int): ByteArray
    suspend fun downloadThumb(id: String): ByteArray
    suspend fun createItem(request: CreateItemRequest): ItemHeader
    suspend fun uploadChunk(id: String, index: Int, body: ByteArray)
    suspend fun uploadThumb(id: String, body: ByteArray)
    suspend fun commit(id: String, request: CommitRequest): ItemHeader
    suspend fun deleteItem(id: String)
    suspend fun pin(id: String, pinned: Boolean): ItemHeader
    suspend fun devices(): List<Device>
    suspend fun renameDevice(id: String, name: String): Device
    suspend fun deleteDevice(id: String)
    suspend fun storage(): StorageInfo
    suspend fun logout()
}

data class SyncState(
    val serverId: String? = null,
    val lastSeq: Long = 0,
    val stateRev: Long? = null,
    val appliedSeq: Long = 0,
)

interface SyncStore {
    fun state(): SyncState
    fun saveState(state: SyncState)
    fun upsert(items: List<CachedItem>)
    fun delete(ids: Collection<String>)
    fun setPinned(id: String, pinned: Boolean)
    fun reconcile(index: Map<String, Boolean>, upToSeq: Long)
    fun clearAll()
    fun newestContentHash(): String?
    fun get(id: String): CachedItem?
    fun payload(id: String): String?
    fun oldestSeq(): Long?
}
