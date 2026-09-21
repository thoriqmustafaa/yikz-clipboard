package dev.yikz.clipboard.core

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.random.Random

@OptIn(ExperimentalCoroutinesApi::class)
class ConnectionManagerTest {
    class FakeSocket(val listener: WsListener) : WsSocket {
        val sent = ArrayList<String>()
        var closedWith: Int? = null
        var cancelled = false

        override fun send(text: String): Boolean {
            sent += text
            return true
        }

        override fun close(code: Int, reason: String) {
            closedWith = code
        }

        override fun cancel() {
            cancelled = true
        }

        fun sentMessages(): List<WsMessage> = sent.map { WsCodec.decode(it) }
    }

    class FakeTransport : WsTransport {
        val sockets = ArrayList<FakeSocket>()
        override fun open(listener: WsListener): WsSocket = FakeSocket(listener).also { sockets += it }
        val last get() = sockets.last()
    }

    class RecordingHandler : SyncHandler {
        val welcomes = ArrayList<WelcomeMsg>()
        val messages = ArrayList<WsMessage>()
        var disconnects = 0
        override suspend fun onWelcome(welcome: WelcomeMsg) {
            welcomes += welcome
        }

        override suspend fun onMessage(message: WsMessage) {
            messages += message
        }

        override suspend fun onDisconnected() {
            disconnects++
        }
    }

    private class Harness(scope: TestScope) {
        val transport = FakeTransport()
        val handler = RecordingHandler()
        var unauthorized = 0
        val manager = ConnectionManager(
            transport = transport,
            handler = handler,
            scope = scope.backgroundScope,
            hello = { HelloMsg(deviceId = Fixtures.OWN, lastSeq = 7, appVersion = "1.0.0") },
            clock = { scope.testScheduler.currentTime },
            wallClock = { scope.testScheduler.currentTime },
            random = Random(1),
            callbacks = object : ConnectionCallbacks {
                override fun onUnauthorized() {
                    unauthorized++
                }
            },
        )

        val welcome = WelcomeMsg(
            protocolVersion = 1, serverId = "01a05bfb-7000-7691-98e4-301030971d0c", serverTime = "2026-09-21T10:10:00.000Z",
            deviceId = Fixtures.OWN, currentSeq = 7, stateRev = 0,
            onlineDevices = listOf(OnlineDevice(Fixtures.OWN, "Pixel", "android"), OnlineDevice(Fixtures.OTHER, "Mac", "macos")),
        )

        fun connect(scope: TestScope) {
            transport.last.listener.onOpen()
            scope.runCurrent()
            transport.last.listener.onMessage(WsCodec.encode(welcome))
            scope.runCurrent()
        }
    }

    @Test
    fun handshakeSendsHelloAndReportsOnlineDevices() = runTest {
        val h = Harness(this)
        h.manager.launch()
        h.manager.start()
        runCurrent()
        assertEquals(1, h.transport.sockets.size)
        assertTrue(h.manager.state.value is ConnectionState.Connecting)
        h.connect(this)
        val hello = h.transport.last.sentMessages().first() as HelloMsg
        assertEquals(7L, hello.lastSeq)
        assertEquals("android", hello.platform)
        val state = h.manager.state.value as ConnectionState.Connected
        assertEquals(2, state.onlineDevices.size)
        assertEquals(1, h.handler.welcomes.size)
        h.transport.last.listener.onMessage("""{"type":"presence","device_id":"x","name":"PC","platform":"windows","online":true}""")
        runCurrent()
        assertEquals(3, (h.manager.state.value as ConnectionState.Connected).onlineDevices.size)
        h.transport.last.listener.onMessage("""{"type":"presence","device_id":"x","name":"PC","platform":"windows","online":false}""")
        h.transport.last.listener.onMessage("""{"type":"clip_pinned","id":"a","pinned":true,"state_rev":1}""")
        h.transport.last.listener.onMessage("""{"type":"mystery"}""")
        runCurrent()
        assertEquals(2, (h.manager.state.value as ConnectionState.Connected).onlineDevices.size)
        assertEquals(1, h.handler.messages.size)
    }

