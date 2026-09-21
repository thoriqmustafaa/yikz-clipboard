package dev.yikz.clipboard.sync

import android.content.ClipData
import android.widget.Toast
import dev.yikz.clipboard.AppGraph
import dev.yikz.clipboard.BuildConfig
import dev.yikz.clipboard.core.ApiException
import dev.yikz.clipboard.core.CachedItem
import dev.yikz.clipboard.core.ConnectionCallbacks
import dev.yikz.clipboard.core.ConnectionManager
import dev.yikz.clipboard.core.ConnectionState
import dev.yikz.clipboard.core.Device
import dev.yikz.clipboard.core.HelloMsg
import dev.yikz.clipboard.core.ItemApplier
import dev.yikz.clipboard.core.ItemCodec
import dev.yikz.clipboard.core.Kdf
import dev.yikz.clipboard.core.LoginRequest
import dev.yikz.clipboard.core.LoginResponse
import dev.yikz.clipboard.core.MasterKey
import dev.yikz.clipboard.core.PreparedClip
import dev.yikz.clipboard.core.Progress
import dev.yikz.clipboard.core.Protocol
import dev.yikz.clipboard.core.StorageInfo
import dev.yikz.clipboard.core.StorageWarningMsg
import dev.yikz.clipboard.core.StrictBase64
import dev.yikz.clipboard.core.SyncCallbacks
import dev.yikz.clipboard.core.SyncEngine
import dev.yikz.clipboard.core.Transfers
import dev.yikz.clipboard.core.UploadResult
import dev.yikz.clipboard.core.UploadSource
import dev.yikz.clipboard.data.AppSettings
import dev.yikz.clipboard.data.Http
import dev.yikz.clipboard.data.HttpApi
import dev.yikz.clipboard.data.OkHttpWsTransport
import dev.yikz.clipboard.service.SyncService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import okhttp3.HttpUrl
import java.util.concurrent.atomic.AtomicBoolean

enum class AuthState { SIGNED_OUT, NEEDS_KEY, NEEDS_PERMISSIONS, READY }

sealed interface UnlockResult {
    data object Ok : UnlockResult
    data object WrongPassword : UnlockResult
}

class UnsupportedServerException(message: String) : Exception(message)

class SyncController(private val g: AppGraph) {
    private val log = g.log
    private val _settings = MutableStateFlow(runBlocking { g.settings.current() })
    val settings: StateFlow<AppSettings> = _settings.asStateFlow()
    private val _connection = MutableStateFlow<ConnectionState>(ConnectionState.Idle)
    val connection: StateFlow<ConnectionState> = _connection.asStateFlow()
    private val _syncing = MutableStateFlow(false)
    val syncing: StateFlow<Boolean> = _syncing.asStateFlow()
    private val _storageWarning = MutableStateFlow<StorageWarningMsg?>(null)
    val storageWarning: StateFlow<StorageWarningMsg?> = _storageWarning.asStateFlow()
    private val _devicesVersion = MutableStateFlow(0)
    val devicesVersion: StateFlow<Int> = _devicesVersion.asStateFlow()
    private val _deviceNames = MutableStateFlow<Map<String, String>>(emptyMap())
    val deviceNames: StateFlow<Map<String, String>> = _deviceNames.asStateFlow()
    private val _auth = MutableStateFlow(computeAuth())
    val auth: StateFlow<AuthState> = _auth.asStateFlow()
    private val signingOut = AtomicBoolean(false)

    @Volatile
    private var session: Session? = null

    init {
        g.scope.launch {
            g.settings.flow.collect { _settings.value = it }
        }
    }

    inner class Session(
        val baseUrl: HttpUrl,
        val deviceId: String,
        val api: HttpApi,
        val key: MasterKey,
        val codec: ItemCodec,
        val scope: CoroutineScope,
    ) {
        val transfers = Transfers(api, codec, g.echo, { g.db.newestContentHash() }, log)
        val engine: SyncEngine
        val manager: ConnectionManager

        init {
            engine = SyncEngine(
                api = api,
                store = g.db,
                codec = codec,
                ownDeviceId = deviceId,
                saltB64 = settings.value.salt,
                applier = Applier(this),
                echo = g.echo,
                scope = scope,
                clock = System::currentTimeMillis,
                log = log,
                callbacks = syncCallbacks,
            )
            manager = ConnectionManager(
                transport = OkHttpWsTransport(baseUrl) { tokenCache },
                handler = engine,
                scope = scope,
                hello = { HelloMsg(deviceId = deviceId, lastSeq = g.db.state().lastSeq, appVersion = BuildConfig.VERSION_NAME) },
                clock = android.os.SystemClock::elapsedRealtime,
                wallClock = System::currentTimeMillis,
                log = log,
                callbacks = connectionCallbacks,
            )
        }
    }

