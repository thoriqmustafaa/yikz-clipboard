package dev.yikz.clipboard.data

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import dev.yikz.clipboard.BuildConfig
import dev.yikz.clipboard.core.Kind
import dev.yikz.clipboard.core.Protocol
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

private val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "settings")

data class AppSettings(
    val serverUrl: String = BuildConfig.DEFAULT_SERVER_URL,
    val username: String = "",
    val deviceId: String = "",
    val deviceName: String = "",
    val salt: String = "",
    val paused: Boolean = false,
    val syncText: Boolean = true,
    val syncImages: Boolean = true,
    val syncFiles: Boolean = true,
    val autoDownloadBytes: Long = Protocol.DEFAULT_AUTO_DOWNLOAD_BYTES,
    val autoMode: Boolean = false,
    val onboarded: Boolean = false,
) {
    fun kindEnabled(kind: String): Boolean = when (kind) {
        Kind.TEXT -> syncText
        Kind.IMAGE -> syncImages
        Kind.FILES -> syncFiles
        else -> false
    }
}

class SettingsStore(private val context: Context) {
    private object Keys {
        val serverUrl = stringPreferencesKey("server_url")
        val username = stringPreferencesKey("username")
        val deviceId = stringPreferencesKey("device_id")
        val deviceName = stringPreferencesKey("device_name")
        val salt = stringPreferencesKey("salt")
        val paused = booleanPreferencesKey("paused")
        val syncText = booleanPreferencesKey("sync_text")
        val syncImages = booleanPreferencesKey("sync_images")
        val syncFiles = booleanPreferencesKey("sync_files")
        val autoDownload = longPreferencesKey("auto_download_bytes")
        val autoMode = booleanPreferencesKey("auto_mode")
        val onboarded = booleanPreferencesKey("onboarded")
    }

    val flow: Flow<AppSettings> = context.dataStore.data.map { p ->
        AppSettings(
            serverUrl = p[Keys.serverUrl] ?: BuildConfig.DEFAULT_SERVER_URL,
            username = p[Keys.username].orEmpty(),
            deviceId = p[Keys.deviceId].orEmpty(),
            deviceName = p[Keys.deviceName].orEmpty(),
            salt = p[Keys.salt].orEmpty(),
            paused = p[Keys.paused] ?: false,
            syncText = p[Keys.syncText] ?: true,
            syncImages = p[Keys.syncImages] ?: true,
            syncFiles = p[Keys.syncFiles] ?: true,
            autoDownloadBytes = p[Keys.autoDownload] ?: Protocol.DEFAULT_AUTO_DOWNLOAD_BYTES,
            autoMode = p[Keys.autoMode] ?: false,
            onboarded = p[Keys.onboarded] ?: false,
        )
    }

    suspend fun current(): AppSettings = flow.first()

    suspend fun saveAccount(serverUrl: String, username: String, deviceId: String, deviceName: String, salt: String) {
        context.dataStore.edit {
            it[Keys.serverUrl] = serverUrl
            it[Keys.username] = username
            it[Keys.deviceId] = deviceId
            it[Keys.deviceName] = deviceName
            it[Keys.salt] = salt
        }
    }

    suspend fun setDeviceName(name: String) = context.dataStore.edit { it[Keys.deviceName] = name }
    suspend fun setPaused(value: Boolean) = context.dataStore.edit { it[Keys.paused] = value }
    suspend fun setSyncText(value: Boolean) = context.dataStore.edit { it[Keys.syncText] = value }
    suspend fun setSyncImages(value: Boolean) = context.dataStore.edit { it[Keys.syncImages] = value }
    suspend fun setSyncFiles(value: Boolean) = context.dataStore.edit { it[Keys.syncFiles] = value }
    suspend fun setAutoDownload(value: Long) = context.dataStore.edit { it[Keys.autoDownload] = value }
    suspend fun setAutoMode(value: Boolean) = context.dataStore.edit { it[Keys.autoMode] = value }
    suspend fun setOnboarded(value: Boolean) = context.dataStore.edit { it[Keys.onboarded] = value }

    suspend fun clearAccount() {
        context.dataStore.edit {
            it.remove(Keys.username)
            it.remove(Keys.salt)
            it[Keys.onboarded] = false
            it[Keys.paused] = false
        }
    }
}
