@file:OptIn(ExperimentalSerializationApi::class)

package dev.yikz.clipboard.core

import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNamingStrategy
import kotlinx.serialization.json.JsonObject

val ProtocolJson: Json = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
    encodeDefaults = true
    namingStrategy = JsonNamingStrategy.SnakeCase
}

@Serializable
data class KdfParams(
    val algorithm: String,
    val iterations: Int,
    val keyLength: Int,
)

@Serializable
data class LoginRequest(
    val username: String,
    val password: String,
    val deviceName: String,
    val platform: String,
    val deviceId: String? = null,
)

@Serializable
data class LoginResponse(
    val deviceId: String,
    val token: String,
    val username: String,
    val salt: String,
    val kdf: KdfParams,
    val keyCheck: String? = null,
    val serverId: String,
    val serverVersion: String = "",
    val protocolVersion: Int,
)

@Serializable
data class Device(
    val id: String,
    val name: String,
    val platform: String,
    val createdAt: String = "",
    val lastSeenAt: String = "",
    val online: Boolean = false,
    val revoked: Boolean = false,
    val current: Boolean = false,
)

@Serializable
data class DevicesResponse(val devices: List<Device>)

@Serializable
data class Limits(
    val inlineMaxBytes: Long = Protocol.INLINE_MAX_BYTES,
    val chunkSizeBytes: Long = Protocol.CHUNK_SIZE_BYTES,
    val maxChunkBodyBytes: Long = Protocol.MAX_CHUNK_BODY_BYTES,
    val thumbMaxBytes: Long = Protocol.THUMB_MAX_BYTES.toLong(),
    val metaMaxBytes: Long = Protocol.META_MAX_BYTES.toLong(),
    val maxJsonBodyBytes: Long = 1_048_576,
    val maxFilesPerItem: Int = Protocol.MAX_FILES_PER_ITEM,
    val historyDefaultLimit: Int = Protocol.HISTORY_DEFAULT_LIMIT,
    val historyMaxLimit: Int = Protocol.HISTORY_MAX_LIMIT,
    val maxWsClientFrameBytes: Long = 65_536,
    val maxWsServerFrameBytes: Long = 1_048_576,
    val maxConnectionsPerDevice: Int = 8,
    val uploadTtlSeconds: Long = 3600,
    val diskLowMaxUploadBytes: Long = 1_048_576,
)

@Serializable
data class MeResponse(
    val username: String,
    val device: Device,
    val salt: String,
    val kdf: KdfParams,
    val keyCheck: String? = null,
    val serverId: String,
    val serverVersion: String = "",
    val protocolVersion: Int,
    val limits: Limits = Limits(),
)

@Serializable
data class KeyCheckRequest(val keyCheck: String)

@Serializable
data class RenameRequest(val name: String)

@Serializable
data class PinRequest(val pinned: Boolean)

@Serializable
data class ItemHeader(
    val id: String,
    val seq: Long = 0,
    val deviceId: String = "",
    val kind: String,
    val size: Long,
    val chunkCount: Int = 0,
    val createdAt: String = "",
    val pinned: Boolean = false,
    val contentHash: String,
    val hasThumb: Boolean = false,
    val storedBytes: Long = 0,
    val meta: String,
    val payload: String? = null,
)

@Serializable
data class CreateItemRequest(
    val id: String,
    val kind: String,
    val size: Long,
    val chunkCount: Int,
    val contentHash: String,
    val meta: String,
    val payload: String,
)

@Serializable
data class CommitRequest(
    val kind: String,
    val size: Long,
    val chunkCount: Int,
    val contentHash: String,
    val meta: String,
)

@Serializable
data class HistoryResponse(
    val items: List<ItemHeader>,
    val hasMore: Boolean = false,
)

@Serializable
data class IndexEntry(val id: String, val seq: Long, val pinned: Boolean)

@Serializable
data class HistoryIndex(
    val currentSeq: Long,
    val stateRev: Long,
    val items: List<IndexEntry>,
)

@Serializable
data class StorageInfo(
    val usedBytes: Long = 0,
    val limitBytes: Long = 0,
    val pinnedBytes: Long = 0,
    val pinnedLimitBytes: Long = 0,
    val itemCount: Long = 0,
    val freeDiskBytes: Long = 0,
    val minFreeDiskBytes: Long = 0,
    val retentionDays: Int = 0,
    val diskLow: Boolean = false,
)

@Serializable
data class Healthz(
    val status: String,
    val serverVersion: String = "",
    val protocolVersion: Int,
)

@Serializable
data class ApiErrorBody(
    val code: String = "",
    val message: String = "",
    val details: JsonObject? = null,
)

@Serializable
data class ImageDims(val width: Int, val height: Int)

@Serializable
data class FileEntry(val name: String, val size: Long)

@Serializable
data class ItemMeta(
    val v: Int,
    val mime: String,
    val preview: String,
    val sha256: String,
    val image: ImageDims? = null,
    val files: List<FileEntry>? = null,
    val sourceApp: String? = null,
)
