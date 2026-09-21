package dev.yikz.clipboard.core

import kotlinx.serialization.Serializable
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.signers.Ed25519Signer
import java.io.File
import java.io.InputStream
import java.security.MessageDigest

object ReleaseKeys {
    const val PUBLIC_KEY_B64 = "2Ve8Uwt53+AwbuAiK08Xf2gB5EU7pguqXL7I9yeqGGk="
    val publicKey: ByteArray by lazy { StrictBase64.decode(PUBLIC_KEY_B64) }
}

data class SemVer(val major: Int, val minor: Int, val patch: Int) : Comparable<SemVer> {
    override fun compareTo(other: SemVer): Int =
        compareValuesBy(this, other, SemVer::major, SemVer::minor, SemVer::patch)

    val versionCode: Int get() = major * 10000 + minor * 100 + patch

    override fun toString(): String = "$major.$minor.$patch"

    companion object {
        private val pattern = Regex("^(0|[1-9][0-9]{0,6})\\.(0|[1-9][0-9]{0,6})\\.(0|[1-9][0-9]{0,6})$")

        fun parse(text: String): SemVer? {
            val m = pattern.matchEntire(text.trim()) ?: return null
            return SemVer(m.groupValues[1].toInt(), m.groupValues[2].toInt(), m.groupValues[3].toInt())
        }

        fun isNewer(candidate: String, current: String): Boolean {
            val c = parse(candidate) ?: return false
            val own = parse(current) ?: return false
            return c > own
        }
    }
}

@Serializable
data class ReleaseAsset(
    val platform: String = "",
    val file: String,
    val size: Long,
    val sha256: String,
    val signature: String,
    val url: String? = null,
)

@Serializable
data class LatestRelease(
    val version: String,
    val publishedAt: String = "",
    val notesMd: String = "",
    val asset: ReleaseAsset,
)

@Serializable
data class ReleaseManifest(
    val version: String,
    val publishedAt: String = "",
    val notesMd: String = "",
    val assets: List<ReleaseAsset> = emptyList(),
)

@Serializable
data class ReleasesResponse(val releases: List<ReleaseManifest> = emptyList())

object Releases {
    const val PLATFORM_ANDROID = Protocol.PLATFORM_ANDROID
    private val fileName = Regex("^[A-Za-z0-9._-]{1,128}$")

    fun parseLatest(status: Int, body: String): LatestRelease? = when (status) {
        204, 404 -> null
        in 200..299 -> {
            if (body.isBlank()) {
                null
            } else {
                try {
                    ProtocolJson.decodeFromString(LatestRelease.serializer(), body)
                } catch (e: Exception) {
                    throw ApiException(status, "invalid_response", "unexpected release response", cause = e)
                }
            }
        }
        else -> throw ApiException(status, "http_$status", "release check failed with HTTP $status")
    }

    fun isValidFileName(name: String): Boolean = fileName.matches(name) && name != "." && name != ".."

    fun assetPath(release: LatestRelease): String {
        val url = release.asset.url
        if (url != null && url.startsWith("/api/releases/") && !url.contains("..") && !url.contains("?") && !url.contains("#")) return url
        return "/api/releases/${release.version}/assets/${release.asset.file}"
    }
}

sealed interface UpdateDecision {
    data object NoRelease : UpdateDecision
    data class UpToDate(val release: LatestRelease) : UpdateDecision
    data class Available(val release: LatestRelease) : UpdateDecision
    data class Rejected(val release: LatestRelease, val reason: String) : UpdateDecision
}

object UpdatePolicy {
    fun decide(currentVersion: String, release: LatestRelease?, platform: String = Releases.PLATFORM_ANDROID): UpdateDecision {
        if (release == null) return UpdateDecision.NoRelease
        val latest = SemVer.parse(release.version) ?: return UpdateDecision.Rejected(release, "invalid version ${release.version}")
        val current = SemVer.parse(currentVersion) ?: return UpdateDecision.Rejected(release, "invalid current version $currentVersion")
        if (latest <= current) return UpdateDecision.UpToDate(release)
        val asset = release.asset
        if (asset.platform != platform) return UpdateDecision.Rejected(release, "asset is for ${asset.platform.ifEmpty { "an unknown platform" }}")
        if (!Releases.isValidFileName(asset.file)) return UpdateDecision.Rejected(release, "invalid asset file name")
        if (asset.size <= 0) return UpdateDecision.Rejected(release, "invalid asset size")
        if (!Hex.isLowerHex(asset.sha256, 64)) return UpdateDecision.Rejected(release, "invalid asset sha256")
        val signature = try {
            StrictBase64.decode(asset.signature)
        } catch (_: Exception) {
            null
        }
        if (signature == null || signature.size != 64) return UpdateDecision.Rejected(release, "invalid asset signature")
        return UpdateDecision.Available(release)
    }
}

sealed interface VerifyResult {
    data object Ok : VerifyResult
    data class Failed(val reason: String) : VerifyResult
}

object ReleaseVerifier {
    fun sha256(input: InputStream, progress: (Long) -> Unit = {}): ByteArray {
        val md = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(64 * 1024)
        var total = 0L
        while (true) {
            val n = input.read(buffer)
            if (n < 0) break
            md.update(buffer, 0, n)
            total += n
            progress(total)
        }
        return md.digest()
    }

