plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

val productVersion: String = rootProject.layout.projectDirectory.file("../VERSION").asFile.readText().trim()
val productVersionCode: Int = run {
    val parts = Regex("^(\\d+)\\.(\\d+)\\.(\\d+)$").matchEntire(productVersion)?.groupValues
        ?: throw GradleException("VERSION must be MAJOR.MINOR.PATCH, got '$productVersion'")
    val (major, minor, patch) = parts.drop(1).map { it.toInt() }
    if (minor > 99 || patch > 99) throw GradleException("VERSION minor and patch must be below 100 for versionCode")
    major * 10000 + minor * 100 + patch
}

fun env(name: String): String? = providers.environmentVariable(name).orNull?.takeIf { it.isNotBlank() }

val releaseKeystorePath: String? = env("YIKZ_KEYSTORE_FILE")
val localDebugKeystore = File(System.getProperty("user.home"), ".android/debug.keystore")

android {
    namespace = "dev.yikz.clipboard"
    compileSdk {
        version = release(36) {
            minorApiLevel = 1
        }
    }

    defaultConfig {
        applicationId = "dev.yikz.clipboard"
        minSdk = 29
        targetSdk = 36
        versionCode = productVersionCode
        versionName = productVersion
        buildConfigField("String", "DEFAULT_SERVER_URL", "\"https://clip.yikz.dev\"")
    }

    signingConfigs {
        create("release") {
            if (releaseKeystorePath != null) {
                val keystore = file(releaseKeystorePath)
                if (!keystore.isFile) throw GradleException("YIKZ_KEYSTORE_FILE points to a missing file: $releaseKeystorePath")
                storeFile = keystore
                storePassword = env("YIKZ_KEYSTORE_PASSWORD") ?: throw GradleException("YIKZ_KEYSTORE_PASSWORD is not set")
                keyAlias = env("YIKZ_KEY_ALIAS") ?: throw GradleException("YIKZ_KEY_ALIAS is not set")
                keyPassword = env("YIKZ_KEY_PASSWORD") ?: throw GradleException("YIKZ_KEY_PASSWORD is not set")
            } else {
                storeFile = localDebugKeystore
                storePassword = "android"
                keyAlias = "androiddebugkey"
                keyPassword = "android"
            }
        }
    }

    buildTypes {
        debug {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro", "proguard-debug.pro")
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = if (releaseKeystorePath != null || localDebugKeystore.isFile) {
                signingConfigs.getByName("release")
            } else {
                signingConfigs.getByName("debug")
            }
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        resources {
            excludes += setOf("/META-INF/{AL2.0,LGPL2.1}", "/META-INF/versions/9/previous-compilation-data.bin")
        }
    }

    lint {
        abortOnError = true
        checkReleaseBuilds = false
        disable += setOf("ProtectedPermissions", "GradleDependency", "NewerVersionAvailable", "AndroidGradlePluginVersion")
    }
}

dependencies {
    implementation(project(":core"))
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.okhttp)
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.process)
    implementation(libs.androidx.datastore.preferences)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.graphics)
    implementation(libs.compose.foundation)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons.extended)
    implementation(libs.compose.ui.tooling.preview)
    debugImplementation(libs.compose.ui.tooling)
    testImplementation(libs.junit)
}