    @Volatile
    private var tokenCache: String? = null

    private inner class Applier(private val s: Session) : ItemApplier {
        override fun autoDownloadLimit(): Long = settings.value.autoDownloadBytes
        override fun isKindEnabled(kind: String): Boolean = settings.value.kindEnabled(kind)

        override suspend fun prepare(item: CachedItem): PreparedClip {
            var lastUpdate = 0L
            val progress = Progress { done, total ->
                val now = System.currentTimeMillis()
                if (!item.isInline && now - lastUpdate > 500) {
                    lastUpdate = now
                    g.notifications.downloadProgress(item, done, total)
                }
            }
            val content = try {
                g.content.materialize(item, s.transfers, progress)
            } finally {
                if (!item.isInline) g.notifications.cancel(item.id.hashCode())
            }
            return object : PreparedClip {
                override suspend fun write() {
                    g.clipboard.write(item.id, content)
                }
            }
        }

        override fun offerDownload(item: CachedItem) {
            g.notifications.offerDownload(item, deviceName(item.deviceId))
        }
    }

    private val syncCallbacks = object : SyncCallbacks {
        override fun onUnauthorized() = handleUnauthorized()
        override fun onKeyInvalid() = handleKeyInvalid()
        override fun onStorageWarning(message: StorageWarningMsg) {
            _storageWarning.value = if (message.active) message else null
            if (message.active) {
                g.notifications.alert(Notifications.ALERT_STORAGE, "Server disk is almost full", "Uploads larger than 1 MB are rejected until space is freed.")
            } else {
                g.notifications.cancel(Notifications.ALERT_STORAGE)
            }
        }

        override fun onDevicesChanged() {
            _devicesVersion.value++
            refreshDeviceNames()
        }

        override fun onApplied(item: CachedItem) {
            log.i("sync", "applied ${item.kind} from ${deviceName(item.deviceId)} to clipboard")
        }

        override fun onReleaseAvailable(version: String) {
            g.updates.onReleaseAvailable(version)
        }
    }

    private val connectionCallbacks = object : ConnectionCallbacks {
        override fun onUnauthorized() = handleUnauthorized()
        override fun onUpdateRequired() {
            g.notifications.alert(Notifications.ALERT_SIGNED_OUT, "Update required", "This server uses a newer protocol. Update Yikz Clipboard to keep syncing.")
        }
    }

    private fun computeAuth(): AuthState = when {
        g.secure.token == null -> AuthState.SIGNED_OUT
        g.secure.masterKey == null -> AuthState.NEEDS_KEY
        !_settings.value.onboarded -> AuthState.NEEDS_PERMISSIONS
        else -> AuthState.READY
    }

    fun refreshAuth() {
        _auth.value = computeAuth()
    }

    private suspend fun reloadSettings() {
        _settings.value = g.settings.current()
    }

    fun deviceName(id: String): String = _deviceNames.value[id] ?: if (id == settings.value.deviceId) "This device" else "Unknown device"

    fun currentSession(): Session? = synchronized(this) { session ?: buildSession() }

    private fun buildSession(): Session? {
        val s = settings.value
        val token = g.secure.token ?: return null
        val keyBytes = g.secure.masterKey ?: return null
        val base = Http.normalizeServerUrl(s.serverUrl) ?: return null
        if (s.deviceId.isEmpty() || s.salt.isEmpty()) return null
        tokenCache = token
        val key = MasterKey(keyBytes)
        val codec = ItemCodec(key)
        g.db.attach(codec)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val created = Session(base, s.deviceId, HttpApi(base) { tokenCache }, key, codec, scope)
        session = created
        scope.launch {
            created.manager.state.collect { state ->
                _connection.value = state
                if (state is ConnectionState.Connected) {
                    val names = _deviceNames.value.toMutableMap()
                    state.onlineDevices.forEach { names[it.deviceId] = it.name }
                    _deviceNames.value = names
                }
            }
        }
        scope.launch { created.engine.syncing.collect { _syncing.value = it } }
        created.manager.launch()
        refreshDeviceNames()
        return created
    }