    @Test
    fun answersServerPingAndSendsOwnPingEvery20Seconds() = runTest {
        val h = Harness(this)
        h.manager.launch()
        h.manager.start()
        runCurrent()
        h.connect(this)
        h.transport.last.listener.onMessage("""{"type":"ping","ts":123}""")
        runCurrent()
        assertEquals(PongMsg(ts = 123), h.transport.last.sentMessages().last())
        advanceTimeBy(19_000)
        runCurrent()
        assertEquals(0, h.transport.last.sentMessages().count { it is PingMsg })
        advanceTimeBy(2_000)
        runCurrent()
        assertEquals(1, h.transport.last.sentMessages().count { it is PingMsg })
        h.transport.last.listener.onMessage("""{"type":"pong","ts":1}""")
        advanceTimeBy(20_000)
        runCurrent()
        assertEquals(2, h.transport.last.sentMessages().count { it is PingMsg })
        assertEquals(1, h.transport.sockets.size)
    }

    @Test
    fun silentConnectionIsDeadAfter45SecondsAndReconnects() = runTest {
        val h = Harness(this)
        h.manager.launch()
        h.manager.start()
        runCurrent()
        h.connect(this)
        advanceTimeBy(44_000)
        runCurrent()
        assertEquals(1, h.transport.sockets.size)
        advanceTimeBy(1_000)
        runCurrent()
        assertTrue(h.transport.sockets[0].cancelled)
        assertTrue(h.manager.state.value !is ConnectionState.Connected)
        assertEquals(1, h.handler.disconnects)
        advanceTimeBy(501)
        runCurrent()
        assertEquals(2, h.transport.sockets.size)
    }

    @Test
    fun backoffGrowsAndResetsAfterWelcome() = runTest {
        val h = Harness(this)
        h.manager.launch()
        h.manager.start()
        runCurrent()
        val attempts = ArrayList<Int>()
        repeat(8) {
            h.transport.last.listener.onFailure(java.io.IOException("refused"), null)
            runCurrent()
            val waiting = h.manager.state.value as ConnectionState.Waiting
            attempts += waiting.attempt
            assertTrue(waiting.retryAtMs - testScheduler.currentTime <= Backoff.reconnectCeilingMs(waiting.attempt))
            advanceTimeBy(30_001)
            runCurrent()
        }
        assertEquals((0..7).toList(), attempts)
        h.connect(this)
        h.transport.last.listener.onClosed(1001, "going away")
        runCurrent()
        assertEquals(0, (h.manager.state.value as ConnectionState.Waiting).attempt)
    }

    @Test
    fun unauthorizedStopsReconnecting() = runTest {
        val h = Harness(this)
        h.manager.launch()
        h.manager.start()
        runCurrent()
        h.connect(this)
        h.transport.last.listener.onClosed(4001, "revoked")
        runCurrent()
        assertEquals(ConnectionState.Blocked(BlockReason.UNAUTHORIZED), h.manager.state.value)
        assertEquals(1, h.unauthorized)
        advanceTimeBy(600_000)
        h.manager.networkChanged()
        h.manager.foreground()
        runCurrent()
        assertEquals(1, h.transport.sockets.size)
    }

    @Test
    fun upgradeRejectedWith401IsUnauthorized() = runTest {
        val h = Harness(this)
        h.manager.launch()
        h.manager.start()
        runCurrent()
        h.transport.last.listener.onFailure(java.net.ProtocolException("Expected HTTP 101"), 401)
        runCurrent()
        assertEquals(ConnectionState.Blocked(BlockReason.UNAUTHORIZED), h.manager.state.value)
    }

    @Test
    fun versionUnsupportedBlocks() = runTest {
        val h = Harness(this)
        h.manager.launch()
        h.manager.start()
        runCurrent()
        h.transport.last.listener.onOpen()
        h.transport.last.listener.onClosed(4003, "unsupported")
        runCurrent()
        assertEquals(ConnectionState.Blocked(BlockReason.UPDATE_REQUIRED), h.manager.state.value)
    }

