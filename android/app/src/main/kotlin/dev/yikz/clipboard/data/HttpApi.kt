package dev.yikz.clipboard.data

import dev.yikz.clipboard.core.ApiErrorBody
import dev.yikz.clipboard.core.ApiException
import dev.yikz.clipboard.core.CommitRequest
import dev.yikz.clipboard.core.CreateItemRequest
import dev.yikz.clipboard.core.Device
import dev.yikz.clipboard.core.DevicesResponse
import dev.yikz.clipboard.core.Healthz
import dev.yikz.clipboard.core.HistoryIndex
import dev.yikz.clipboard.core.HistoryResponse
import dev.yikz.clipboard.core.ItemHeader
import dev.yikz.clipboard.core.KeyCheckRequest
import dev.yikz.clipboard.core.LoginRequest
import dev.yikz.clipboard.core.LoginResponse
import dev.yikz.clipboard.core.MeResponse
import dev.yikz.clipboard.core.PinRequest
import dev.yikz.clipboard.core.ProtocolJson
import dev.yikz.clipboard.core.RenameRequest
import dev.yikz.clipboard.core.StorageInfo
import dev.yikz.clipboard.core.SyncApi
import dev.yikz.clipboard.core.WsListener
import dev.yikz.clipboard.core.WsSocket
import dev.yikz.clipboard.core.WsTransport
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.serialization.KSerializer
import okhttp3.Call
import okhttp3.Callback
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

object Http {
    val base: OkHttpClient by lazy {
        OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(60, TimeUnit.SECONDS)
            .writeTimeout(60, TimeUnit.SECONDS)
            .retryOnConnectionFailure(true)
            .build()
    }

    val websocket: OkHttpClient by lazy {
        base.newBuilder()
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .pingInterval(0, TimeUnit.MILLISECONDS)
            .build()
    }

    fun normalizeServerUrl(input: String): HttpUrl? {
        var text = input.trim().trimEnd('/')
        if (text.isEmpty()) return null
        if (!text.startsWith("http://") && !text.startsWith("https://")) text = "https://$text"
        val url = text.toHttpUrlOrNull() ?: return null
        return url.newBuilder().encodedPath("/").query(null).fragment(null).build()
    }
}

private val JSON = "application/json; charset=utf-8".toMediaType()
private val OCTET = "application/octet-stream".toMediaType()

class HttpApi(private val baseUrl: HttpUrl, private val token: () -> String?) : SyncApi {
    private val client = Http.base

    private fun url(path: String, query: Map<String, String?> = emptyMap()): HttpUrl {
        val builder = baseUrl.newBuilder()
        path.trim('/').split('/').filter { it.isNotEmpty() }.forEach { builder.addPathSegment(it) }
        for ((k, v) in query) if (v != null) builder.addQueryParameter(k, v)
        return builder.build()
    }

    private fun request(path: String, query: Map<String, String?> = emptyMap(), auth: Boolean = true): Request.Builder {
        val b = Request.Builder().url(url(path, query))
        if (auth) {
            val t = token() ?: throw ApiException(401, "unauthorized", "not signed in")
            b.header("Authorization", "Bearer $t")
        }
        return b
    }

    private suspend fun execute(request: Request): Response = suspendCancellableCoroutine { cont ->
        val call = client.newCall(request)
        cont.invokeOnCancellation { call.cancel() }
        call.enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                cont.resumeWithException(ApiException(0, "network", e.message ?: "network error", cause = e))
            }