    private fun stopSession() {
        synchronized(this) {
            session?.let {
                it.manager.stop()
                val scope = it.scope
                g.scope.launch {
                    kotlinx.coroutines.delay(300)
                    scope.cancel()
                }
            }
            session = null
        }
        _connection.value = ConnectionState.Idle
        _syncing.value = false
    }

    fun start() {
        val s = currentSession() ?: return
        if (settings.value.paused) s.manager.pause() else s.manager.start()
    }

    fun shutdown() = stopSession()

    fun onForeground() {
        val s = session ?: return
        s.manager.foreground()
        if (_connection.value == ConnectionState.Idle && !settings.value.paused) s.manager.start()
    }

    fun onNetworkChanged() {
        session?.manager?.networkChanged()
    }

    fun onDeviceWake() {
        session?.manager?.foreground()
    }

    fun reconnectNow() {
        session?.manager?.reconnectNow()
    }

    fun pause() {
        g.scope.launch {
            g.settings.setPaused(true)
            reloadSettings()
            session?.manager?.pause()
            log.i("sync", "paused")
        }
    }

    fun resume() {
        g.scope.launch {
            g.settings.setPaused(false)
            reloadSettings()
            val s = currentSession() ?: return@launch
            s.manager.resume()
            log.i("sync", "resumed")
        }
    }

    private fun refreshDeviceNames() {
        val s = session ?: return
        s.scope.launch {
            try {
                val list = s.api.devices()
                _deviceNames.value = _deviceNames.value + list.associate { it.id to it.name }
            } catch (e: ApiException) {
                if (e.isUnauthorized) handleUnauthorized()
            } catch (_: Exception) {
            }
        }
    }

    private fun handleUnauthorized() {
        if (!signingOut.compareAndSet(false, true)) return
        g.scope.launch {
            log.w("auth", "token rejected by server, signing out")
            try {
                signOut(remote = false)
                g.notifications.alert(Notifications.ALERT_SIGNED_OUT, "Signed out", "This device was revoked or its token expired. Open the app to sign in again.")
            } finally {
                signingOut.set(false)
            }
        }
    }

    private fun handleKeyInvalid() {
        g.scope.launch {
            log.w("auth", "encryption key no longer matches the server")
            stopSession()
            g.secure.masterKey = null
            g.db.clearAll()
            g.content.clear()
            g.thumbnails.clear()
            refreshAuth()
            g.notifications.alert(Notifications.ALERT_SIGNED_OUT, "Encryption password needed", "The server was reset. Open the app and enter the encryption password again.")
        }
    }

    suspend fun login(serverUrl: String, username: String, password: String, deviceName: String): LoginResponse {
        val base = Http.normalizeServerUrl(serverUrl) ?: throw IllegalArgumentException("Enter a valid server URL")
        val api = HttpApi(base) { null }
        val previous = settings.value
        val sameServer = Http.normalizeServerUrl(previous.serverUrl) == base
        val response = api.login(
            LoginRequest(
                username = username.trim(),
                password = password,
                deviceName = deviceName.trim(),
                platform = Protocol.PLATFORM_ANDROID,
                deviceId = previous.deviceId.takeIf { sameServer && it.isNotEmpty() },
            ),
        )
        if (response.kdf.algorithm != Protocol.KDF_ALGORITHM || response.kdf.keyLength != Protocol.KEY_LENGTH) {
            throw UnsupportedServerException("The server uses an unsupported key derivation")
        }
        if (response.protocolVersion != Protocol.VERSION) {
            throw UnsupportedServerException("The server speaks protocol ${response.protocolVersion}. Update the app.")
        }
        stopSession()
        g.secure.token = response.token
        g.secure.masterKey = null
        g.settings.saveAccount(base.toString(), response.username, response.deviceId, deviceName.trim(), response.salt)
        reloadSettings()
        refreshAuth()
        log.i("auth", "signed in as ${response.username}, device ${response.deviceId}")
        return response
    }

