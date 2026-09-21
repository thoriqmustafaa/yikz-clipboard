package dev.yikz.clipboard.ui.screens

import android.Manifest
import android.app.StatusBarManager
import android.content.ClipData
import android.content.ComponentName
import android.content.Intent
import android.graphics.drawable.Icon as AndroidIcon
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowForward
import androidx.compose.material.icons.automirrored.outlined.Logout
import androidx.compose.material.icons.automirrored.outlined.Notes
import androidx.compose.material.icons.outlined.AutoMode
import androidx.compose.material.icons.outlined.BatteryChargingFull
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.Dns
import androidx.compose.material.icons.outlined.Download
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Image
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.Notifications
import androidx.compose.material.icons.outlined.PauseCircle
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material.icons.outlined.PhoneAndroid
import androidx.compose.material.icons.outlined.RadioButtonUnchecked
import androidx.compose.material.icons.outlined.TextFields
import androidx.compose.material.icons.outlined.Tune
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.yikz.clipboard.BuildConfig
import dev.yikz.clipboard.R
import dev.yikz.clipboard.core.Protocol
import dev.yikz.clipboard.graph
import dev.yikz.clipboard.service.LogcatWatcher
import dev.yikz.clipboard.service.SendTileService
import dev.yikz.clipboard.sync.SyncController
import dev.yikz.clipboard.ui.components.SectionLabel
import dev.yikz.clipboard.ui.components.SettingsCard
import dev.yikz.clipboard.ui.components.SettingsRow
import dev.yikz.clipboard.ui.theme.MonoStyle
import dev.yikz.clipboard.util.Format
import kotlinx.coroutines.launch

private val downloadLimits = listOf(
    10L * 1_048_576, 25L * 1_048_576, 50L * 1_048_576, 100L * 1_048_576, 250L * 1_048_576, 1024L * 1_048_576,
)

