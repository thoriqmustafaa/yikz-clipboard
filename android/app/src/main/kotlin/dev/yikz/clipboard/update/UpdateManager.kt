package dev.yikz.clipboard.update

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.os.Build
import android.provider.Settings
import androidx.core.content.IntentCompat
import androidx.core.net.toUri
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ProcessLifecycleOwner
import dev.yikz.clipboard.AppGraph
import dev.yikz.clipboard.BuildConfig
import dev.yikz.clipboard.core.ApiException
import dev.yikz.clipboard.core.LatestRelease
import dev.yikz.clipboard.core.ReleaseVerifier
import dev.yikz.clipboard.core.Releases
import dev.yikz.clipboard.core.SemVer
import dev.yikz.clipboard.core.UpdateDecision
import dev.yikz.clipboard.core.UpdatePolicy
import dev.yikz.clipboard.core.VerifyResult
import dev.yikz.clipboard.data.Http
import dev.yikz.clipboard.data.HttpApi
import dev.yikz.clipboard.sync.SyncController
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException

sealed interface UpdateStatus {
    data object Idle : UpdateStatus
    data object Checking : UpdateStatus
    data object UpToDate : UpdateStatus
    data class Available(val version: String) : UpdateStatus
    data class Downloading(val version: String, val percent: Int) : UpdateStatus
    data class Verifying(val version: String) : UpdateStatus
    data class ReadyToInstall(val version: String) : UpdateStatus
    data class NeedsPermission(val version: String) : UpdateStatus
    data class Installing(val version: String) : UpdateStatus
    data class Error(val reason: String) : UpdateStatus
}

enum class UpdateTrigger { STARTUP, PERIODIC, PUSH, MANUAL }

class UpdateManager(private val g: AppGraph) {
    private val context: Context = g.app
    private val log = g.log
    private val mutex = Mutex()
    private val dir = File(context.cacheDir, "updates")
    private val _status = MutableStateFlow<UpdateStatus>(UpdateStatus.Idle)
    val status: StateFlow<UpdateStatus> = _status.asStateFlow()
    private val _latest = MutableStateFlow<LatestRelease?>(null)
    val latest: StateFlow<LatestRelease?> = _latest.asStateFlow()

    @Volatile
    private var ready: Pair<LatestRelease, File>? = null

    @Volatile
    private var notifiedAvailable: String? = null

    val currentVersion: String get() = BuildConfig.VERSION_NAME

    fun isDue(now: Long = System.currentTimeMillis()): Boolean {
        val last = g.sync.settings.value.lastUpdateCheckMs
        return last <= 0 || now - last >= CHECK_INTERVAL_MS || now < last
    }

    fun checkAsync(trigger: UpdateTrigger): Job = g.scope.launch { check(trigger) }

    fun checkIfDue() {
        if (isDue()) checkAsync(UpdateTrigger.PERIODIC)
    }

    fun onReleaseAvailable(version: String) {
        if (!SemVer.isNewer(version, currentVersion)) {
            log.d(TAG, "release_available $version is not newer than $currentVersion")
            return
        }
        log.i(TAG, "server announced release $version")
        checkAsync(UpdateTrigger.PUSH)
    }

    suspend fun check(trigger: UpdateTrigger) {
        if (trigger == UpdateTrigger.MANUAL) {
            mutex.withLock { runCheck(trigger) }
        } else {
            if (!mutex.tryLock()) return
            try {
                runCheck(trigger)
            } finally {
                mutex.unlock()
            }
        }
    }

    fun installNow(): Job = g.scope.launch {
        mutex.withLock {
            val release = _latest.value ?: return@withLock
            if (UpdatePolicy.decide(currentVersion, release) is UpdateDecision.Available) downloadAndInstall(release)
        }
    }

    suspend fun refreshNotes(): Boolean {
        if (_latest.value != null) return true
        val api = api() ?: return false
        return try {
            val release = withContext(Dispatchers.IO) { api.latestRelease(Releases.PLATFORM_ANDROID) }
            if (_latest.value == null) _latest.value = release
            true
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            log.w(TAG, "cannot load release notes: ${SyncController.errorMessage(e)}")
            false
        }
    }

    fun onPermissionMaybeChanged() {
        val current = _status.value
        if (current is UpdateStatus.NeedsPermission && canInstall()) {
            g.notifications.cancel(NOTIFICATION_ID)
            installNow()
        }
    }

    fun cleanup() {
        ready = null
        dir.deleteRecursively()
        g.notifications.cancel(NOTIFICATION_ID)
    }

    private fun api(): HttpApi? {
        val base = Http.normalizeServerUrl(g.sync.settings.value.serverUrl) ?: return null
        if (g.secure.token == null) return null
        return HttpApi(base) { g.secure.token }
    }

