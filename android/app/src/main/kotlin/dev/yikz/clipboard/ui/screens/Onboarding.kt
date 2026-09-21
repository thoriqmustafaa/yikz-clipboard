package dev.yikz.clipboard.ui.screens

import android.Manifest
import android.annotation.SuppressLint
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.outlined.BatteryChargingFull
import androidx.compose.material.icons.outlined.ContentPaste
import androidx.compose.material.icons.outlined.Key
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Notifications
import androidx.compose.material.icons.outlined.Visibility
import androidx.compose.material.icons.outlined.VisibilityOff
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.yikz.clipboard.graph
import dev.yikz.clipboard.sync.AuthState
import dev.yikz.clipboard.sync.SyncController
import dev.yikz.clipboard.sync.UnlockResult
import kotlinx.coroutines.launch

private enum class Step { WELCOME, ACCOUNT, KEY, PERMISSIONS }

@Composable
fun OnboardingFlow(auth: AuthState) {
    var showAccount by rememberSaveable { mutableStateOf(false) }
    val step = when (auth) {
        AuthState.SIGNED_OUT -> if (showAccount) Step.ACCOUNT else Step.WELCOME
        AuthState.NEEDS_KEY -> Step.KEY
        else -> Step.PERMISSIONS
    }
    AnimatedContent(
        targetState = step,
        transitionSpec = {
            if (targetState.ordinal > initialState.ordinal) {
                (slideInHorizontally { it / 4 } + fadeIn()) togetherWith (slideOutHorizontally { -it / 4 } + fadeOut())
            } else {
                (slideInHorizontally { -it / 4 } + fadeIn()) togetherWith (slideOutHorizontally { it / 4 } + fadeOut())
            }
        },
        label = "onboarding",
    ) { current ->
        Box(
            Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .navigationBarsPadding()
                .imePadding(),
        ) {
            when (current) {
                Step.WELCOME -> WelcomeStep(onContinue = { showAccount = true })
                Step.ACCOUNT -> AccountStep(onBack = { showAccount = false })
                Step.KEY -> KeyStep()
                Step.PERMISSIONS -> PermissionsStep()
            }
        }
    }
}

@Composable
private fun StepScaffold(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    title: String,
    subtitle: String,
    bottom: @Composable () -> Unit,
    content: @Composable () -> Unit,
) {
    Column(Modifier.fillMaxSize()) {
        Column(
            Modifier
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 24.dp),
        ) {
            Spacer(Modifier.height(40.dp))
            Box(
                Modifier
                    .size(56.dp)
                    .clip(RoundedCornerShape(18.dp))
                    .background(MaterialTheme.colorScheme.primaryContainer),
                contentAlignment = Alignment.Center,
            ) {
                Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onPrimaryContainer, modifier = Modifier.size(28.dp))
            }
            Spacer(Modifier.height(24.dp))
            Text(title, style = MaterialTheme.typography.headlineMedium)
            Spacer(Modifier.height(8.dp))
            Text(subtitle, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.height(28.dp))
            content()
            Spacer(Modifier.height(24.dp))
        }
        Column(Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 16.dp)) { bottom() }
    }
}

@Composable
private fun WelcomeStep(onContinue: () -> Unit) {
    Column(
        Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(Modifier.weight(1f))
        Box(
            Modifier
                .size(112.dp)
                .clip(RoundedCornerShape(34.dp))
                .background(MaterialTheme.colorScheme.primary),
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.Outlined.ContentPaste, contentDescription = null, tint = MaterialTheme.colorScheme.onPrimary, modifier = Modifier.size(56.dp))
        }
        Spacer(Modifier.height(36.dp))
        Text("Your clipboard,\neverywhere", style = MaterialTheme.typography.displaySmall, textAlign = TextAlign.Center)
        Spacer(Modifier.height(16.dp))
        Text(
            "Copy on one device, paste on the next. Every item is end-to-end encrypted and synced through your own server.",
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(horizontal = 8.dp),
        )
        Spacer(Modifier.weight(1.2f))
        Button(onClick = onContinue, modifier = Modifier.fillMaxWidth().height(56.dp)) {
            Text("Get started", style = MaterialTheme.typography.titleMedium)
        }
        Spacer(Modifier.height(8.dp))
    }
}

@Composable
private fun PasswordField(
    value: String,
    onChange: (String) -> Unit,
    label: String,
    imeAction: ImeAction,
    onDone: () -> Unit = {},
    isError: Boolean = false,
    supporting: String? = null,
) {
    var visible by rememberSaveable { mutableStateOf(false) }
    OutlinedTextField(
        value = value,
        onValueChange = onChange,
        label = { Text(label) },
        singleLine = true,
        isError = isError,
        supportingText = supporting?.let { { Text(it) } },
        visualTransformation = if (visible) VisualTransformation.None else PasswordVisualTransformation(),
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = imeAction),
        keyboardActions = KeyboardActions(onDone = { onDone() }, onGo = { onDone() }),
        trailingIcon = {
            IconButton(onClick = { visible = !visible }) {
                Icon(if (visible) Icons.Outlined.VisibilityOff else Icons.Outlined.Visibility, contentDescription = if (visible) "Hide" else "Show")
            }
        },
        modifier = Modifier.fillMaxWidth(),
    )
}

