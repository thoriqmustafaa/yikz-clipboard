package dev.yikz.clipboard.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject

sealed interface WsMessage

@Serializable
data class HelloMsg(
    val type: String = "hello",
    val protocolVersion: Int = Protocol.VERSION,
    val deviceId: String,
    val lastSeq: Long,
    val appVersion: String,
    val platform: String = Protocol.PLATFORM_ANDROID,
) : WsMessage

@Serializable
data class OnlineDevice(val deviceId: String, val name: String = "", val platform: String = "")

@Serializable
data class WelcomeMsg(
    val type: String = "welcome",
    val protocolVersion: Int,
    val serverVersion: String = "",
    val serverId: String,
    val serverTime: String,
    val deviceId: String,
    val currentSeq: Long,
    val stateRev: Long,
    val onlineDevices: List<OnlineDevice> = emptyList(),
) : WsMessage

@Serializable
data class PresenceMsg(
    val type: String = "presence",
    val deviceId: String,
    val name: String = "",
    val platform: String = "",
    val online: Boolean,
) : WsMessage

@Serializable
data class DevicesChangedMsg(val type: String = "devices_changed") : WsMessage

@Serializable
data class ClipMsg(val type: String = "clip", val item: ItemHeader) : WsMessage

@Serializable
data class ClipDeletedMsg(
    val type: String = "clip_deleted",
    val ids: List<String>,
    val reason: String = "",
    val stateRev: Long,
) : WsMessage

@Serializable
data class ClipPinnedMsg(
    val type: String = "clip_pinned",
    val id: String,
    val pinned: Boolean,
    val stateRev: Long,
) : WsMessage

@Serializable
data class StorageWarningMsg(
    val type: String = "storage_warning",
    val active: Boolean,
    val reason: String = "",
    val freeDiskBytes: Long = 0,
    val minFreeDiskBytes: Long = 0,
) : WsMessage

@Serializable
data class ReleaseAvailableMsg(val type: String = "release_available", val version: String) : WsMessage

@Serializable
data class PingMsg(val type: String = "ping", val ts: Long) : WsMessage

@Serializable
data class PongMsg(val type: String = "pong", val ts: Long) : WsMessage

@Serializable
data class ErrorMsg(
    val type: String = "error",
    val code: String,
    val message: String = "",
    val details: JsonObject? = null,
) : WsMessage

data class UnknownMsg(val type: String) : WsMessage

data class InvalidMsg(val reason: String) : WsMessage

object WsCodec {
    fun decode(text: String): WsMessage {
        val obj = try {
            ProtocolJson.parseToJsonElement(text).jsonObject
        } catch (e: Exception) {
            return InvalidMsg("not a JSON object")
        }
        val typeElement = obj["type"] as? JsonPrimitive
        if (typeElement == null || !typeElement.isString) return InvalidMsg("missing type")
        val type = typeElement.content
        return try {
            when (type) {
                "welcome" -> ProtocolJson.decodeFromJsonElement(WelcomeMsg.serializer(), obj)
                "presence" -> ProtocolJson.decodeFromJsonElement(PresenceMsg.serializer(), obj)
                "devices_changed" -> DevicesChangedMsg()
                "clip" -> ProtocolJson.decodeFromJsonElement(ClipMsg.serializer(), obj)
                "clip_deleted" -> ProtocolJson.decodeFromJsonElement(ClipDeletedMsg.serializer(), obj)
                "clip_pinned" -> ProtocolJson.decodeFromJsonElement(ClipPinnedMsg.serializer(), obj)
                "storage_warning" -> ProtocolJson.decodeFromJsonElement(StorageWarningMsg.serializer(), obj)
                "release_available" -> ProtocolJson.decodeFromJsonElement(ReleaseAvailableMsg.serializer(), obj)
                "ping" -> ProtocolJson.decodeFromJsonElement(PingMsg.serializer(), obj)
                "pong" -> ProtocolJson.decodeFromJsonElement(PongMsg.serializer(), obj)
                "error" -> ProtocolJson.decodeFromJsonElement(ErrorMsg.serializer(), obj)
                "hello" -> ProtocolJson.decodeFromJsonElement(HelloMsg.serializer(), obj)
                else -> UnknownMsg(type)
            }
        } catch (e: Exception) {
            InvalidMsg("malformed $type: ${e.message}")
        }
    }

    fun encode(message: WsMessage): String = when (message) {
        is HelloMsg -> ProtocolJson.encodeToString(HelloMsg.serializer(), message)
        is PingMsg -> ProtocolJson.encodeToString(PingMsg.serializer(), message)
        is PongMsg -> ProtocolJson.encodeToString(PongMsg.serializer(), message)
        is WelcomeMsg -> ProtocolJson.encodeToString(WelcomeMsg.serializer(), message)
        is PresenceMsg -> ProtocolJson.encodeToString(PresenceMsg.serializer(), message)
        is DevicesChangedMsg -> ProtocolJson.encodeToString(DevicesChangedMsg.serializer(), message)
        is ClipMsg -> ProtocolJson.encodeToString(ClipMsg.serializer(), message)
        is ClipDeletedMsg -> ProtocolJson.encodeToString(ClipDeletedMsg.serializer(), message)
        is ClipPinnedMsg -> ProtocolJson.encodeToString(ClipPinnedMsg.serializer(), message)
        is StorageWarningMsg -> ProtocolJson.encodeToString(StorageWarningMsg.serializer(), message)
        is ErrorMsg -> ProtocolJson.encodeToString(ErrorMsg.serializer(), message)
        is ReleaseAvailableMsg -> ProtocolJson.encodeToString(ReleaseAvailableMsg.serializer(), message)
        is UnknownMsg, is InvalidMsg -> throw IllegalArgumentException("cannot encode $message")
    }
}

object CloseCodes {
    const val NORMAL = 1000
    const val GOING_AWAY = 1001
    const val TOO_LARGE = 1009
    const val UNAUTHORIZED = 4001
    const val TOO_MANY_CONNECTIONS = 4002
    const val VERSION_UNSUPPORTED = 4003
    const val HELLO_REQUIRED = 4004
    const val HEARTBEAT_TIMEOUT = 4005
}
