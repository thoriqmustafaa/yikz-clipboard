package dev.yikz.clipboard.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.CloudOff
import androidx.compose.material.icons.outlined.Computer
import androidx.compose.material.icons.outlined.DesktopWindows
import androidx.compose.material.icons.outlined.Language
import androidx.compose.material.icons.outlined.MoreVert
import androidx.compose.material.icons.outlined.PhoneAndroid
import androidx.compose.material.icons.outlined.Storage
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.yikz.clipboard.core.ConnectionState
import dev.yikz.clipboard.core.Device
import dev.yikz.clipboard.core.StorageInfo
import dev.yikz.clipboard.core.Timestamps
import dev.yikz.clipboard.graph
import dev.yikz.clipboard.sync.SyncController
import dev.yikz.clipboard.ui.components.EmptyState
import dev.yikz.clipboard.ui.components.SectionLabel
import dev.yikz.clipboard.ui.theme.StatusColors
import dev.yikz.clipboard.util.Format
import kotlinx.coroutines.async
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DevicesScreen() {
    val g = LocalContext.current.graph
    val version by g.sync.devicesVersion.collectAsStateWithLifecycle()
    val connection by g.sync.connection.collectAsStateWithLifecycle()
    val settings by g.sync.settings.collectAsStateWithLifecycle()
    var devices by remember { mutableStateOf<List<Device>?>(null) }
    var storage by remember { mutableStateOf<StorageInfo?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var refreshing by remember { mutableStateOf(false) }
    var reload by remember { mutableIntStateOf(0) }
    var renaming by remember { mutableStateOf<Device?>(null) }
    var revoking by remember { mutableStateOf<Device?>(null) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(version, reload) {
        try {
            val d = async { g.sync.devices() }
            val s = async { runCatching { g.sync.storage() }.getOrNull() }
            devices = d.await()
            storage = s.await() ?: storage
            error = null
        } catch (e: Exception) {
            error = SyncController.errorMessage(e)
        } finally {
            refreshing = false
        }
    }
    val liveOnline = (connection as? ConnectionState.Connected)?.onlineDevices?.map { it.deviceId }?.toSet()

    PullToRefreshBox(
        isRefreshing = refreshing,
        onRefresh = {
            refreshing = true
            reload++
        },
        modifier = Modifier.fillMaxSize(),
    ) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
            item {
                Text(
                    "Devices",
                    style = MaterialTheme.typography.headlineLarge,
                    modifier = Modifier.statusBarsPadding().padding(start = 20.dp, end = 20.dp, top = 20.dp, bottom = 8.dp),
                )
            }
            item { StorageCard(storage) }
            item { SectionLabel("Signed in devices") }
            val list = devices
            when {
                list == null && error != null -> item {
                    EmptyState(Icons.Outlined.CloudOff, "Cannot load devices", error.orEmpty(), action = {
                        TextButton(onClick = { reload++ }) { Text("Retry") }
                    })
                }
                list == null -> item {
                    Box(Modifier.fillMaxWidth().padding(32.dp), contentAlignment = Alignment.Center) {
                        androidx.compose.material3.CircularProgressIndicator()
                    }
                }
                else -> items(list, key = { it.id }) { device ->
                    DeviceCard(
                        device = device,
                        online = if (liveOnline != null) device.id in liveOnline else device.online,
                        isSelf = device.id == settings.deviceId,
                        onRename = { renaming = device },
                        onRevoke = { revoking = device },
                        modifier = Modifier.animateItem(),
                    )
                }
            }
        }
    }

    renaming?.let { device ->
        var name by remember(device.id) { mutableStateOf(device.name) }
        AlertDialog(
            onDismissRequest = { renaming = null },
            title = { Text("Rename device") },
            text = {
                OutlinedTextField(value = name, onValueChange = { name = it }, singleLine = true, label = { Text("Name") }, modifier = Modifier.fillMaxWidth())
            },
            confirmButton = {
                TextButton(
                    enabled = name.isNotBlank() && name.trim().length <= 64,
                    onClick = {
                        renaming = null
                        scope.launch {
                            try {
                                g.sync.renameDevice(device.id, name)
                                reload++
                            } catch (e: Exception) {
                                error = SyncController.errorMessage(e)
                            }
                        }
                    },
                ) { Text("Save") }
            },
            dismissButton = { TextButton(onClick = { renaming = null }) { Text("Cancel") } },
        )
    }

    revoking?.let { device ->
        val self = device.id == settings.deviceId
        AlertDialog(
            onDismissRequest = { revoking = null },
            title = { Text(if (device.revoked) "Remove ${device.name}?" else "Revoke ${device.name}?") },
            text = {
                Text(
                    when {
                        self -> "This device will be signed out and its local history cleared."
                        device.revoked -> "The device record is removed. Items it created stay in history."
                        else -> "The device is signed out immediately and must sign in again to sync."
                    },
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    revoking = null
                    scope.launch {
                        try {
                            if (self) {
                                g.sync.signOut(remote = true)
                            } else {
                                g.sync.revokeDevice(device.id)
                                reload++
                            }
                        } catch (e: Exception) {
                            error = SyncController.errorMessage(e)
                        }
                    }
                }) { Text(if (device.revoked) "Remove" else "Revoke", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { revoking = null }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun StorageCard(storage: StorageInfo?) {
    Surface(
        color = MaterialTheme.colorScheme.primaryContainer,
        shape = MaterialTheme.shapes.large,
        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
    ) {
        Column(Modifier.padding(20.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Outlined.Storage, contentDescription = null, tint = MaterialTheme.colorScheme.onPrimaryContainer)
                Spacer(Modifier.width(10.dp))
                Text("Storage", style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.onPrimaryContainer, modifier = Modifier.weight(1f))
                if (storage != null) {
                    Text(
                        "${storage.itemCount} items",
                        style = MaterialTheme.typography.labelLarge,
                        color = MaterialTheme.colorScheme.onPrimaryContainer.copy(alpha = 0.8f),
                    )
                }
            }
            Spacer(Modifier.height(16.dp))
            if (storage == null) {
                LinearProgressIndicator(Modifier.fillMaxWidth())
            } else {
                Text(
                    "${Format.bytes(storage.usedBytes)} of ${Format.bytes(storage.limitBytes)}",
                    style = MaterialTheme.typography.headlineSmall,
                    color = MaterialTheme.colorScheme.onPrimaryContainer,
                )
                Spacer(Modifier.height(10.dp))
                LinearProgressIndicator(
                    progress = { if (storage.limitBytes > 0) (storage.usedBytes.toFloat() / storage.limitBytes).coerceIn(0f, 1f) else 0f },
                    modifier = Modifier.fillMaxWidth().height(8.dp).clip(CircleShape),
                    color = MaterialTheme.colorScheme.primary,
                    trackColor = MaterialTheme.colorScheme.onPrimaryContainer.copy(alpha = 0.15f),
                    drawStopIndicator = {},
                )
                Spacer(Modifier.height(12.dp))
                Text(
                    "Pinned ${Format.bytes(storage.pinnedBytes)} of ${Format.bytes(storage.pinnedLimitBytes)}  ·  Kept ${storage.retentionDays} days",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onPrimaryContainer.copy(alpha = 0.8f),
                )
                if (storage.diskLow) {
                    Spacer(Modifier.height(8.dp))
                    Text(
                        "Server disk is low (${Format.bytes(storage.freeDiskBytes)} free). Uploads over 1 MB are rejected.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }
        }
    }
}

private fun platformIcon(platform: String): ImageVector = when (platform) {
    "android" -> Icons.Outlined.PhoneAndroid
    "macos" -> Icons.Outlined.Computer
    "windows" -> Icons.Outlined.DesktopWindows
    else -> Icons.Outlined.Language
}

private fun platformLabel(platform: String): String = when (platform) {
    "android" -> "Android"
    "macos" -> "macOS"
    "windows" -> "Windows"
    "web" -> "Web"
    else -> platform
}

@Composable
private fun DeviceCard(device: Device, online: Boolean, isSelf: Boolean, onRename: () -> Unit, onRevoke: () -> Unit, modifier: Modifier = Modifier) {
    var menu by remember { mutableStateOf(false) }
    Surface(
        color = MaterialTheme.colorScheme.surfaceContainerLow,
        shape = MaterialTheme.shapes.large,
        modifier = modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
    ) {
        Row(Modifier.padding(start = 16.dp, top = 14.dp, bottom = 14.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Box {
                Box(
                    Modifier.size(44.dp).clip(RoundedCornerShape(14.dp)).background(MaterialTheme.colorScheme.secondaryContainer),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(platformIcon(device.platform), contentDescription = null, tint = MaterialTheme.colorScheme.onSecondaryContainer)
                }
                if (!device.revoked) {
                    Box(
                        Modifier
                            .align(Alignment.BottomEnd)
                            .size(14.dp)
                            .clip(CircleShape)
                            .background(MaterialTheme.colorScheme.surfaceContainerLow)
                            .padding(2.dp)
                            .clip(CircleShape)
                            .background(if (online) StatusColors.online else StatusColors.offline),
                    )
                }
            }
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(device.name, style = MaterialTheme.typography.titleMedium, maxLines = 1, modifier = Modifier.weight(1f, fill = false))
                    if (isSelf) {
                        Spacer(Modifier.width(8.dp))
                        Surface(color = MaterialTheme.colorScheme.primary, shape = CircleShape) {
                            Text(
                                "This device",
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onPrimary,
                                modifier = Modifier.padding(horizontal = 8.dp, vertical = 2.dp),
                            )
                        }
                    }
                }
                val status = when {
                    device.revoked -> "Revoked"
                    online -> "Online"
                    else -> Timestamps.parseOrNull(device.lastSeenAt)?.let { "Last seen ${Format.ago(it)}" } ?: "Offline"
                }
                Text(
                    "${platformLabel(device.platform)}  ·  $status",
                    style = MaterialTheme.typography.bodyMedium,
                    color = if (device.revoked) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Box {
                IconButton(onClick = { menu = true }) { Icon(Icons.Outlined.MoreVert, contentDescription = "More") }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    if (!device.revoked) {
                        DropdownMenuItem(text = { Text("Rename") }, onClick = {
                            menu = false
                            onRename()
                        })
                    }
                    DropdownMenuItem(
                        text = { Text(if (device.revoked) "Remove" else if (isSelf) "Sign out" else "Revoke", color = MaterialTheme.colorScheme.error) },
                        onClick = {
                            menu = false
                            onRevoke()
                        },
                    )
                }
            }
        }
    }
}