    fun verifySignature(digest: ByteArray, signatureB64: String, publicKey: ByteArray = ReleaseKeys.publicKey): Boolean {
        if (digest.size != 32 || publicKey.size != 32) return false
        val signature = try {
            StrictBase64.decode(signatureB64)
        } catch (_: Exception) {
            return false
        }
        if (signature.size != 64) return false
        return try {
            val signer = Ed25519Signer()
            signer.init(false, Ed25519PublicKeyParameters(publicKey, 0))
            signer.update(digest, 0, digest.size)
            signer.verifySignature(signature)
        } catch (_: Exception) {
            false
        }
    }

    fun verifyDigest(
        digest: ByteArray,
        expectedSha256Hex: String,
        signatureB64: String,
        publicKey: ByteArray = ReleaseKeys.publicKey,
    ): VerifyResult {
        if (!Hex.isLowerHex(expectedSha256Hex, 64)) return VerifyResult.Failed("invalid expected sha256")
        if (!MessageDigest.isEqual(digest, Hex.decode(expectedSha256Hex))) return VerifyResult.Failed("sha256 mismatch")
        if (!verifySignature(digest, signatureB64, publicKey)) return VerifyResult.Failed("signature does not verify")
        return VerifyResult.Ok
    }

    fun verifyBytes(bytes: ByteArray, expectedSha256Hex: String, signatureB64: String, publicKey: ByteArray = ReleaseKeys.publicKey): VerifyResult =
        verifyDigest(MessageDigest.getInstance("SHA-256").digest(bytes), expectedSha256Hex, signatureB64, publicKey)

    fun verifyFile(file: File, asset: ReleaseAsset, publicKey: ByteArray = ReleaseKeys.publicKey): VerifyResult {
        if (!file.isFile) return VerifyResult.Failed("file missing")
        if (file.length() != asset.size) return VerifyResult.Failed("size mismatch: ${file.length()} != ${asset.size}")
        val digest = file.inputStream().use { sha256(it) }
        return verifyDigest(digest, asset.sha256, asset.signature, publicKey)
    }
}

data class MdSpan(val text: String, val bold: Boolean = false, val code: Boolean = false, val url: String? = null)

sealed interface MdBlock {
    data class Heading(val level: Int, val spans: List<MdSpan>) : MdBlock
    data class Bullet(val spans: List<MdSpan>) : MdBlock
    data class Paragraph(val spans: List<MdSpan>) : MdBlock
}

object NotesMarkdown {
    private val heading = Regex("^(#{1,6})\\s+(.*)$")
    private val bullet = Regex("^\\s*[-*]\\s+(.*)$")

    fun parse(markdown: String): List<MdBlock> {
        val blocks = ArrayList<MdBlock>()
        val paragraph = StringBuilder()
        fun flush() {
            if (paragraph.isNotEmpty()) {
                blocks += MdBlock.Paragraph(inline(paragraph.toString()))
                paragraph.setLength(0)
            }
        }
        for (raw in markdown.replace("\r\n", "\n").split('\n')) {
            val line = raw.trimEnd()
            if (line.isBlank()) {
                flush()
                continue
            }
            val h = heading.matchEntire(line.trimStart())
            if (h != null) {
                flush()
                blocks += MdBlock.Heading(h.groupValues[1].length, inline(h.groupValues[2].trim().trimEnd('#').trim()))
                continue
            }
            val b = bullet.matchEntire(line)
            if (b != null) {
                flush()
                blocks += MdBlock.Bullet(inline(b.groupValues[1].trim()))
                continue
            }
            if (paragraph.isNotEmpty()) paragraph.append(' ')
            paragraph.append(line.trim())
        }
        flush()
        return blocks
    }

    fun inline(text: String): List<MdSpan> {
        val spans = ArrayList<MdSpan>()
        val current = StringBuilder()
        var bold = false
        fun emit() {
            if (current.isNotEmpty()) {
                spans += MdSpan(current.toString(), bold = bold)
                current.setLength(0)
            }
        }
        var i = 0
        while (i < text.length) {
            val c = text[i]
            when {
                c == '\\' && i + 1 < text.length && text[i + 1] in "\\`*[]()#-_" -> {
                    current.append(text[i + 1])
                    i += 2
                }
                text.startsWith("**", i) && (bold || text.indexOf("**", i + 2) > i + 2) -> {
                    emit()
                    bold = !bold
                    i += 2
                }
                c == '`' -> {
                    val end = text.indexOf('`', i + 1)
                    if (end > i + 1) {
                        emit()
                        spans += MdSpan(text.substring(i + 1, end), bold = bold, code = true)
                        i = end + 1
                    } else {
                        current.append(c)
                        i++
                    }
                }
                c == '[' -> {
                    val close = text.indexOf(']', i + 1)
                    if (close > i + 1 && close + 1 < text.length && text[close + 1] == '(') {
                        val end = text.indexOf(')', close + 2)
                        if (end > close + 2) {
                            emit()
                            val label = text.substring(i + 1, close).replace("**", "")
                            spans += MdSpan(label, bold = bold, url = text.substring(close + 2, end).trim())
                            i = end + 1
                            continue
                        }
                    }
                    current.append(c)
                    i++
                }
                else -> {
                    current.append(c)
                    i++
                }
            }
        }
        emit()
        return spans
    }
}
