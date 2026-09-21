package dev.yikz.clipboard.ui

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ContentPaste
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.outlined.ContentPaste
import androidx.compose.material.icons.outlined.Devices
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.yikz.clipboard.graph
import dev.yikz.clipboard.service.SyncService
import dev.yikz.clipboard.sync.AuthState
import dev.yikz.clipboard.ui.screens.ActivityLogScreen
import dev.yikz.clipboard.ui.screens.DevicesScreen
import dev.yikz.clipboard.ui.screens.HistoryScreen
import dev.yikz.clipboard.ui.screens.OnboardingFlow
import dev.yikz.clipboard.ui.screens.SettingsScreen
import dev.yikz.clipboard.ui.theme.YikzTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        setContent {
            YikzTheme {
                Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.surface) {
                    Root()
                }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        graph.sync.refreshAuth()
        if (graph.sync.auth.value == AuthState.READY) SyncService.start(this)
    }
}

@Composable
private fun Root() {
    val g = LocalContext.current.graph
    val auth by g.sync.auth.collectAsStateWithLifecycle()
    AnimatedContent(
        targetState = auth == AuthState.READY,
        transitionSpec = { fadeIn() togetherWith fadeOut() },
        label = "root",
    ) { ready ->
        if (ready) MainScaffold() else OnboardingFlow(auth)
    }
}

private data class Tab(val label: String, val selected: ImageVector, val unselected: ImageVector)

private val tabs = listOf(
    Tab("Clipboard", Icons.Filled.ContentPaste, Icons.Outlined.ContentPaste),
    Tab("Devices", Icons.Filled.Devices, Icons.Outlined.Devices),
    Tab("Settings", Icons.Filled.Settings, Icons.Outlined.Settings),
)

@Composable
private fun MainScaffold() {
    var tab by rememberSaveable { mutableIntStateOf(0) }
    var showLog by rememberSaveable { mutableStateOf(false) }
    AnimatedContent(
        targetState = showLog,
        transitionSpec = {
            if (targetState) {
                (slideInHorizontally { it / 3 } + fadeIn()) togetherWith fadeOut()
            } else {
                fadeIn() togetherWith (slideOutHorizontally { it / 3 } + fadeOut())
            }
        },
        label = "log",
    ) { logVisible ->
        if (logVisible) {
            BackHandler { showLog = false }
            ActivityLogScreen(onBack = { showLog = false })
        } else {
            BackHandler(enabled = tab != 0) { tab = 0 }
            Scaffold(
                contentWindowInsets = WindowInsets(0),
                bottomBar = {
                    NavigationBar {
                        tabs.forEachIndexed { index, t ->
                            NavigationBarItem(
                                selected = tab == index,
                                onClick = { tab = index },
                                icon = { Icon(if (tab == index) t.selected else t.unselected, contentDescription = null) },
                                label = { Text(t.label) },
                            )
                        }
                    }
                },
            ) { padding ->
                AnimatedContent(
                    targetState = tab,
                    transitionSpec = { fadeIn() togetherWith fadeOut() },
                    modifier = Modifier.padding(bottom = padding.calculateBottomPadding()),
                    label = "tab",
                ) { current ->
                    Box(Modifier.fillMaxSize()) {
                        when (current) {
                            0 -> HistoryScreen()
                            1 -> DevicesScreen()
                            else -> SettingsScreen(onOpenLog = { showLog = true })
                        }
                    }
                }
            }
        }
    }
}