@Composable
fun SettingsScreen(onOpenLog: () -> Unit) {
    val context = LocalContext.current
    val g = context.graph
    val settings by g.sync.settings.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()
    val tick = rememberResumeTick()
    var notifications by remember { mutableStateOf(hasNotificationPermission(context)) }
    var battery by remember { mutableStateOf(isIgnoringBattery(context)) }
    var readLogs by remember { mutableStateOf(LogcatWatcher.hasReadLogs(context)) }
    var overlay by remember { mutableStateOf(Settings.canDrawOverlays(context)) }
    LaunchedEffect(tick) {
        notifications = hasNotificationPermission(context)
        battery = isIgnoringBattery(context)
        readLogs = LogcatWatcher.hasReadLogs(context)
        overlay = Settings.canDrawOverlays(context)
    }
    val notificationLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { notifications = it }
    var limitDialog by remember { mutableStateOf(false) }
    var renameDialog by remember { mutableStateOf(false) }
    var signOutDialog by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        Text(
            "Settings",
            style = MaterialTheme.typography.headlineLarge,
            modifier = Modifier.statusBarsPadding().padding(start = 20.dp, end = 20.dp, top = 20.dp, bottom = 4.dp),
        )
        message?.let {
            Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(horizontal = 20.dp, vertical = 4.dp))
        }

        SectionLabel("Account")
        SettingsCard {
            SettingsRow("Server", Uri.parse(settings.serverUrl).host ?: settings.serverUrl, Icons.Outlined.Dns)
            Divider()
            SettingsRow("Username", settings.username, Icons.Outlined.Person)
            Divider()
            SettingsRow("Device name", settings.deviceName.ifEmpty { Build.MODEL }, Icons.Outlined.PhoneAndroid, onClick = { renameDialog = true })
        }

        SectionLabel("Sync")
        SettingsCard {
            SettingsRow(
                "Pause sync",
                if (settings.paused) "Nothing is sent or received" else "Items sync in real time",
                Icons.Outlined.PauseCircle,
                onClick = { if (settings.paused) g.sync.resume() else g.sync.pause() },
            ) { Switch(checked = settings.paused, onCheckedChange = { if (it) g.sync.pause() else g.sync.resume() }) }
            Divider()
            SettingsRow("Text and links", null, Icons.Outlined.TextFields) {
                Switch(checked = settings.syncText, onCheckedChange = { v -> scope.launch { g.settings.setSyncText(v) } })
            }
            Divider()
            SettingsRow("Images", null, Icons.Outlined.Image) {
                Switch(checked = settings.syncImages, onCheckedChange = { v -> scope.launch { g.settings.setSyncImages(v) } })
            }
            Divider()
            SettingsRow("Files", null, Icons.Outlined.Folder) {
                Switch(checked = settings.syncFiles, onCheckedChange = { v -> scope.launch { g.settings.setSyncFiles(v) } })
            }
            Divider()
            SettingsRow(
                "Auto-download limit",
                "Larger items ask before downloading: ${Format.bytes(settings.autoDownloadBytes)}",
                Icons.Outlined.Download,
                onClick = { limitDialog = true },
            )
        }
        Text(
            "Type switches apply to items placed on the clipboard automatically and to clipboard sends. The share sheet always sends.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(horizontal = 24.dp, vertical = 8.dp),
        )

        SectionLabel("Sending")
        SettingsCard {
            SettingsRow(
                "Quick Settings tile",
                "Swipe down and tap Send clipboard from anywhere",
                Icons.Outlined.Tune,
                onClick = if (Build.VERSION.SDK_INT >= 33) {
                    { requestTile(context) }
                } else {
                    null
                },
            ) {
                if (Build.VERSION.SDK_INT >= 33) FilledTonalButton(onClick = { requestTile(context) }) { Text("Add") }
            }
        }
        Spacer(Modifier.height(12.dp))
        AutomaticModeCard(
            enabled = settings.autoMode,
            readLogs = readLogs,
            overlay = overlay,
            onToggle = { v -> scope.launch { g.settings.setAutoMode(v) } },
        )

        SectionLabel("Background")
        SettingsCard {
            SettingsRow(
                "Notifications",
                if (notifications) "Allowed" else "Needed for the status notification and actions",
                Icons.Outlined.Notifications,
            ) {
                if (!notifications) {
                    FilledTonalButton(onClick = {
                        if (Build.VERSION.SDK_INT >= 33) notificationLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                    }) { Text("Allow") }
                } else {
                    Icon(Icons.Outlined.CheckCircle, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                }
            }
            Divider()
            SettingsRow(
                "Unrestricted battery",
                if (battery) "Sync keeps running in Doze" else "Android may pause sync in the background",
                Icons.Outlined.BatteryChargingFull,
            ) {
                if (!battery) {
                    FilledTonalButton(onClick = { context.startActivity(batteryExemptionIntent(context)) }) { Text("Allow") }
                } else {
                    Icon(Icons.Outlined.CheckCircle, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                }
            }
        }

        SectionLabel("Diagnostics")
        SettingsCard {
            SettingsRow("Activity log", "Connection events, syncs and errors", Icons.AutoMirrored.Outlined.Notes, onClick = onOpenLog) {
                Icon(Icons.AutoMirrored.Outlined.ArrowForward, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Divider()
            SettingsRow("About", "Version ${BuildConfig.VERSION_NAME}  ·  Protocol ${Protocol.VERSION}", Icons.Outlined.Info)
        }

        Spacer(Modifier.height(20.dp))
        OutlinedButton(
            onClick = { signOutDialog = true },
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp).height(52.dp),
        ) {
            Icon(Icons.AutoMirrored.Outlined.Logout, contentDescription = null, tint = MaterialTheme.colorScheme.error)
            Spacer(Modifier.width(10.dp))
            Text("Sign out", color = MaterialTheme.colorScheme.error)
        }
        Spacer(Modifier.height(32.dp))
    }

    if (limitDialog) {
        AlertDialog(
            onDismissRequest = { limitDialog = false },
            title = { Text("Auto-download limit") },
            text = {
                Column {
                    Text(
                        "Items up to this size go straight to the clipboard. Bigger ones show a notification first.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Spacer(Modifier.height(8.dp))
                    downloadLimits.forEach { limit ->
                        Row(
                            Modifier
                                .fillMaxWidth()
                                .selectable(selected = settings.autoDownloadBytes == limit, role = Role.RadioButton) {
                                    scope.launch { g.settings.setAutoDownload(limit) }
                                    limitDialog = false
                                }
                                .padding(vertical = 4.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            RadioButton(selected = settings.autoDownloadBytes == limit, onClick = null)
                            Spacer(Modifier.width(12.dp))
                            Text(Format.bytes(limit))
                        }
                    }
                }
            },
            confirmButton = { TextButton(onClick = { limitDialog = false }) { Text("Close") } },
        )
    }

    if (renameDialog) {
        var name by remember { mutableStateOf(settings.deviceName.ifEmpty { Build.MODEL }) }
        AlertDialog(
            onDismissRequest = { renameDialog = false },
            title = { Text("Device name") },
            text = { OutlinedTextField(value = name, onValueChange = { name = it }, singleLine = true, modifier = Modifier.fillMaxWidth()) },
            confirmButton = {
                TextButton(enabled = name.isNotBlank() && name.trim().length <= 64, onClick = {
                    renameDialog = false
                    scope.launch {
                        try {
                            g.sync.renameDevice(settings.deviceId, name)
                            message = null
                        } catch (e: Exception) {
                            message = SyncController.errorMessage(e)
                        }
                    }
                }) { Text("Save") }
            },
            dismissButton = { TextButton(onClick = { renameDialog = false }) { Text("Cancel") } },
        )
    }

    if (signOutDialog) {
        AlertDialog(
            onDismissRequest = { signOutDialog = false },
            title = { Text("Sign out?") },
            text = { Text("This device is revoked on the server and its local history is cleared. You need both passwords to sign in again.") },
            confirmButton = {
                TextButton(onClick = {
                    signOutDialog = false
                    scope.launch { g.sync.signOut(remote = true) }
                }) { Text("Sign out", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { signOutDialog = false }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun Divider() {
    HorizontalDivider(Modifier.padding(start = 54.dp), color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
}

private fun requestTile(context: android.content.Context) {
    if (Build.VERSION.SDK_INT < 33) return
    val manager = context.getSystemService(StatusBarManager::class.java)
    manager.requestAddTileService(
        ComponentName(context, SendTileService::class.java),
        "Send clipboard",
        AndroidIcon.createWithResource(context, R.drawable.ic_tile),
        context.mainExecutor,
    ) { }
}

@Composable
private fun AutomaticModeCard(enabled: Boolean, readLogs: Boolean, overlay: Boolean, onToggle: (Boolean) -> Unit) {
    val context = LocalContext.current
    val command = LogcatWatcher.adbCommand(context)
    Surface(
        color = MaterialTheme.colorScheme.surfaceContainerLow,
        shape = MaterialTheme.shapes.large,
        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
    ) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Outlined.AutoMode, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(22.dp))
                Spacer(Modifier.width(16.dp))
                Column(Modifier.weight(1f)) {
                    Text("Automatic mode", style = MaterialTheme.typography.bodyLarge)
                    Text(
                        "Send every copy automatically",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                Switch(checked = enabled && readLogs, enabled = readLogs, onCheckedChange = onToggle)
            }
            Spacer(Modifier.height(12.dp))
            Text(
                "Android blocks background clipboard reads. With log access the app notices when you copy and briefly opens an invisible window to read the clipboard. Android may ask once to allow log access.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Spacer(Modifier.height(12.dp))
            StepRow(done = readLogs, text = "Grant log access once from a computer:")
            Surface(
                color = MaterialTheme.colorScheme.surfaceContainerHighest,
                shape = MaterialTheme.shapes.small,
                modifier = Modifier.fillMaxWidth().padding(start = 32.dp, top = 6.dp, bottom = 10.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(command, style = MonoStyle, modifier = Modifier.weight(1f).padding(start = 12.dp, top = 10.dp, bottom = 10.dp))
                    IconButton(onClick = {
                        val cm = context.getSystemService(android.content.ClipboardManager::class.java)
                        val clip = ClipData.newPlainText("adb command", command)
                        clip.description.extras = android.os.PersistableBundle().apply { putString("dev.yikz.clipboard.origin", "local") }
                        cm.setPrimaryClip(clip)
                    }) { Icon(Icons.Outlined.ContentCopy, contentDescription = "Copy command") }
                }
            }
            StepRow(done = overlay, text = "Allow display over other apps")
            if (!overlay) {
                TextButton(
                    onClick = {
                        context.startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:${context.packageName}")))
                    },
                    modifier = Modifier.padding(start = 20.dp),
                ) { Text("Open settings") }
            }
        }
    }
}

@Composable
private fun StepRow(done: Boolean, text: String) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(
            if (done) Icons.Outlined.CheckCircle else Icons.Outlined.RadioButtonUnchecked,
            contentDescription = if (done) "Done" else "Not done",
            tint = if (done) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline,
            modifier = Modifier.size(20.dp),
        )
        Spacer(Modifier.width(12.dp))
        Text(text, style = MaterialTheme.typography.bodyMedium)
    }
}
