package dev.yikz.clipboard.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.automirrored.outlined.ArrowForward
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.NewReleases
import androidx.compose.material.icons.outlined.Schedule
import androidx.compose.material.icons.outlined.SystemUpdate
import androidx.compose.material.icons.outlined.Update
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withLink
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.yikz.clipboard.core.MdBlock
import dev.yikz.clipboard.core.MdSpan
import dev.yikz.clipboard.core.NotesMarkdown
import dev.yikz.clipboard.core.SemVer
import dev.yikz.clipboard.core.Timestamps
import dev.yikz.clipboard.graph
import dev.yikz.clipboard.ui.components.SectionLabel
import dev.yikz.clipboard.ui.components.SettingsCard
import dev.yikz.clipboard.ui.components.SettingsRow
import dev.yikz.clipboard.update.UpdateStatus
import dev.yikz.clipboard.update.UpdateTrigger
import dev.yikz.clipboard.util.Format
import kotlinx.coroutines.launch

fun UpdateStatus.label(): String = when (this) {
    UpdateStatus.Idle -> "Not checked yet"
    UpdateStatus.Checking -> "Checking"
    UpdateStatus.UpToDate -> "Up to date"
    is UpdateStatus.Available -> "Version $version available"
    is UpdateStatus.Downloading -> "Downloading $percent%"
    is UpdateStatus.Verifying -> "Verifying $version"
    is UpdateStatus.ReadyToInstall -> "Ready to install $version"
    is UpdateStatus.NeedsPermission -> "Permission needed to install $version"
    is UpdateStatus.Installing -> "Installing $version"
    is UpdateStatus.Error -> "Error: $reason"
}

private fun UpdateStatus.busy(): Boolean =
    this is UpdateStatus.Checking || this is UpdateStatus.Downloading || this is UpdateStatus.Verifying || this is UpdateStatus.Installing

