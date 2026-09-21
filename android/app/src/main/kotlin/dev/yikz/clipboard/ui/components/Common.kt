package dev.yikz.clipboard.ui.components

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Description
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Image
import androidx.compose.material.icons.outlined.Link
import androidx.compose.material.icons.automirrored.outlined.Notes
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import dev.yikz.clipboard.core.BlockReason
import dev.yikz.clipboard.core.CachedItem
import dev.yikz.clipboard.core.ConnectionState
import dev.yikz.clipboard.core.Kind
import dev.yikz.clipboard.ui.theme.StatusColors
import dev.yikz.clipboard.util.Format
import kotlinx.coroutines.delay
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue

enum class ItemType(val label: String) { TEXT("Text"), LINK("Links"), IMAGE("Images"), FILES("Files") }

fun CachedItem.type(): ItemType = when (kind) {
    Kind.IMAGE -> ItemType.IMAGE
    Kind.FILES -> ItemType.FILES
    else -> if (Format.isLink(preview)) ItemType.LINK else ItemType.TEXT
}

fun ItemType.icon(): ImageVector = when (this) {
    ItemType.TEXT -> Icons.AutoMirrored.Outlined.Notes
    ItemType.LINK -> Icons.Outlined.Link
    ItemType.IMAGE -> Icons.Outlined.Image
    ItemType.FILES -> Icons.Outlined.Folder
}

@Composable
fun ItemType.containerColor(): Color = when (this) {
    ItemType.TEXT -> MaterialTheme.colorScheme.primaryContainer
    ItemType.LINK -> MaterialTheme.colorScheme.tertiaryContainer
    ItemType.IMAGE -> MaterialTheme.colorScheme.secondaryContainer
    ItemType.FILES -> MaterialTheme.colorScheme.surfaceContainerHighest
}

@Composable
fun ItemType.contentColor(): Color = when (this) {
    ItemType.TEXT -> MaterialTheme.colorScheme.onPrimaryContainer
    ItemType.LINK -> MaterialTheme.colorScheme.onTertiaryContainer
    ItemType.IMAGE -> MaterialTheme.colorScheme.onSecondaryContainer
    ItemType.FILES -> MaterialTheme.colorScheme.onSurfaceVariant
}

fun CachedItem.title(): String {
    val meta = meta ?: return "Unreadable item"
    return when (kind) {
        Kind.IMAGE -> meta.image?.let { "Image ${it.width} × ${it.height}" } ?: "Image"
        Kind.FILES -> meta.files?.let { if (it.size == 1) it[0].name else "${it.size} files" } ?: "Files"
        else -> meta.preview.trim().lineSequence().firstOrNull { it.isNotBlank() }?.trim() ?: "Empty text"
    }
}

fun CachedItem.fileIcon(): ImageVector = if (kind == Kind.FILES) Icons.Outlined.Folder else Icons.Outlined.Description

@Composable
fun KindBadge(type: ItemType, modifier: Modifier = Modifier, size: Dp = 44.dp) {
    Box(
        modifier = modifier
            .size(size)
            .clip(RoundedCornerShape(size * 0.3f))
            .background(type.containerColor()),
        contentAlignment = Alignment.Center,
    ) {
        Icon(type.icon(), contentDescription = null, tint = type.contentColor(), modifier = Modifier.size(size * 0.5f))
    }
}

data class StatusInfo(val label: String, val color: Color, val pulsing: Boolean)

@Composable
fun statusInfo(state: ConnectionState, paused: Boolean, ownDeviceId: String, syncing: Boolean): StatusInfo {
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(state) {
        while (state is ConnectionState.Waiting) {
            now = System.currentTimeMillis()
            delay(1000)
        }
    }
    return when {
        paused || state == ConnectionState.Paused -> StatusInfo("Paused", StatusColors.offline, false)
        state is ConnectionState.Connected -> {
            val others = state.onlineDevices.count { it.deviceId != ownDeviceId }
            val label = when {
                syncing -> "Syncing"
                others == 0 -> "Connected"
                others == 1 -> "1 device online"
                else -> "$others devices online"
            }
            StatusInfo(label, StatusColors.online, syncing)
        }
        state is ConnectionState.Connecting -> StatusInfo("Connecting", StatusColors.warning, true)
        state is ConnectionState.Waiting -> {
            val seconds = ((state.retryAtMs - now + 999) / 1000).coerceAtLeast(0)
            StatusInfo(if (seconds > 0) "Reconnecting in ${seconds}s" else "Reconnecting", StatusColors.warning, true)
        }
        state is ConnectionState.Blocked -> StatusInfo(
            when (state.reason) {
                BlockReason.UNAUTHORIZED -> "Signed out"
                BlockReason.UPDATE_REQUIRED -> "Update required"
                BlockReason.TOO_MANY_CONNECTIONS -> "Too many connections"
            },
            MaterialTheme.colorScheme.error,
            false,
        )
        else -> StatusInfo("Offline", StatusColors.offline, false)
    }
}