    private suspend fun runCheck(trigger: UpdateTrigger) {
        val api = api()
        if (api == null) {
            if (trigger == UpdateTrigger.MANUAL) _status.value = UpdateStatus.Error("Not signed in")
            return
        }
        val previous = _status.value
        _status.value = UpdateStatus.Checking
        val release = try {
            withContext(Dispatchers.IO) { api.latestRelease(Releases.PLATFORM_ANDROID) }
        } catch (e: CancellationException) {
            _status.value = previous
            throw e
        } catch (e: Exception) {
            log.w(TAG, "update check failed: ${SyncController.errorMessage(e)}")
            _status.value = UpdateStatus.Error(if (e is ApiException && e.isUnauthorized) "Signed out" else SyncController.errorMessage(e))
            return
        }
        g.settings.setLastUpdateCheck(System.currentTimeMillis())
        when (val decision = UpdatePolicy.decide(currentVersion, release)) {
            UpdateDecision.NoRelease -> {
                _latest.value = null
                _status.value = UpdateStatus.UpToDate
                log.d(TAG, "no release published for android")
            }
            is UpdateDecision.UpToDate -> {
                _latest.value = decision.release
                _status.value = UpdateStatus.UpToDate
                log.d(TAG, "up to date ($currentVersion, latest ${decision.release.version})")
            }
            is UpdateDecision.Rejected -> {
                _latest.value = decision.release
                _status.value = UpdateStatus.Error(decision.reason)
                log.w(TAG, "release ${decision.release.version} rejected: ${decision.reason}")
            }
            is UpdateDecision.Available -> {
                val r = decision.release
                _latest.value = r
                log.i(TAG, "update ${r.version} available (current $currentVersion, trigger ${trigger.name.lowercase()})")
                if (!g.sync.settings.value.autoInstallUpdates) {
                    _status.value = UpdateStatus.Available(r.version)
                    if (trigger != UpdateTrigger.MANUAL && notifiedAvailable != r.version && !isForeground()) {
                        notifiedAvailable = r.version
                        g.notifications.updateAvailable(NOTIFICATION_ID, r.version)
                    }
                    return
                }
                downloadAndInstall(r)
            }
        }
    }

    private suspend fun downloadAndInstall(release: LatestRelease) {
        val file = try {
            prepare(release) ?: return
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            log.e(TAG, "download of ${release.version} failed", e)
            _status.value = UpdateStatus.Error("Download failed: ${SyncController.errorMessage(e)}")
            return
        }
        ready = release to file
        _status.value = UpdateStatus.ReadyToInstall(release.version)
        install(release, file)
    }

    private suspend fun prepare(release: LatestRelease): File? = withContext(Dispatchers.IO) {
        val asset = release.asset
        dir.mkdirs()
        val target = File(dir, asset.file)
        dir.listFiles()?.filter { it.name != target.name }?.forEach { it.delete() }
        val cached = ready?.takeIf { it.first.version == release.version && it.second == target && target.isFile }
        if (cached != null || (target.isFile && target.length() == asset.size)) {
            _status.value = UpdateStatus.Verifying(release.version)
            if (ReleaseVerifier.verifyFile(target, asset) == VerifyResult.Ok && checkArchive(target, release)) return@withContext target
            target.delete()
        }
        download(release, target)
        _status.value = UpdateStatus.Verifying(release.version)
        when (val result = ReleaseVerifier.verifyFile(target, asset)) {
            VerifyResult.Ok -> Unit
            is VerifyResult.Failed -> {
                target.delete()
                log.e(TAG, "verification of ${asset.file} failed: ${result.reason}")
                _status.value = UpdateStatus.Error("Verification failed: ${result.reason}")
                return@withContext null
            }
        }
        if (!checkArchive(target, release)) {
            target.delete()
            _status.value = UpdateStatus.Error("The downloaded APK is not a valid update")
            return@withContext null
        }
        log.i(TAG, "downloaded and verified ${asset.file} (${asset.size} bytes)")
        target
    }

    private suspend fun download(release: LatestRelease, target: File) {
        val api = api() ?: throw IllegalStateException("Not signed in")
        val asset = release.asset
        val part = File(dir, "${asset.file}.part")
        part.delete()
        _status.value = UpdateStatus.Downloading(release.version, 0)
        log.i(TAG, "downloading ${asset.file}")
        val response = api.openAsset(Releases.assetPath(release))
        try {
            response.use { res ->
                res.body.byteStream().use { input ->
                    part.outputStream().use { out ->
                        val buffer = ByteArray(64 * 1024)
                        var total = 0L
                        var lastPercent = 0
                        while (true) {
                            val n = input.read(buffer)
                            if (n < 0) break
                            total += n
                            if (total > asset.size) throw IOException("download is larger than expected")
                            out.write(buffer, 0, n)
                            val percent = ((total * 100) / asset.size).toInt().coerceIn(0, 100)
                            if (percent != lastPercent) {
                                lastPercent = percent
                                _status.value = UpdateStatus.Downloading(release.version, percent)
                            }
                        }
                    }
                }
            }
            if (!part.renameTo(target)) throw IOException("cannot move downloaded file")
        } catch (e: Exception) {
            part.delete()
            throw e
        }
    }

