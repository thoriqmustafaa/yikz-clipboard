package dev.yikz.clipboard

import android.app.Application
import android.content.Context
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.ProcessLifecycleOwner
import dev.yikz.clipboard.core.EchoGuard
import dev.yikz.clipboard.data.AppLog
import dev.yikz.clipboard.data.ClipDatabase
import dev.yikz.clipboard.data.SecureStore
import dev.yikz.clipboard.data.SettingsStore
import dev.yikz.clipboard.sync.ClipboardBridge
import dev.yikz.clipboard.sync.ContentStore
import dev.yikz.clipboard.sync.Notifications
import dev.yikz.clipboard.sync.SyncController
import dev.yikz.clipboard.sync.Thumbnails
import dev.yikz.clipboard.update.UpdateManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import java.io.File

class App : Application() {
    lateinit var graph: AppGraph
        private set

    override fun onCreate() {
        super.onCreate()
        graph = AppGraph(this)
        graph.log.i("app", "started ${BuildConfig.VERSION_NAME} on Android ${android.os.Build.VERSION.RELEASE}")
        ProcessLifecycleOwner.get().lifecycle.addObserver(object : DefaultLifecycleObserver {
            override fun onStart(owner: LifecycleOwner) {
                graph.sync.onForeground()
            }
        })
    }
}

class AppGraph(val app: Application) {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    val log = AppLog(File(app.filesDir, "logs"))
    val settings = SettingsStore(app)
    val secure = SecureStore(app)
    val db = ClipDatabase(app)
    val echo = EchoGuard()
    val notifications = Notifications(app)
    val clipboard = ClipboardBridge(app, log)
    val content = ContentStore(app, db)
    val thumbnails = Thumbnails(app)
    val sync = SyncController(this)
    val updates = UpdateManager(this)
}

val Context.graph: AppGraph get() = (applicationContext as App).graph