    @Test
    fun tooManyConnectionsWaitsForForeground() = runTest {
        val h = Harness(this)
        h.manager.launch()
        h.manager.start()
        runCurrent()
        h.connect(this)
        h.transport.last.listener.onClosed(4002, "too many")
        runCurrent()
        assertEquals(ConnectionState.Blocked(BlockReason.TOO_MANY_CONNECTIONS), h.manager.state.value)
        advanceTimeBy(120_000)
        h.manager.networkChanged()
        runCurrent()
        assertEquals(1, h.transport.sockets.size)
        h.manager.foreground()
        runCurrent()
        assertEquals(2, h.transport.sockets.size)
    }

    @Test
    fun networkChangeForcesImmediateReconnect() = runTest {
        val h = Harness(this)
        h.manager.launch()
        h.manager.start()
        runCurrent()
        h.connect(this)
        h.manager.networkChanged()
        runCurrent()
        assertTrue(h.transport.sockets[0].cancelled)
        assertEquals(2, h.transport.sockets.size)
        h.transport.last.listener.onFailure(java.io.IOException("offline"), null)
        runCurrent()
        repeat(5) {
            h.transport.last.listener.onFailure(java.io.IOException("offline"), null)
            advanceTimeBy(30_001)
            runCurrent()
        }
        assertTrue(h.manager.state.value is ConnectionState.Connecting || h.manager.state.value is ConnectionState.Waiting)
        val before = h.transport.sockets.size
        if (h.manager.state.value is ConnectionState.Connecting) {
            h.transport.last.listener.onFailure(java.io.IOException("offline"), null)
            runCurrent()
        }
        h.manager.networkChanged()
        runCurrent()
        assertEquals(ConnectionState.Connecting(0), h.manager.state.value)
        assertTrue(h.transport.sockets.size > before)
        h.transport.sockets[0].listener.onMessage(WsCodec.encode(h.welcome))
        runCurrent()
        assertEquals(ConnectionState.Connecting(0), h.manager.state.value)
    }

    @Test
    fun foregroundProbesLiveConnection() = runTest {
        val h = Harness(this)
        h.manager.launch()
        h.manager.start()
        runCurrent()
        h.connect(this)
        h.manager.foreground()
        runCurrent()
        assertTrue(h.transport.last.sentMessages().last() is PingMsg)
        advanceTimeBy(1_000)
        h.transport.last.listener.onMessage("""{"type":"pong","ts":1}""")
        advanceTimeBy(5_000)
        runCurrent()
        assertEquals(1, h.transport.sockets.size)
        h.manager.foreground()
        advanceTimeBy(5_001)
        runCurrent()
        assertEquals(2, h.transport.sockets.size)
        assertEquals(ConnectionState.Connecting(0), h.manager.state.value)
    }

    @Test
    fun foregroundWhileWaitingConnectsNow() = runTest {
        val h = Harness(this)
        h.manager.launch()
        h.manager.start()
        runCurrent()
        repeat(6) {
            h.transport.last.listener.onFailure(java.io.IOException("x"), null)
            runCurrent()
            advanceTimeBy(30_001)
            runCurrent()
        }
        h.transport.last.listener.onFailure(java.io.IOException("x"), null)
        runCurrent()
        val count = h.transport.sockets.size
        h.manager.foreground()
        runCurrent()
        assertEquals(count + 1, h.transport.sockets.size)
    }

    @Test
    fun pauseAndResume() = runTest {
        val h = Harness(this)
        h.manager.launch()
        h.manager.start()
        runCurrent()
        h.connect(this)
        h.manager.pause()
        runCurrent()
        assertEquals(ConnectionState.Paused, h.manager.state.value)
        assertEquals(1000, h.transport.sockets[0].closedWith)
        h.transport.sockets[0].listener.onClosed(1000, "paused")
        advanceTimeBy(120_000)
        h.manager.networkChanged()
        h.manager.foreground()
        runCurrent()
        assertEquals(1, h.transport.sockets.size)
        h.manager.resume()
        runCurrent()
        assertEquals(2, h.transport.sockets.size)
    }
}