    @Suppress("DEPRECATION")
    private fun checkArchive(file: File, release: LatestRelease): Boolean {
        val info = context.packageManager.getPackageArchiveInfo(file.absolutePath, 0)
        if (info == null) {
            log.e(TAG, "${file.name} is not a readable APK")
            return false
        }
        if (info.packageName != context.packageName) {
            log.e(TAG, "${file.name} is for package ${info.packageName}")
            return false
        }
        val expected = SemVer.parse(release.version)?.versionCode?.toLong() ?: return false
        if (info.longVersionCode <= BuildConfig.VERSION_CODE.toLong()) {
            log.e(TAG, "${file.name} has versionCode ${info.longVersionCode}, not newer than ${BuildConfig.VERSION_CODE}")
            return false
        }
        if (info.longVersionCode != expected) log.w(TAG, "${file.name} has versionCode ${info.longVersionCode}, expected $expected")
        return true
    }

    fun canInstall(): Boolean = context.packageManager.canRequestPackageInstalls()

    fun unknownSourcesIntent(): Intent =
        Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, "package:${context.packageName}".toUri())

    private suspend fun install(release: LatestRelease, file: File) {
        if (!canInstall()) {
            log.w(TAG, "install unknown apps is not allowed for this app")
            _status.value = UpdateStatus.NeedsPermission(release.version)
            if (!isForeground()) g.notifications.updatePermission(NOTIFICATION_ID, release.version, unknownSourcesIntent())
            return
        }
        _status.value = UpdateStatus.Installing(release.version)
        try {
            withContext(Dispatchers.IO) { commitSession(release, file) }
            log.i(TAG, "install session for ${release.version} committed")
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            log.e(TAG, "cannot start install of ${release.version}", e)
            _status.value = UpdateStatus.Error("Install failed: ${e.message ?: e.javaClass.simpleName}")
        }
    }

    private fun commitSession(release: LatestRelease, file: File) {
        val installer = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(context.packageName)
            setSize(file.length())
            setInstallReason(PackageManager.INSTALL_REASON_USER)
            if (Build.VERSION.SDK_INT >= 31) setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
            if (Build.VERSION.SDK_INT >= 34) setRequestUpdateOwnership(true)
        }
        val sessionId = installer.createSession(params)
        try {
            installer.openSession(sessionId).use { session ->
                session.openWrite("base.apk", 0, file.length()).use { out ->
                    file.inputStream().use { it.copyTo(out, 64 * 1024) }
                    session.fsync(out)
                }
                val intent = Intent(context, InstallStatusReceiver::class.java)
                    .setAction(InstallStatusReceiver.ACTION)
                    .putExtra(InstallStatusReceiver.EXTRA_VERSION, release.version)
                val flags = PendingIntent.FLAG_UPDATE_CURRENT or if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0
                val pending = PendingIntent.getBroadcast(context, sessionId, intent, flags)
                session.commit(pending.intentSender)
            }
        } catch (e: Exception) {
            try {
                installer.abandonSession(sessionId)
            } catch (_: Exception) {
            }
            throw e
        }
    }

    fun onInstallStatus(intent: Intent) {
        val version = intent.getStringExtra(InstallStatusReceiver.EXTRA_VERSION) ?: _latest.value?.version ?: "?"
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        val message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
        when (status) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                val confirm = IntentCompat.getParcelableExtra(intent, Intent.EXTRA_INTENT, Intent::class.java)
                _status.value = UpdateStatus.ReadyToInstall(version)
                if (confirm == null) {
                    log.e(TAG, "install of $version needs confirmation but no intent was provided")
                    return
                }
                confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                log.i(TAG, "install of $version needs user confirmation")
                if (isForeground()) {
                    try {
                        context.startActivity(confirm)
                        return
                    } catch (e: Exception) {
                        log.w(TAG, "cannot open install confirmation: ${e.message}")
                    }
                }
                g.notifications.updateReady(NOTIFICATION_ID, version, confirm)
            }
            PackageInstaller.STATUS_SUCCESS -> {
                log.i(TAG, "update $version installed")
                _status.value = UpdateStatus.Idle
                g.notifications.cancel(NOTIFICATION_ID)
            }
            PackageInstaller.STATUS_FAILURE_ABORTED -> {
                log.i(TAG, "install of $version was cancelled")
                _status.value = UpdateStatus.ReadyToInstall(version)
            }
            else -> {
                val reason = message ?: "status $status"
                log.e(TAG, "install of $version failed: $reason")
                _status.value = UpdateStatus.Error("Install failed: $reason")
                if (status == PackageInstaller.STATUS_FAILURE_CONFLICT || status == PackageInstaller.STATUS_FAILURE_INCOMPATIBLE) {
                    ready?.second?.delete()
                    ready = null
                }
            }
        }
    }

    private fun isForeground(): Boolean = try {
        ProcessLifecycleOwner.get().lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)
    } catch (_: Exception) {
        false
    }

    companion object {
        private const val TAG = "update"
        const val CHECK_INTERVAL_MS = 6L * 60 * 60 * 1000
        const val NOTIFICATION_ID = 4
    }
}