    suspend fun serverHasKeyCheck(): Boolean {
        val base = Http.normalizeServerUrl(settings.value.serverUrl) ?: throw IllegalStateException("No server")
        val token = g.secure.token ?: throw IllegalStateException("Not signed in")
        return HttpApi(base) { token }.me().keyCheck != null
    }

    suspend fun unlock(password: String): UnlockResult {
        val base = Http.normalizeServerUrl(settings.value.serverUrl) ?: throw IllegalStateException("No server")
        val token = g.secure.token ?: throw IllegalStateException("Not signed in")
        val api = HttpApi(base) { token }
        val me = api.me()
        if (me.kdf.algorithm != Protocol.KDF_ALGORITHM || me.kdf.keyLength != Protocol.KEY_LENGTH || me.protocolVersion != Protocol.VERSION) {
            throw UnsupportedServerException("This server needs a newer app version")
        }
        val salt = StrictBase64.decode(me.salt)
        if (salt.size != Protocol.SALT_LENGTH) throw UnsupportedServerException("Invalid account salt")
        val keyBytes = withContext(Dispatchers.Default) { Kdf.derive(password, salt, me.kdf.iterations.coerceAtLeast(Protocol.KDF_ITERATIONS)) }
        val key = MasterKey(keyBytes)
        val stored = me.keyCheck
        if (stored == null) {
            try {
                api.putKeyCheck(key.keyCheck)
            } catch (e: ApiException) {
                if (e.code != "key_check_exists") throw e
                if (api.me().keyCheck != key.keyCheck) return UnlockResult.WrongPassword
            }
        } else if (stored != key.keyCheck) {
            return UnlockResult.WrongPassword
        }
        g.secure.masterKey = keyBytes
        val s = settings.value
        g.settings.saveAccount(s.serverUrl, me.username, me.device.id, me.device.name, me.salt)
        reloadSettings()
        refreshAuth()
        log.i("auth", "encryption key verified")
        if (settings.value.onboarded) SyncService.start(g.app)
        return UnlockResult.Ok
    }

    suspend fun completeOnboarding() {
        g.settings.setOnboarded(true)
        reloadSettings()
        refreshAuth()
        SyncService.start(g.app)
    }

    suspend fun signOut(remote: Boolean) {
        val s = session
        stopSession()
        if (remote && s != null) {
            try {
                s.api.logout()
            } catch (_: Exception) {
            }
        }
        tokenCache = null
        g.secure.token = null
        g.secure.masterKey = null
        g.db.clearAll()
        g.db.attach(null)
        g.content.clear()
        g.thumbnails.clear()
        g.echo.clear()
        g.settings.clearAccount()
        reloadSettings()
        refreshAuth()
        SyncService.stop(g.app)
        log.i("auth", "signed out")
    }

    private fun requireSession(): Session = currentSession() ?: throw IllegalStateException("Not signed in")

    private suspend fun <T> guarded(block: suspend (Session) -> T): T {
        val s = requireSession()
        try {
            return block(s)
        } catch (e: ApiException) {
            if (e.isUnauthorized) handleUnauthorized()
            throw e
        }
    }

    suspend fun send(source: UploadSource, force: Boolean = false, progress: Progress = Progress { _, _ -> }): UploadResult = guarded { s ->
        val result = try {
            s.transfers.upload(source, force, progress)
        } catch (e: ApiException) {
            if (e.code != "key_check_missing") throw e
            try {
                s.api.putKeyCheck(s.key.keyCheck)
            } catch (conflict: ApiException) {
                if (conflict.code == "key_check_exists" && s.api.me().keyCheck != s.key.keyCheck) {
                    handleKeyInvalid()
                }
                throw conflict
            }
            s.transfers.upload(source, force, progress)
        }
        if (result is UploadResult.Sent) {
            g.db.upsert(listOf(s.codec.decode(result.header)))
            log.i("send", "sent ${result.header.kind} ${result.header.id} (${result.header.size} bytes)")
        } else if (result is UploadResult.Skipped) {
            log.i("send", "skipped: ${result.reason}")
        }
        result
    }

