package dev.yikz.clipboard.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlin.random.Random

interface WsListener {
    fun onOpen()
    fun onMessage(text: String)
    fun onClosed(code: Int, reason: String)
    fun onFailure(error: Throwable, httpStatus: Int?)
}

interface WsSocket {
    fun send(text: String): Boolean
    fun close(code: Int, reason: String)
    fun cancel()
}

fun interface WsTransport {
    fun open(listener: WsListener): WsSocket
}

enum class BlockReason { UNAUTHORIZED, UPDATE_REQUIRED, TOO_MANY_CONNECTIONS }

sealed interface ConnectionState {
    data object Idle : ConnectionState
    data object Paused : ConnectionState
    data class Connecting(val attempt: Int) : ConnectionState
    data class Connected(val onlineDevices: List<OnlineDevice>, val sinceMs: Long) : ConnectionState
    data class Waiting(val retryAtMs: Long, val attempt: Int, val lastError: String?) : ConnectionState
    data class Blocked(val reason: BlockReason) : ConnectionState
}

interface ConnectionCallbacks {
    fun onUnauthorized() {}
    fun onUpdateRequired() {}
}

class ConnectionManager(
    private val transport: WsTransport,
    private val handler: SyncHandler,
    private val scope: CoroutineScope,
    private val hello: () -> HelloMsg,
    private val clock: () -> Long,
    private val wallClock: () -> Long = System::currentTimeMillis,
    private val random: Random = Random.Default,
    private val log: Logger = NoLog,
    private val callbacks: ConnectionCallbacks = object : ConnectionCallbacks {},
) {
    private sealed interface Event {
        data object Start : Event
        data object Stop : Event
        data object Pause : Event
        data object Resume : Event
        data object NetworkChanged : Event
        data object Foreground : Event
        data object Reconnect : Event
        data class Opened(val gen: Int) : Event
        data class Message(val gen: Int, val text: String) : Event
        data class Closed(val gen: Int, val code: Int, val reason: String) : Event
        data class Failed(val gen: Int, val error: Throwable, val httpStatus: Int?) : Event
        data class Tick(val gen: Int) : Event
        data class RetryDue(val token: Int) : Event
        data class ProbeDue(val gen: Int, val sentAt: Long) : Event
    }

    private val events = Channel<Event>(Channel.UNLIMITED)
    private val _state = MutableStateFlow<ConnectionState>(ConnectionState.Idle)
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    private var socket: WsSocket? = null
    private var generation = 0
    private var attempt = 0
    private var retryToken = 0
    private var retryJob: Job? = null
    private var heartbeatJob: Job? = null
    private var lastReceivedAt = 0L
    private var lastPingSentAt = 0L
    private var welcomed = false
    private var online: MutableList<OnlineDevice> = mutableListOf()
    private var loop: Job? = null

    fun launch() {
        if (loop != null) return
        loop = scope.launch {
            for (event in events) {
                try {
                    handle(event)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    log.e(TAG, "error handling $event", e)
                }
            }
        }
    }

    fun start() = post(Event.Start)
    fun stop() = post(Event.Stop)
    fun pause() = post(Event.Pause)
    fun resume() = post(Event.Resume)
    fun networkChanged() = post(Event.NetworkChanged)
    fun foreground() = post(Event.Foreground)
    fun reconnectNow() = post(Event.Reconnect)

    private fun post(event: Event) {
        events.trySend(event)
    }

    private suspend fun handle(event: Event) {
        when (event) {
            Event.Start -> when (_state.value) {
                ConnectionState.Idle, is ConnectionState.Waiting -> connectNow(resetAttempt = true)
                else -> Unit
            }
            Event.Stop -> {
                shutdown(CloseCodes.NORMAL, "stopped")
                _state.value = ConnectionState.Idle
            }
            Event.Pause -> {
                shutdown(CloseCodes.NORMAL, "paused")
                _state.value = ConnectionState.Paused
            }
            Event.Resume -> if (_state.value == ConnectionState.Paused || _state.value == ConnectionState.Idle) {
                connectNow(resetAttempt = true)
            }
            Event.Reconnect -> if (_state.value != ConnectionState.Paused && !isHardBlocked()) {
                connectNow(resetAttempt = true)
            }
            Event.NetworkChanged -> if (_state.value != ConnectionState.Paused && _state.value != ConnectionState.Idle && !isBlocked()) {
                log.i(TAG, "network changed, reconnecting now")
                connectNow(resetAttempt = true)
            }
            Event.Foreground -> onForeground()
            is Event.Opened -> if (event.gen == generation) {
                lastReceivedAt = clock()
                lastPingSentAt = clock()
                send(WsCodec.encode(hello()))
                startHeartbeat(event.gen)
            }
            is Event.Message -> if (event.gen == generation) onMessage(event.text)
            is Event.Closed -> if (event.gen == generation) onClosed(event.code, event.reason, null)
            is Event.Failed -> if (event.gen == generation) {
                val status = event.httpStatus
                if (status == 401) {
                    onClosed(CloseCodes.UNAUTHORIZED, "unauthorized", null)
                } else {
                    onClosed(-1, event.error.message ?: event.error.javaClass.simpleName, event.error)
                }
            }
            is Event.Tick -> if (event.gen == generation) onTick()
            is Event.RetryDue -> if (event.token == retryToken && _state.value is ConnectionState.Waiting) {
                connectNow(resetAttempt = false)
            }
            is Event.ProbeDue -> if (event.gen == generation && lastReceivedAt < event.sentAt) {
                log.w(TAG, "no answer to foreground probe, reconnecting")
                connectNow(resetAttempt = true)
            }
        }
    }

    private fun isBlocked(): Boolean = _state.value is ConnectionState.Blocked

    private fun isHardBlocked(): Boolean {
        val s = _state.value
        return s is ConnectionState.Blocked && s.reason != BlockReason.TOO_MANY_CONNECTIONS
    }

    private suspend fun onForeground() {
        when (val s = _state.value) {
            ConnectionState.Paused, ConnectionState.Idle -> Unit
            is ConnectionState.Blocked -> if (s.reason == BlockReason.TOO_MANY_CONNECTIONS) connectNow(resetAttempt = true)
            is ConnectionState.Waiting -> connectNow(resetAttempt = true)
            is ConnectionState.Connecting -> Unit
            is ConnectionState.Connected -> {
                val sentAt = clock()
                send(WsCodec.encode(PingMsg(ts = wallClock())))
                lastPingSentAt = sentAt
                val gen = generation
                scope.launch {
                    delay(Protocol.FOREGROUND_PROBE_MS)
                    post(Event.ProbeDue(gen, sentAt))
                }
            }
        }
    }

    private suspend fun onMessage(text: String) {
        lastReceivedAt = clock()
        when (val message = WsCodec.decode(text)) {
            is PingMsg -> send(WsCodec.encode(PongMsg(ts = message.ts)))
            is PongMsg -> Unit
            is WelcomeMsg -> {
                attempt = 0
                welcomed = true
                online = message.onlineDevices.toMutableList()
                _state.value = ConnectionState.Connected(online.toList(), wallClock())
                log.i(TAG, "connected, ${online.size} devices online, current_seq ${message.currentSeq}")
                handler.onWelcome(message)
            }
            is PresenceMsg -> {
                online.removeAll { it.deviceId == message.deviceId }
                if (message.online) online += OnlineDevice(message.deviceId, message.name, message.platform)
                val s = _state.value
                if (s is ConnectionState.Connected) _state.value = s.copy(onlineDevices = online.toList())
            }
            is ErrorMsg -> log.w(TAG, "server error ${message.code}: ${message.message}")
            is InvalidMsg -> log.w(TAG, "ignoring invalid message: ${message.reason}")
            is UnknownMsg -> Unit
            else -> if (welcomed) handler.onMessage(message)
        }
    }

    private suspend fun onTick() {
        val now = clock()
        if (now - lastReceivedAt >= Protocol.DEAD_TIMEOUT_MS) {
            log.w(TAG, "no traffic for ${(now - lastReceivedAt) / 1000}s, connection is dead")
            onClosedInternal(CloseCodes.HEARTBEAT_TIMEOUT, "heartbeat timeout", null, closeSocket = true)
            return
        }
        if (now - lastPingSentAt >= Protocol.HEARTBEAT_INTERVAL_MS) {
            lastPingSentAt = now
            send(WsCodec.encode(PingMsg(ts = wallClock())))
        }
    }

    private suspend fun onClosed(code: Int, reason: String, error: Throwable?) {
        onClosedInternal(code, reason, error, closeSocket = false)
    }

    private suspend fun onClosedInternal(code: Int, reason: String, error: Throwable?, closeSocket: Boolean) {
        val wasWelcomed = welcomed
        teardown(closeSocket)
        if (wasWelcomed) handler.onDisconnected()
        if (error != null) log.w(TAG, "connection failed: $reason") else log.i(TAG, "connection closed: $code $reason")
        when (code) {
            CloseCodes.UNAUTHORIZED -> {
                _state.value = ConnectionState.Blocked(BlockReason.UNAUTHORIZED)
                callbacks.onUnauthorized()
            }
            CloseCodes.VERSION_UNSUPPORTED -> {
                _state.value = ConnectionState.Blocked(BlockReason.UPDATE_REQUIRED)
                callbacks.onUpdateRequired()
            }
            CloseCodes.TOO_MANY_CONNECTIONS -> _state.value = ConnectionState.Blocked(BlockReason.TOO_MANY_CONNECTIONS)
            else -> scheduleRetry(if (error != null) reason else "closed $code")
        }
    }

    private fun scheduleRetry(lastError: String?) {
        val delayMs = Backoff.reconnectDelayMs(attempt, random)
        val currentAttempt = attempt
        attempt++
        val token = ++retryToken
        _state.value = ConnectionState.Waiting(wallClock() + delayMs, currentAttempt, lastError)
        retryJob?.cancel()
        retryJob = scope.launch {
            delay(delayMs)
            post(Event.RetryDue(token))
        }
    }

    private suspend fun connectNow(resetAttempt: Boolean) {
        if (resetAttempt) attempt = 0
        val wasWelcomed = welcomed
        teardown(closeSocket = true)
        if (wasWelcomed) handler.onDisconnected()
        val gen = ++generation
        _state.value = ConnectionState.Connecting(attempt)
        val listener = object : WsListener {
            override fun onOpen() = post(Event.Opened(gen))
            override fun onMessage(text: String) = post(Event.Message(gen, text))
            override fun onClosed(code: Int, reason: String) = post(Event.Closed(gen, code, reason))
            override fun onFailure(error: Throwable, httpStatus: Int?) = post(Event.Failed(gen, error, httpStatus))
        }
        socket = try {
            transport.open(listener)
        } catch (e: Exception) {
            post(Event.Failed(gen, e, null))
            null
        }
    }

    private suspend fun shutdown(code: Int, reason: String) {
        val wasWelcomed = welcomed
        socket?.close(code, reason)
        socket = null
        teardown(closeSocket = false)
        if (wasWelcomed) handler.onDisconnected()
    }

    private fun teardown(closeSocket: Boolean) {
        retryToken++
        retryJob?.cancel()
        retryJob = null
        heartbeatJob?.cancel()
        heartbeatJob = null
        if (closeSocket) socket?.cancel()
        socket = null
        generation++
        welcomed = false
        online.clear()
    }

    private fun startHeartbeat(gen: Int) {
        heartbeatJob?.cancel()
        heartbeatJob = scope.launch {
            while (isActive) {
                delay(TICK_MS)
                post(Event.Tick(gen))
            }
        }
    }

    private fun send(text: String) {
        socket?.send(text)
    }

    companion object {
        private const val TAG = "ws"
        const val TICK_MS = 1_000L
    }
}
