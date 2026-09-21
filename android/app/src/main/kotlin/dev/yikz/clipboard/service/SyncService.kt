package dev.yikz.clipboard.service

import android.app.Service
import android.content.BroadcastReceiver
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import dev.yikz.clipboard.core.ConnectionState
import dev.yikz.clipboard.graph
import dev.yikz.clipboard.sync.Notifications
import dev.yikz.clipboard.update.UpdateTrigger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch

class SyncService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var networkCallback: ConnectivityManager.NetworkCallback? = null
    private var wakeReceiver: BroadcastReceiver? = null
    private var clipListener: ClipboardManager.OnPrimaryClipChangedListener? = null
    private var watcher: LogcatWatcher? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        val g = graph
        promote()
        g.log.i("service", "foreground service created")
        g.content.prune()
        g.sync.start()
        registerNetworkCallback()
        registerWakeReceiver()
        registerClipboardListener()
        scope.launch {
            combine(g.sync.connection, g.sync.settings, g.sync.syncing) { state, settings, syncing ->
                Triple(state, settings.paused, syncing)
            }.distinctUntilChanged().collect { (state, paused, syncing) ->
                val notification = g.notifications.status(state, paused, g.sync.settings.value.deviceId, syncing)
                androidx.core.app.NotificationManagerCompat.from(this@SyncService).let {
                    try {
                        it.notify(Notifications.STATUS_ID, notification)
                    } catch (_: SecurityException) {
                    }
                }
                if (state is ConnectionState.Blocked) g.log.w("service", "connection blocked: ${state.reason}")
            }
        }
        scope.launch {
            delay(UPDATE_START_DELAY_MS)
            g.updates.check(UpdateTrigger.STARTUP)
            while (true) {
                delay(UPDATE_POLL_MS)
                if (g.updates.isDue()) g.updates.check(UpdateTrigger.PERIODIC)
            }
        }
        scope.launch {
            g.sync.settings.map { it.autoMode }.distinctUntilChanged().collect { enabled ->
                watcher?.stop()
                watcher = null
                if (enabled) {
                    if (LogcatWatcher.hasReadLogs(this@SyncService)) {
                        watcher = LogcatWatcher(this@SyncService, g.log, g.clipboard) { launchReader() }.also { it.start() }
                    } else {
                        g.log.w("auto", "automatic mode is on but READ_LOGS is not granted")
                    }
                }
            }
        }
    }

    private fun promote() {
        val g = graph
        val notification = g.notifications.status(g.sync.connection.value, g.sync.settings.value.paused, g.sync.settings.value.deviceId, false)
        val type = if (Build.VERSION.SDK_INT >= 34) ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE else 0
        ServiceCompat.startForeground(this, Notifications.STATUS_ID, notification, type)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        promote()
        val g = graph
        if (g.sync.currentSession() == null) {
            g.log.w("service", "not signed in, stopping service")
            stopSelf()
            return START_NOT_STICKY
        }
        when (intent?.action) {
            ACTION_PAUSE -> g.sync.pause()
            ACTION_RESUME -> g.sync.resume()
            ACTION_DOWNLOAD -> intent.getStringExtra(EXTRA_ID)?.let { g.sync.downloadAndApply(it) }
            ACTION_RECONNECT -> g.sync.reconnectNow()
            else -> g.sync.start()
        }
        return START_STICKY
    }

    private fun launchReader() {
        val g = graph
        if (g.clipboard.recentlyWrote()) return
        if (!android.provider.Settings.canDrawOverlays(this)) {
            g.log.w("auto", "cannot open clipboard reader: display over other apps is not allowed")
            return
        }
        try {
            startActivity(dev.yikz.clipboard.ui.ClipboardReadActivity.intent(this, manual = false))
        } catch (e: Exception) {
            g.log.e("auto", "cannot open clipboard reader", e)
        }
    }

    private fun registerNetworkCallback() {
        val cm = getSystemService(ConnectivityManager::class.java)
        var current: Network? = cm.activeNetwork
        var validated = current?.let { cm.getNetworkCapabilities(it)?.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) } ?: false
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                if (network != current) {
                    current = network
                    validated = false
                    graph.log.i("net", "default network changed")
                    graph.sync.onNetworkChanged()
                }
            }

            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
                val nowValidated = caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
                if (network == current && nowValidated && !validated) {
                    graph.log.i("net", "network validated")
                    graph.sync.onNetworkChanged()
                }
                if (network == current) validated = nowValidated
            }

            override fun onLost(network: Network) {
                if (network == current) {
                    current = null
                    validated = false
                    graph.log.i("net", "network lost")
                }
            }
        }
        cm.registerDefaultNetworkCallback(callback)
        networkCallback = callback
    }

    private fun registerWakeReceiver() {
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) {
                when (intent.action) {
                    Intent.ACTION_SCREEN_ON -> {
                        graph.sync.onDeviceWake()
                        graph.updates.checkIfDue()
                    }
                    PowerManager.ACTION_DEVICE_IDLE_MODE_CHANGED -> {
                        val pm = getSystemService(PowerManager::class.java)
                        if (!pm.isDeviceIdleMode) {
                            graph.log.i("service", "left doze")
                            graph.sync.onDeviceWake()
                            graph.updates.checkIfDue()
                        }
                    }
                }
            }
        }
        val filter = IntentFilter().apply {
            addAction(Intent.ACTION_SCREEN_ON)
            addAction(PowerManager.ACTION_DEVICE_IDLE_MODE_CHANGED)
        }
        ContextCompat.registerReceiver(this, receiver, filter, ContextCompat.RECEIVER_NOT_EXPORTED)
        wakeReceiver = receiver
    }

    private fun registerClipboardListener() {
        val cm = getSystemService(ClipboardManager::class.java)
        val listener = ClipboardManager.OnPrimaryClipChangedListener {
            val g = graph
            if (g.clipboard.ignoreNextChange) {
                g.clipboard.ignoreNextChange = false
                if (g.clipboard.recentlyWrote()) return@OnPrimaryClipChangedListener
            }
            if (!g.sync.settings.value.autoMode || g.clipboard.recentlyWrote()) return@OnPrimaryClipChangedListener
            val clip = g.clipboard.currentClip() ?: return@OnPrimaryClipChangedListener
            g.sync.sendClip(clip, manual = false)
        }
        cm.addPrimaryClipChangedListener(listener)
        clipListener = listener
    }

    override fun onDestroy() {
        val g = graph
        g.log.i("service", "foreground service destroyed")
        networkCallback?.let { getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(it) }
        wakeReceiver?.let { unregisterReceiver(it) }
        clipListener?.let { getSystemService(ClipboardManager::class.java).removePrimaryClipChangedListener(it) }
        watcher?.stop()
        scope.cancel()
        super.onDestroy()
    }

    companion object {
        const val ACTION_PAUSE = "dev.yikz.clipboard.action.PAUSE"
        const val ACTION_RESUME = "dev.yikz.clipboard.action.RESUME"
        const val ACTION_DOWNLOAD = "dev.yikz.clipboard.action.DOWNLOAD"
        const val ACTION_RECONNECT = "dev.yikz.clipboard.action.RECONNECT"
        const val EXTRA_ID = "id"
        private const val UPDATE_START_DELAY_MS = 10_000L
        private const val UPDATE_POLL_MS = 15L * 60 * 1000

        fun start(context: Context, action: String? = null) {
            val intent = Intent(context, SyncService::class.java).setAction(action)
            try {
                ContextCompat.startForegroundService(context, intent)
            } catch (e: Exception) {
                context.graph.log.e("service", "cannot start foreground service", e)
            }
        }

        fun stop(context: Context) {
            context.graph.sync.shutdown()
            context.stopService(Intent(context, SyncService::class.java))
        }
    }
}