    fun sendClip(clip: ClipData?, manual: Boolean): Job = g.scope.launch {
        val read = try {
            g.clipboard.read(clip)
        } catch (e: Exception) {
            log.e("send", "cannot read clipboard", e)
            ClipRead.Skip("Cannot read clipboard")
        }
        when (read) {
            is ClipRead.Skip -> {
                log.i("send", "clipboard not sent: ${read.reason}")
                if (manual) toast(read.reason)
            }
            is ClipRead.Ready -> {
                if (!settings.value.kindEnabled(read.source.kind)) {
                    if (manual) toast("${read.label} sync is turned off")
                    return@launch
                }
                try {
                    when (send(read.source)) {
                        is UploadResult.Sent -> if (manual) toast("${read.label} sent")
                        is UploadResult.Skipped -> if (manual) toast("Already synced")
                    }
                } catch (e: Exception) {
                    log.e("send", "upload failed", e)
                    if (manual) toast(errorMessage(e))
                }
            }
        }
    }

    suspend fun toast(text: String) = withContext(Dispatchers.Main) {
        Toast.makeText(g.app, text, Toast.LENGTH_SHORT).show()
    }

    suspend fun materialize(item: CachedItem, progress: Progress = Progress { _, _ -> }): Materialized = guarded { s ->
        g.content.materialize(item, s.transfers, progress)
    }

    suspend fun copyToClipboard(item: CachedItem, progress: Progress = Progress { _, _ -> }) {
        val content = materialize(item, progress)
        g.echo.add(item.contentHash)
        g.clipboard.write(item.id, content)
    }

    fun downloadAndApply(id: String) {
        g.scope.launch {
            val s = currentSession() ?: return@launch
            val item = g.db.get(id) ?: return@launch
            val ok = try {
                s.engine.applyNow(item)
            } catch (e: Exception) {
                log.e("sync", "download of $id failed", e)
                false
            }
            g.notifications.downloadFinished(item, ok)
        }
    }

    suspend fun setPinned(item: CachedItem, pinned: Boolean) = guarded { s ->
        s.api.pin(item.id, pinned)
        g.db.setPinned(item.id, pinned)
    }

    suspend fun delete(id: String) = guarded { s ->
        s.api.deleteItem(id)
        g.db.delete(listOf(id))
    }

    suspend fun loadOlder(): Boolean = guarded { s -> s.engine.loadOlder() }

    suspend fun devices(): List<Device> = guarded { s ->
        s.api.devices().also { list -> _deviceNames.value = _deviceNames.value + list.associate { it.id to it.name } }
    }

    suspend fun renameDevice(id: String, name: String): Device = guarded { s ->
        s.api.renameDevice(id, name.trim()).also {
            if (id == settings.value.deviceId) {
                g.settings.setDeviceName(it.name)
                reloadSettings()
            }
            _deviceNames.value = _deviceNames.value + (it.id to it.name)
        }
    }

    suspend fun revokeDevice(id: String) = guarded { s -> s.api.deleteDevice(id) }

    suspend fun storage(): StorageInfo = guarded { s -> s.api.storage() }

    fun thumbnailSession(): Session? = session

    companion object {
        fun errorMessage(e: Throwable): String = when (e) {
            is ApiException -> when (e.code) {
                "network" -> "Cannot reach the server"
                "invalid_credentials" -> "Wrong username or password"
                "rate_limited" -> "Too many attempts. Try again in ${((e.retryAfterSeconds ?: 60) + 59) / 60} min."
                "item_too_large" -> "Not enough space on the server for this item"
                "disk_low" -> "Server disk is almost full. Try again later."
                "pinned_limit" -> "Pinned storage limit reached"
                "unauthorized" -> "Signed out"
                "invalid_response" -> "Unexpected server response"
                else -> e.message ?: "Server error (${e.status})"
            }
            is UnsupportedServerException -> e.message ?: "Unsupported server"
            is IllegalArgumentException -> e.message ?: "Invalid input"
            is dev.yikz.clipboard.core.IntegrityException -> "Content failed verification"
            is dev.yikz.clipboard.core.CryptoException -> "Content could not be decrypted"
            else -> e.message ?: e.javaClass.simpleName
        }
    }
}