@Composable
fun StatusPill(info: StatusInfo, modifier: Modifier = Modifier, onClick: (() -> Unit)? = null) {
    val color by animateColorAsState(info.color, label = "status")
    val transition = rememberInfiniteTransition(label = "pulse")
    val pulse by transition.animateFloat(
        initialValue = 1f,
        targetValue = 0.35f,
        animationSpec = infiniteRepeatable(tween(900), RepeatMode.Reverse),
        label = "pulse",
    )
    Surface(
        modifier = modifier,
        shape = CircleShape,
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
        onClick = onClick ?: {},
        enabled = onClick != null,
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(
                modifier = Modifier
                    .size(8.dp)
                    .alpha(if (info.pulsing) pulse else 1f)
                    .clip(CircleShape)
                    .background(color),
            )
            Spacer(Modifier.width(8.dp))
            Text(info.label, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurface)
        }
    }
}

@Composable
fun EmptyState(icon: ImageVector, title: String, message: String, modifier: Modifier = Modifier, action: (@Composable () -> Unit)? = null) {
    Column(
        modifier = modifier.fillMaxWidth().padding(horizontal = 32.dp, vertical = 48.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Box(
            modifier = Modifier
                .size(88.dp)
                .clip(CircleShape)
                .background(MaterialTheme.colorScheme.primaryContainer.copy(alpha = 0.6f)),
            contentAlignment = Alignment.Center,
        ) {
            Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onPrimaryContainer, modifier = Modifier.size(40.dp))
        }
        Spacer(Modifier.height(20.dp))
        Text(title, style = MaterialTheme.typography.titleLarge, textAlign = TextAlign.Center)
        Spacer(Modifier.height(8.dp))
        Text(
            message,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
        if (action != null) {
            Spacer(Modifier.height(20.dp))
            action()
        }
    }
}

@Composable
fun SectionLabel(text: String, modifier: Modifier = Modifier) {
    Text(
        text,
        modifier = modifier.padding(start = 20.dp, end = 20.dp, top = 20.dp, bottom = 8.dp),
        style = MaterialTheme.typography.labelLarge,
        color = MaterialTheme.colorScheme.primary,
    )
}

@Composable
fun SettingsCard(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    Surface(
        modifier = modifier.fillMaxWidth().padding(horizontal = 16.dp),
        shape = MaterialTheme.shapes.large,
        color = MaterialTheme.colorScheme.surfaceContainerLow,
    ) {
        Column { content() }
    }
}

@Composable
fun SettingsRow(
    title: String,
    subtitle: String? = null,
    icon: ImageVector? = null,
    onClick: (() -> Unit)? = null,
    trailing: (@Composable () -> Unit)? = null,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier)
            .padding(horizontal = 16.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (icon != null) {
            Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(22.dp))
            Spacer(Modifier.width(16.dp))
        }
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge)
            if (subtitle != null) {
                Spacer(Modifier.height(2.dp))
                Text(subtitle, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        if (trailing != null) {
            Spacer(Modifier.width(12.dp))
            trailing()
        }
    }
}

@Composable
fun ShimmerRow() {
    val transition = rememberInfiniteTransition(label = "shimmer")
    val alpha by transition.animateFloat(0.35f, 0.8f, infiniteRepeatable(tween(800), RepeatMode.Reverse), label = "shimmer")
    val color = MaterialTheme.colorScheme.surfaceContainerHighest.copy(alpha = alpha)
    Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(44.dp).clip(RoundedCornerShape(13.dp)).background(color))
        Spacer(Modifier.width(16.dp))
        Column(Modifier.weight(1f)) {
            Box(Modifier.fillMaxWidth(0.7f).height(14.dp).clip(RoundedCornerShape(7.dp)).background(color))
            Spacer(Modifier.height(8.dp))
            Box(Modifier.fillMaxWidth(0.4f).height(10.dp).clip(RoundedCornerShape(5.dp)).background(color))
        }
    }
}