@Composable
private fun AccountStep(onBack: () -> Unit) {
    val g = LocalContext.current.graph
    val settings by g.sync.settings.collectAsStateWithLifecycle()
    var server by rememberSaveable { mutableStateOf(settings.serverUrl) }
    var username by rememberSaveable { mutableStateOf(settings.username) }
    var password by rememberSaveable { mutableStateOf("") }
    var deviceName by rememberSaveable { mutableStateOf(settings.deviceName.ifEmpty { Build.MODEL }) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    val canSubmit = server.isNotBlank() && username.isNotBlank() && password.isNotEmpty() && deviceName.isNotBlank() && deviceName.trim().length <= 64 && !busy
    val submit = {
        if (canSubmit) {
            busy = true
            error = null
            scope.launch {
                try {
                    g.sync.login(server, username, password, deviceName)
                } catch (e: Exception) {
                    error = SyncController.errorMessage(e)
                } finally {
                    busy = false
                }
            }
        }
    }
    StepScaffold(
        icon = Icons.Outlined.Lock,
        title = "Sign in",
        subtitle = "Connect this device to your yikz-clipboard server.",
        bottom = {
            Button(onClick = { submit() }, enabled = canSubmit, modifier = Modifier.fillMaxWidth().height(56.dp)) {
                if (busy) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.5.dp, color = MaterialTheme.colorScheme.onPrimary) else Text("Sign in", style = MaterialTheme.typography.titleMedium)
            }
            TextButton(onClick = onBack, modifier = Modifier.fillMaxWidth()) { Text("Back") }
        },
    ) {
        OutlinedTextField(
            value = server,
            onValueChange = { server = it },
            label = { Text("Server") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Next),
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(12.dp))
        OutlinedTextField(
            value = username,
            onValueChange = { username = it },
            label = { Text("Username") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Next),
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(12.dp))
        PasswordField(password, { password = it }, "Login password", ImeAction.Next)
        Spacer(Modifier.height(12.dp))
        OutlinedTextField(
            value = deviceName,
            onValueChange = { deviceName = it },
            label = { Text("Device name") },
            singleLine = true,
            supportingText = { Text("Shown on your other devices") },
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Go),
            keyboardActions = KeyboardActions(onGo = { submit() }),
            modifier = Modifier.fillMaxWidth(),
        )
        ErrorCard(error)
    }
}

@Composable
private fun ErrorCard(error: String?) {
    AnimatedVisibility(visible = error != null) {
        Surface(
            color = MaterialTheme.colorScheme.errorContainer,
            shape = MaterialTheme.shapes.medium,
            modifier = Modifier.fillMaxWidth().padding(top = 16.dp),
        ) {
            Text(
                error.orEmpty(),
                color = MaterialTheme.colorScheme.onErrorContainer,
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.padding(16.dp),
            )
        }
    }
}

@Composable
private fun KeyStep() {
    val g = LocalContext.current.graph
    var firstDevice by remember { mutableStateOf<Boolean?>(null) }
    var password by rememberSaveable { mutableStateOf("") }
    var confirm by rememberSaveable { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var wrong by remember { mutableStateOf(false) }
    var reload by remember { mutableIntStateOf(0) }
    val scope = rememberCoroutineScope()
    LaunchedEffect(reload) {
        try {
            firstDevice = !g.sync.serverHasKeyCheck()
            error = null
        } catch (e: Exception) {
            error = SyncController.errorMessage(e)
        }
    }
    val isFirst = firstDevice == true
    val mismatch = isFirst && confirm.isNotEmpty() && confirm != password
    val canSubmit = firstDevice != null && password.isNotEmpty() && (!isFirst || (confirm == password && password.length >= 8)) && !busy
    val submit = {
        if (canSubmit) {
            busy = true
            error = null
            wrong = false
            scope.launch {
                try {
                    when (g.sync.unlock(password)) {
                        UnlockResult.Ok -> Unit
                        UnlockResult.WrongPassword -> wrong = true
                    }
                } catch (e: Exception) {
                    error = SyncController.errorMessage(e)
                } finally {
                    busy = false
                }
            }
        }
    }
    StepScaffold(
        icon = Icons.Outlined.Key,
        title = if (isFirst) "Create encryption password" else "Encryption password",
        subtitle = if (isFirst) {
            "This is the first device. Choose a password that encrypts everything you sync. You will enter it once on every other device."
        } else {
            "Enter the encryption password you chose on your first device. It is different from your login password."
        },
        bottom = {
            Button(onClick = { submit() }, enabled = canSubmit, modifier = Modifier.fillMaxWidth().height(56.dp)) {
                if (busy) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.5.dp, color = MaterialTheme.colorScheme.onPrimary)
                        Spacer(Modifier.width(12.dp))
                        Text("Deriving key")
                    }
                } else {
                    Text(if (isFirst) "Create and continue" else "Unlock", style = MaterialTheme.typography.titleMedium)
                }
            }
            TextButton(onClick = { scope.launch { g.sync.signOut(remote = true) } }, modifier = Modifier.fillMaxWidth()) {
                Text("Use a different account")
            }
        },
    ) {
        if (firstDevice == null && error == null) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(12.dp))
                Text("Checking server", color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        } else {
            PasswordField(
                value = password,
                onChange = {
                    password = it
                    wrong = false
                },
                label = "Encryption password",
                imeAction = if (isFirst) ImeAction.Next else ImeAction.Go,
                onDone = { submit() },
                isError = wrong,
                supporting = when {
                    wrong -> "Wrong encryption password"
                    isFirst -> "At least 8 characters. It cannot be recovered."
                    else -> null
                },
            )
            if (isFirst) {
                Spacer(Modifier.height(12.dp))
                PasswordField(
                    value = confirm,
                    onChange = { confirm = it },
                    label = "Confirm password",
                    imeAction = ImeAction.Go,
                    onDone = { submit() },
                    isError = mismatch,
                    supporting = if (mismatch) "Passwords do not match" else null,
                )
            }
            Spacer(Modifier.height(20.dp))
            InfoCard("Your encryption password never leaves this device. The server only stores encrypted data it cannot read.")
        }
        ErrorCard(error)
        if (error != null && firstDevice == null) {
            TextButton(onClick = { reload++ }) { Text("Try again") }
        }
    }
}