@Composable
fun UpdatesSection(onOpenWhatsNew: () -> Unit) {
    val context = LocalContext.current
    val g = context.graph
    val settings by g.sync.settings.collectAsStateWithLifecycle()
    val status by g.updates.status.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()
    val tick = rememberResumeTick()
    LaunchedEffect(tick) { g.updates.onPermissionMaybeChanged() }

    SectionLabel("Updates")
    SettingsCard {
        SettingsRow("Current version", g.updates.currentVersion, Icons.Outlined.Info)
        UpdatesDivider()
        SettingsRow(
            "Automatically install updates",
            if (settings.autoInstallUpdates) "Download, verify and install new versions" else "Only notify when a new version is out",
            Icons.Outlined.Update,
            onClick = { scope.launch { g.settings.setAutoInstallUpdates(!settings.autoInstallUpdates) } },
        ) {
            Switch(checked = settings.autoInstallUpdates, onCheckedChange = { v -> scope.launch { g.settings.setAutoInstallUpdates(v) } })
        }
        UpdatesDivider()
        Column {
            SettingsRow("Check for updates", status.label(), Icons.Outlined.SystemUpdate) {
                val current = status
                when {
                    current.busy() -> CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.5.dp)
                    current is UpdateStatus.Available || current is UpdateStatus.ReadyToInstall ->
                        FilledTonalButton(onClick = { g.updates.installNow() }) { Text("Install") }
                    else -> FilledTonalButton(onClick = { g.updates.checkAsync(UpdateTrigger.MANUAL) }) { Text("Check") }
                }
            }
            val downloading = status as? UpdateStatus.Downloading
            if (downloading != null) {
                LinearProgressIndicator(
                    progress = { downloading.percent / 100f },
                    modifier = Modifier.fillMaxWidth().padding(start = 54.dp, end = 16.dp, bottom = 12.dp),
                )
            }
        }
        UpdatesDivider()
        SettingsRow(
            "Last checked",
            settings.lastUpdateCheckMs.takeIf { it > 0 }?.let { Format.dateTime(it) } ?: "Never",
            Icons.Outlined.Schedule,
        )
        UpdatesDivider()
        SettingsRow("What's new", "Release notes of the latest version", Icons.Outlined.NewReleases, onClick = onOpenWhatsNew) {
            Icon(Icons.AutoMirrored.Outlined.ArrowForward, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
    val needsPermission = status as? UpdateStatus.NeedsPermission
    if (needsPermission != null) {
        Spacer(Modifier.height(12.dp))
        Surface(
            color = MaterialTheme.colorScheme.secondaryContainer,
            shape = MaterialTheme.shapes.large,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
        ) {
            Column(Modifier.padding(16.dp)) {
                Text("Allow installing updates", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.onSecondaryContainer)
                Spacer(Modifier.height(6.dp))
                Text(
                    "Version ${needsPermission.version} is downloaded and verified. Android only lets Yikz Clipboard install it after you turn on " +
                        "\"Allow from this source\" for this app. You only need to do this once. Come back here afterwards and the update installs.",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSecondaryContainer,
                )
                Spacer(Modifier.height(8.dp))
                TextButton(onClick = {
                    try {
                        context.startActivity(g.updates.unknownSourcesIntent())
                    } catch (e: Exception) {
                        g.log.e("update", "cannot open install permission settings", e)
                    }
                }) { Text("Open settings") }
            }
        }
    }
}

@Composable
private fun UpdatesDivider() {
    HorizontalDivider(Modifier.padding(start = 54.dp), color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun WhatsNewScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    val g = context.graph
    val latest by g.updates.latest.collectAsStateWithLifecycle()
    val settings by g.sync.settings.collectAsStateWithLifecycle()
    var loading by remember { mutableStateOf(latest == null) }
    var failed by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        if (latest == null) {
            failed = !g.updates.refreshNotes()
            loading = false
        }
    }
    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("What's new") },
            navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = "Back") } },
        )
        val release = latest
        when {
            release != null -> {
                val blocks = remember(release.notesMd) { NotesMarkdown.parse(release.notesMd) }
                val linkColor = MaterialTheme.colorScheme.primary
                val codeBackground = MaterialTheme.colorScheme.surfaceContainerHighest
                Column(
                    Modifier
                        .fillMaxSize()
                        .verticalScroll(rememberScrollState())
                        .navigationBarsPadding()
                        .padding(horizontal = 20.dp, vertical = 8.dp),
                    verticalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    Text("Version ${release.version}", style = MaterialTheme.typography.headlineSmall)
                    val published = Timestamps.parseOrNull(release.publishedAt)
                    val newer = SemVer.isNewer(release.version, g.updates.currentVersion)
                    Text(
                        listOfNotNull(
                            published?.let { "Released ${Format.dateTime(it)}" },
                            if (newer) "You have ${g.updates.currentVersion}" else "Installed",
                        ).joinToString("  ·  "),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Spacer(Modifier.height(8.dp))
                    if (blocks.isEmpty()) {
                        Text("No release notes.", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    blocks.forEach { block ->
                        when (block) {
                            is MdBlock.Heading -> Text(
                                render(block.spans, linkColor, codeBackground, settings.serverUrl),
                                style = if (block.level <= 2) MaterialTheme.typography.titleLarge else MaterialTheme.typography.titleMedium,
                                modifier = Modifier.padding(top = 10.dp),
                            )
                            is MdBlock.Bullet -> Row {
                                Text("•", style = MaterialTheme.typography.bodyLarge, modifier = Modifier.width(18.dp))
                                Text(render(block.spans, linkColor, codeBackground, settings.serverUrl), style = MaterialTheme.typography.bodyLarge)
                            }
                            is MdBlock.Paragraph -> Text(
                                render(block.spans, linkColor, codeBackground, settings.serverUrl),
                                style = MaterialTheme.typography.bodyLarge,
                            )
                        }
                    }
                    Spacer(Modifier.height(24.dp))
                }
            }
            loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            else -> Box(Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) {
                Text(
                    if (failed) "Release notes could not be loaded." else "No release has been published yet.",
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

private fun linkTarget(url: String, serverUrl: String): String? = when {
    url.startsWith("https://") || url.startsWith("http://") -> url
    url.startsWith("/") && !url.startsWith("//") -> serverUrl.trimEnd('/') + url
    else -> null
}

private fun render(spans: List<MdSpan>, linkColor: Color, codeBackground: Color, serverUrl: String): AnnotatedString = buildAnnotatedString {
    for (span in spans) {
        val base = SpanStyle(
            fontWeight = if (span.bold) FontWeight.SemiBold else null,
            fontFamily = if (span.code) FontFamily.Monospace else null,
            background = if (span.code) codeBackground else Color.Unspecified,
        )
        val target = span.url?.let { linkTarget(it, serverUrl) }
        if (target != null) {
            withLink(LinkAnnotation.Url(target, TextLinkStyles(SpanStyle(color = linkColor, textDecoration = TextDecoration.Underline)))) {
                withStyle(base) { append(span.text) }
            }
        } else {
            withStyle(base) { append(span.text) }
        }
    }
}
