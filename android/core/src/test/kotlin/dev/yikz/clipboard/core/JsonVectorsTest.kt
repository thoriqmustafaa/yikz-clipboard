package dev.yikz.clipboard.core

import kotlinx.serialization.KSerializer
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class JsonVectorsTest {
    private val ws = Vectors.load("ws.json")
    private val http = Vectors.load("http.json")

    @Test
    fun everyWebSocketMessageDecodes() {
        val expected = mapOf(
            "hello" to HelloMsg::class,
            "welcome" to WelcomeMsg::class,
            "presence_online" to PresenceMsg::class,
            "presence_offline" to PresenceMsg::class,
            "devices_changed" to DevicesChangedMsg::class,
            "clip_inline_text" to ClipMsg::class,
            "clip_inline_image" to ClipMsg::class,
            "clip_chunked" to ClipMsg::class,
            "clip_deleted_user" to ClipDeletedMsg::class,
            "clip_deleted_retention" to ClipDeletedMsg::class,
            "clip_deleted_dedupe" to ClipDeletedMsg::class,
            "clip_pinned" to ClipPinnedMsg::class,
            "storage_warning_active" to StorageWarningMsg::class,
            "storage_warning_cleared" to StorageWarningMsg::class,
            "ping_from_client" to PingMsg::class,
            "pong_from_server" to PongMsg::class,
            "ping_from_server" to PingMsg::class,
            "pong_from_client" to PongMsg::class,
            "error_protocol_version" to ErrorMsg::class,
            "error_device_mismatch" to ErrorMsg::class,
            "error_unknown_type" to ErrorMsg::class,
            "error_invalid_message" to ErrorMsg::class,
            "error_not_ready" to ErrorMsg::class,
        )
        val messages = ws.arr("messages").map { it.o() }
        assertEquals(expected.keys, messages.map { it.str("name") }.toSet())
        for (m in messages) {
            val name = m.str("name")
            val json = m.obj("message")
            val decoded = WsCodec.decode(json.toString())
            assertEquals(name, expected[name], decoded::class)
            val reencoded = ProtocolJson.parseToJsonElement(WsCodec.encode(decoded))
            assertEquals(name, json, reencoded)
        }
        val welcome = WsCodec.decode(messages.first { it.str("name") == "welcome" }.obj("message").toString()) as WelcomeMsg
        assertEquals(44, welcome.currentSeq)
        assertEquals(17, welcome.stateRev)
        assertEquals(2, welcome.onlineDevices.size)
        val chunked = WsCodec.decode(messages.first { it.str("name") == "clip_chunked" }.obj("message").toString()) as ClipMsg
        assertEquals(null, chunked.item.payload)
        assertEquals(3, chunked.item.chunkCount)
        val clip = WsCodec.decode(messages.first { it.str("name") == "clip_inline_text" }.obj("message").toString()) as ClipMsg
        assertEquals("Hello from yikz-clipboard 👋\nSecond line.", String(Fixtures.codec.decryptPayload(clip.item.id, clip.item.payload!!)))
    }

    @Test
    fun wsEdgeCases() {
        assertTrue(WsCodec.decode("""{"type":"future_thing","x":1}""") is UnknownMsg)
        assertTrue(WsCodec.decode("""[1,2]""") is InvalidMsg)
        assertTrue(WsCodec.decode("""{"type":5}""") is InvalidMsg)
        assertTrue(WsCodec.decode("""not json""") is InvalidMsg)
        val ping = WsCodec.decode("""{"type":"ping","ts":5,"extra":{"a":1}}""")
        assertEquals(PingMsg(ts = 5), ping)
        val hello = HelloMsg(deviceId = Fixtures.OWN, lastSeq = 3, appVersion = "1.0.0")
        val encoded = ProtocolJson.parseToJsonElement(WsCodec.encode(hello)).o()
        assertEquals("android", encoded.str("platform"))
        assertEquals(1L, encoded.num("protocol_version"))
        assertEquals(3L, encoded.num("last_seq"))
    }

    @Test
    fun closeCodesMatch() {
        val codes = ws.arr("close_codes").map { it.o().num("code").toInt() }.toSet()
        assertEquals(
            setOf(
                CloseCodes.NORMAL, CloseCodes.GOING_AWAY, CloseCodes.UNAUTHORIZED, CloseCodes.TOO_MANY_CONNECTIONS,
                CloseCodes.VERSION_UNSUPPORTED, CloseCodes.HELLO_REQUIRED, CloseCodes.HEARTBEAT_TIMEOUT,
            ),
            codes,
        )
    }

    private fun <T> roundTrip(name: String, serializer: KSerializer<T>, element: JsonElement): T {
        val value = ProtocolJson.decodeFromJsonElement(serializer, element)
        assertEquals(name, stripNulls(element), ProtocolJson.encodeToJsonElement(serializer, value))
        return value
    }

    private fun stripNulls(element: JsonElement): JsonElement = when (element) {
        is JsonObject -> JsonObject(element.filterValues { it !is JsonNull }.mapValues { stripNulls(it.value) })
        is JsonArray -> JsonArray(element.map { stripNulls(it) })
        else -> element
    }

    @Test
    fun everyHttpExampleParses() {
        val requestTypes: Map<String, KSerializer<*>> = mapOf(
            "login_new_device" to LoginRequest.serializer(),
            "login_existing_device" to LoginRequest.serializer(),
            "login_invalid_credentials" to LoginRequest.serializer(),
            "login_rate_limited" to LoginRequest.serializer(),
            "key_check_set" to KeyCheckRequest.serializer(),
            "key_check_conflict" to KeyCheckRequest.serializer(),
            "device_rename" to RenameRequest.serializer(),
            "item_create_inline" to CreateItemRequest.serializer(),
            "item_create_inline_with_thumb_uploaded_first" to CreateItemRequest.serializer(),
            "item_create_repeat_is_idempotent" to CreateItemRequest.serializer(),
            "item_create_key_check_missing" to CreateItemRequest.serializer(),
            "commit" to CommitRequest.serializer(),
            "commit_missing_chunks" to CommitRequest.serializer(),
            "commit_chunk_size_mismatch" to CommitRequest.serializer(),
            "commit_item_too_large" to CommitRequest.serializer(),
            "pin" to PinRequest.serializer(),
            "unpin" to PinRequest.serializer(),
            "pin_limit" to PinRequest.serializer(),
        )
        val responseTypes: Map<String, KSerializer<*>> = mapOf(
            "login_new_device" to LoginResponse.serializer(),
            "login_existing_device" to LoginResponse.serializer(),
            "me" to MeResponse.serializer(),
            "devices_list" to DevicesResponse.serializer(),
            "device_rename" to Device.serializer(),
            "item_create_inline" to ItemHeader.serializer(),
            "item_create_inline_with_thumb_uploaded_first" to ItemHeader.serializer(),
            "item_create_repeat_is_idempotent" to ItemHeader.serializer(),
            "commit" to ItemHeader.serializer(),
            "item_get_inline" to ItemHeader.serializer(),
            "item_get_chunked" to ItemHeader.serializer(),
            "history_newest" to HistoryResponse.serializer(),
            "history_before" to HistoryResponse.serializer(),
            "history_after_catch_up" to HistoryResponse.serializer(),
            "history_index" to HistoryIndex.serializer(),
            "pin" to ItemHeader.serializer(),
            "unpin" to ItemHeader.serializer(),
            "storage" to StorageInfo.serializer(),
            "healthz" to Healthz.serializer(),
        )
        val examples = http.arr("examples").map { it.o() }
        var parsedResponses = 0
        for (e in examples) {
            val name = e.str("name")
            val status = e.num("status").toInt()
            val request = e["request_body"]
            if (request != null && request !is JsonPrimitive) {
                val type = requestTypes[name]
                if (type != null) roundTrip(name, type, request)
            }
            val response = e["response_body"] ?: continue
            if (status >= 400) {
                val error = ProtocolJson.decodeFromJsonElement(ApiErrorBody.serializer(), response)
                assertTrue(name, error.code.isNotEmpty())
                parsedResponses++
                continue
            }
            if (response is JsonPrimitive) {
                assertEquals("thumb_download", name)
                val sealed = StrictBase64.decode(response.content)
                assertEquals(e.obj("response_headers").str("Content-Length").toInt(), sealed.size)
                Fixtures.codec.decryptThumb(e.str("path").split("/")[3], sealed)
                parsedResponses++
                continue
            }
            val type = responseTypes[name] ?: error("no response type for $name")
            val value = roundTrip(name, type, response)
            parsedResponses++
            when (value) {
                is ItemHeader -> Fixtures.codec.decode(value).also { assertEquals(name, null, it.metaError) }
                is HistoryResponse -> value.items.forEach {
                    assertEquals(null, it.payload)
                    assertEquals(name, null, Fixtures.codec.decode(it).metaError)
                }
                is LoginResponse -> {
                    assertEquals(Protocol.KDF_ALGORITHM, value.kdf.algorithm)
                    assertEquals(Protocol.VERSION, value.protocolVersion)
                    assertTrue(Tokens.isValid(value.token))
                }
                is MeResponse -> assertEquals(Fixtures.key.keyCheck, value.keyCheck)
            }
        }
        assertTrue(parsedResponses >= 40)
        val errors = examples.filter { it.num("status") >= 400 }.map { it.str("name") }
        assertTrue(errors.contains("commit_missing_chunks"))
        val missing = examples.first { it.str("name") == "commit_missing_chunks" }.obj("response_body")
        val body = ProtocolJson.decodeFromJsonElement(ApiErrorBody.serializer(), missing)
        assertEquals(listOf(1, 2), ApiException(409, body.code, body.message, body.details).missingChunks())
        val rateLimited = examples.first { it.str("name") == "login_rate_limited" }
        assertEquals("600", rateLimited.obj("response_headers").str("Retry-After"))
    }
}