@Composable
private fun InfoCard(text: String) {
    Surface(color = MaterialTheme.colorScheme.surfaceContainerHigh, shape = MaterialTheme.shapes.medium, modifier = Modifier.fillMaxWidth()) {
        Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(16.dp))
    }
}

@Composable
fun rememberResumeTick(): Int {
    var tick by remember { mutableIntStateOf(0) }
    val owner = LocalLifecycleOwner.current
    DisposableEffect(owner) {
        val observer = LifecycleEventObserver { _, event -> if (event == Lifecycle.Event.ON_RESUME) tick++ }
        owner.lifecycle.addObserver(observer)
        onDispose { owner.lifecycle.removeObserver(observer) }
    }
    return tick
}

@SuppressLint("BatteryLife")
fun batteryExemptionIntent(context: android.content.Context): Intent =
    Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:${context.packageName}"))

fun isIgnoringBattery(context: android.content.Context): Boolean =
    context.getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(context.packageName)

fun hasNotificationPermission(context: android.content.Context): Boolean =
    Build.VERSION.SDK_INT < 33 || context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

@Composable
private fun PermissionsStep() {
    val context = LocalContext.current
    val g = context.graph
    val tick = rememberResumeTick()
    var notificationsGranted by remember { mutableStateOf(hasNotificationPermission(context)) }
    var batteryExempt by remember { mutableStateOf(isIgnoringBattery(context)) }
    LaunchedEffect(tick) {
        notificationsGranted = hasNotificationPermission(context)
        batteryExempt = isIgnoringBattery(context)
    }
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { notificationsGranted = it }
    val scope = rememberCoroutineScope()
    StepScaffold(
        icon = Icons.Outlined.Notifications,
        title = "Stay connected",
        subtitle = "Two permissions keep sync reliable in the background.",
        bottom = {
            Button(onClick = { scope.launch { g.sync.completeOnboarding() } }, modifier = Modifier.fillMaxWidth().height(56.dp)) {
                Text(if (notificationsGranted && batteryExempt) "Finish" else "Continue anyway", style = MaterialTheme.typography.titleMedium)
            }
        },
    ) {
        PermissionCard(
            icon = Icons.Outlined.Notifications,
            title = "Notifications",
            text = "Shows the connection status with quick Send and Pause actions, and asks before downloading very large items.",
            granted = notificationsGranted,
            onAllow = {
                if (Build.VERSION.SDK_INT >= 33) launcher.launch(Manifest.permission.POST_NOTIFICATIONS)
            },
        )
        Spacer(Modifier.height(12.dp))
        PermissionCard(
            icon = Icons.Outlined.BatteryChargingFull,
            title = "Unrestricted battery",
            text = "Lets the sync connection survive Doze so items arrive instantly. The connection is idle most of the time and uses very little power.",
            granted = batteryExempt,
            onAllow = { context.startActivity(batteryExemptionIntent(context)) },
        )
    }
}

@Composable
fun PermissionCard(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    title: String,
    text: String,
    granted: Boolean,
    onAllow: () -> Unit,
) {
    Surface(color = MaterialTheme.colorScheme.surfaceContainerLow, shape = MaterialTheme.shapes.large, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(20.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier.size(40.dp).clip(CircleShape).background(MaterialTheme.colorScheme.secondaryContainer),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSecondaryContainer, modifier = Modifier.size(22.dp))
                }
                Spacer(Modifier.width(14.dp))
                Text(title, style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                if (granted) Icon(Icons.Filled.CheckCircle, contentDescription = "Granted", tint = MaterialTheme.colorScheme.primary)
            }
            Spacer(Modifier.height(10.dp))
            Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (!granted) {
                Spacer(Modifier.height(14.dp))
                FilledTonalButton(onClick = onAllow) { Text("Allow") }
            }
        }
    }
}