            override fun onResponse(call: Call, response: Response) {
                cont.resume(response)
            }
        })
    }

    private suspend fun raw(request: Request): ByteArray {
        val response = execute(request)
        response.use {
            val body = try {
                it.body.bytes()
            } catch (e: IOException) {
                throw ApiException(0, "network", e.message ?: "network error", cause = e)
            }
            if (!it.isSuccessful) throw toError(it, body)
            return body
        }
    }

    private fun toError(response: Response, body: ByteArray): ApiException {
        val parsed = try {
            ProtocolJson.decodeFromString(ApiErrorBody.serializer(), body.toString(Charsets.UTF_8))
        } catch (_: Exception) {
            ApiErrorBody(code = "http_${response.code}", message = response.message)
        }
        return ApiException(
            status = response.code,
            code = parsed.code.ifEmpty { "http_${response.code}" },
            message = parsed.message.ifEmpty { response.message },
            details = parsed.details,
            retryAfterSeconds = response.header("Retry-After")?.toLongOrNull(),
        )
    }

    private suspend fun <T> json(request: Request, serializer: KSerializer<T>): T {
        val body = raw(request)
        return try {
            ProtocolJson.decodeFromString(serializer, body.toString(Charsets.UTF_8))
        } catch (e: Exception) {
            throw ApiException(0, "invalid_response", "unexpected server response", cause = e)
        }
    }

    private fun <T> body(serializer: KSerializer<T>, value: T): RequestBody =
        ProtocolJson.encodeToString(serializer, value).toRequestBody(JSON)

    suspend fun healthz(): Healthz = json(request("healthz", auth = false).get().build(), Healthz.serializer())

    suspend fun login(request: LoginRequest): LoginResponse =
        json(request("api/login", auth = false).post(body(LoginRequest.serializer(), request)).build(), LoginResponse.serializer())

    override suspend fun me(): MeResponse = json(request("api/me").get().build(), MeResponse.serializer())

    override suspend fun putKeyCheck(keyCheck: String) {
        raw(request("api/account/key-check").put(body(KeyCheckRequest.serializer(), KeyCheckRequest(keyCheck))).build())
    }

    override suspend fun history(before: Long?, after: Long?, limit: Int?): HistoryResponse = json(
        request("api/history", mapOf("before" to before?.toString(), "after" to after?.toString(), "limit" to limit?.toString())).get().build(),
        HistoryResponse.serializer(),
    )

    override suspend fun historyIndex(): HistoryIndex = json(request("api/history/index").get().build(), HistoryIndex.serializer())

    override suspend fun getItem(id: String): ItemHeader = json(request("api/items/$id").get().build(), ItemHeader.serializer())

    override suspend fun downloadChunk(id: String, index: Int): ByteArray = raw(request("api/items/$id/chunks/$index").get().build())

    override suspend fun downloadThumb(id: String): ByteArray = raw(request("api/items/$id/thumb").get().build())

    override suspend fun createItem(request: CreateItemRequest): ItemHeader =
        json(request("api/items").post(body(CreateItemRequest.serializer(), request)).build(), ItemHeader.serializer())

    override suspend fun uploadChunk(id: String, index: Int, body: ByteArray) {
        raw(request("api/items/$id/chunks/$index").put(body.toRequestBody(OCTET)).build())
    }

    override suspend fun uploadThumb(id: String, body: ByteArray) {
        raw(request("api/items/$id/thumb").put(body.toRequestBody(OCTET)).build())
    }

    override suspend fun commit(id: String, request: CommitRequest): ItemHeader =
        json(request("api/items/$id/commit").post(body(CommitRequest.serializer(), request)).build(), ItemHeader.serializer())

    override suspend fun deleteItem(id: String) {
        try {
            raw(request("api/items/$id").delete().build())
        } catch (e: ApiException) {
            if (!e.isNotFound) throw e
        }
    }

    override suspend fun pin(id: String, pinned: Boolean): ItemHeader =
        json(request("api/items/$id/pin").post(body(PinRequest.serializer(), PinRequest(pinned))).build(), ItemHeader.serializer())

    override suspend fun devices(): List<Device> = json(request("api/devices").get().build(), DevicesResponse.serializer()).devices

    override suspend fun renameDevice(id: String, name: String): Device =
        json(request("api/devices/$id").patch(body(RenameRequest.serializer(), RenameRequest(name))).build(), Device.serializer())

    override suspend fun deleteDevice(id: String) {
        raw(request("api/devices/$id").delete().build())
    }

    override suspend fun storage(): StorageInfo = json(request("api/storage").get().build(), StorageInfo.serializer())

    override suspend fun logout() {
        raw(request("api/logout").post(ByteArray(0).toRequestBody(null)).build())
    }
}

class OkHttpWsTransport(private val baseUrl: HttpUrl, private val token: () -> String?) : WsTransport {
    override fun open(listener: WsListener): WsSocket {
        val t = token() ?: throw IllegalStateException("not signed in")
        val request = Request.Builder()
            .url(baseUrl.newBuilder().addPathSegment("ws").build())
            .header("Authorization", "Bearer $t")
            .build()
        val reported = java.util.concurrent.atomic.AtomicBoolean(false)
        val ws = Http.websocket.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) = listener.onOpen()

            override fun onMessage(webSocket: WebSocket, text: String) = listener.onMessage(text)

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(code.takeIf { it in 1000..4999 && it != 1005 && it != 1006 } ?: 1000, null)
                if (reported.compareAndSet(false, true)) listener.onClosed(code, reason)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                if (reported.compareAndSet(false, true)) listener.onClosed(code, reason)
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                val status = response?.code
                response?.close()
                if (reported.compareAndSet(false, true)) listener.onFailure(t, status)
            }
        })
        return object : WsSocket {
            override fun send(text: String): Boolean = ws.send(text)
            override fun close(code: Int, reason: String) {
                ws.close(code, reason)
            }

            override fun cancel() = ws.cancel()
        }
    }
}
