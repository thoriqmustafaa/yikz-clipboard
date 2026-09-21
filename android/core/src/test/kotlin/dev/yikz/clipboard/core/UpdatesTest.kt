package dev.yikz.clipboard.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.io.File
import java.security.MessageDigest

class UpdatesTest {
    private val vector = Vectors.load("release_signature.json")
    private val publicKey = StrictBase64.decode(vector.str("public_key_b64"))
    private val file = StrictBase64.decode(vector.str("file_b64"))
    private val sha = vector.str("sha256_hex")
    private val signature = vector.str("signature_b64")

    @Test
    fun releaseSignatureVectorVerifies() {
        assertEquals(sha, Hex.encode(MessageDigest.getInstance("SHA-256").digest(file)))
        assertEquals(VerifyResult.Ok, ReleaseVerifier.verifyBytes(file, sha, signature, publicKey))
        assertTrue(ReleaseVerifier.verifySignature(Hex.decode(sha), signature, publicKey))
    }

    @Test
    fun tamperedFileFails() {
        for (index in listOf(0, file.size / 2, file.size - 1)) {
            val tampered = file.copyOf()
            tampered[index] = (tampered[index].toInt() xor 0x01).toByte()
            val result = ReleaseVerifier.verifyBytes(tampered, sha, signature, publicKey)
            assertEquals(VerifyResult.Failed("sha256 mismatch"), result)
            val digest = MessageDigest.getInstance("SHA-256").digest(tampered)
            assertFalse(ReleaseVerifier.verifySignature(digest, signature, publicKey))
        }
    }

    @Test
    fun tamperedSignatureFails() {
        val raw = StrictBase64.decode(signature)
        for (index in listOf(0, 31, 32, 63)) {
            val tampered = raw.copyOf()
            tampered[index] = (tampered[index].toInt() xor 0x01).toByte()
            val result = ReleaseVerifier.verifyBytes(file, sha, StrictBase64.encode(tampered), publicKey)
            assertEquals(VerifyResult.Failed("signature does not verify"), result)
        }
        assertFalse(ReleaseVerifier.verifySignature(Hex.decode(sha), "not base64!", publicKey))
        assertFalse(ReleaseVerifier.verifySignature(Hex.decode(sha), StrictBase64.encode(raw.copyOf(63)), publicKey))
    }

    @Test
    fun wrongKeyFails() {
        assertFalse(ReleaseVerifier.verifySignature(Hex.decode(sha), signature, ReleaseKeys.publicKey))
        assertEquals(32, ReleaseKeys.publicKey.size)
        val otherKey = publicKey.copyOf().also { it[5] = (it[5].toInt() xor 0x40).toByte() }
        assertFalse(ReleaseVerifier.verifySignature(Hex.decode(sha), signature, otherKey))
    }

    @Test
    fun verifyFileChecksSizeHashAndSignature() {
        val tmp = File.createTempFile("release", ".bin")
        try {
            tmp.writeBytes(file)
            val asset = ReleaseAsset("android", "x.apk", file.size.toLong(), sha, signature)
            assertEquals(VerifyResult.Ok, ReleaseVerifier.verifyFile(tmp, asset, publicKey))
            assertTrue(ReleaseVerifier.verifyFile(tmp, asset.copy(size = file.size + 1L), publicKey) is VerifyResult.Failed)
            assertTrue(ReleaseVerifier.verifyFile(tmp, asset, ReleaseKeys.publicKey) is VerifyResult.Failed)
            tmp.writeBytes(file.copyOf().also { it[0] = (it[0] + 1).toByte() })
            assertTrue(ReleaseVerifier.verifyFile(tmp, asset, publicKey) is VerifyResult.Failed)
        } finally {
            tmp.delete()
        }
    }

    @Test
    fun semverCompare() {
        assertEquals(SemVer(1, 1, 0), SemVer.parse("1.1.0"))
        assertEquals(10100, SemVer.parse("1.1.0")!!.versionCode)
        assertEquals(10000, SemVer.parse("1.0.0")!!.versionCode)
        assertTrue(SemVer.parse("1.10.0")!! > SemVer.parse("1.9.9")!!)
        assertTrue(SemVer.parse("2.0.0")!! > SemVer.parse("1.99.99")!!)
        assertTrue(SemVer.parse("1.0.10")!! > SemVer.parse("1.0.9")!!)
        assertEquals(0, SemVer.parse("1.2.3")!!.compareTo(SemVer(1, 2, 3)))
        assertTrue(SemVer.isNewer("1.1.0", "1.0.0"))
        assertFalse(SemVer.isNewer("1.0.0", "1.0.0"))
        assertFalse(SemVer.isNewer("0.9.0", "1.0.0"))
        assertFalse(SemVer.isNewer("garbage", "1.0.0"))
        for (bad in listOf("", "1", "1.0", "1.0.0.0", "v1.0.0", "1.0.0-beta", "01.0.0", "1.-1.0", "a.b.c")) {
            assertNull(bad, SemVer.parse(bad))
        }
    }

    private fun release(version: String, platform: String = "android", file: String = "yikz-clipboard-$version.apk") = LatestRelease(
        version = version,
        publishedAt = "2026-09-22T10:00:00.000Z",
        notesMd = "### Android\n- Updates\n",
        asset = ReleaseAsset(platform, file, 12_000_000, sha, signature, "/api/releases/$version/assets/$file"),
    )

    @Test
    fun decisionLogic() {
        assertEquals(UpdateDecision.NoRelease, UpdatePolicy.decide("1.0.0", null))
        assertEquals(UpdateDecision.Available(release("1.1.0")), UpdatePolicy.decide("1.0.0", release("1.1.0")))
        assertEquals(UpdateDecision.UpToDate(release("1.1.0")), UpdatePolicy.decide("1.1.0", release("1.1.0")))
        assertEquals(UpdateDecision.UpToDate(release("1.0.5")), UpdatePolicy.decide("1.1.0", release("1.0.5")))
        assertTrue(UpdatePolicy.decide("1.0.0", release("1.1.0", platform = "macos")) is UpdateDecision.Rejected)
        assertTrue(UpdatePolicy.decide("1.0.0", release("1.1.0", file = "../evil.apk")) is UpdateDecision.Rejected)
        assertTrue(UpdatePolicy.decide("1.0.0", release("1.1")) is UpdateDecision.Rejected)
        val bad = release("1.1.0")
        assertTrue(UpdatePolicy.decide("1.0.0", bad.copy(asset = bad.asset.copy(sha256 = "ABC"))) is UpdateDecision.Rejected)
        assertTrue(UpdatePolicy.decide("1.0.0", bad.copy(asset = bad.asset.copy(signature = "AAAA"))) is UpdateDecision.Rejected)
        assertTrue(UpdatePolicy.decide("1.0.0", bad.copy(asset = bad.asset.copy(size = 0))) is UpdateDecision.Rejected)
    }

    @Test
    fun parsesLatestReleaseBody() {
        val body = """
            {"version":"1.1.0","published_at":"2026-09-22T10:00:00.000Z","notes_md":"### Android\n- In-app updates\n",
             "asset":{"platform":"android","file":"yikz-clipboard-1.1.0.apk","size":12345678,
             "sha256":"$sha","signature":"$signature",
             "url":"/api/releases/1.1.0/assets/yikz-clipboard-1.1.0.apk","extra":true},"future":1}
        """.trimIndent()
        val parsed = Releases.parseLatest(200, body)!!
        assertEquals("1.1.0", parsed.version)
        assertEquals("2026-09-22T10:00:00.000Z", parsed.publishedAt)
        assertEquals("### Android\n- In-app updates\n", parsed.notesMd)
        assertEquals("android", parsed.asset.platform)
        assertEquals("yikz-clipboard-1.1.0.apk", parsed.asset.file)
        assertEquals(12345678L, parsed.asset.size)
        assertEquals(sha, parsed.asset.sha256)
        assertEquals(signature, parsed.asset.signature)
        assertEquals("/api/releases/1.1.0/assets/yikz-clipboard-1.1.0.apk", Releases.assetPath(parsed))
        assertEquals("/api/releases/1.1.0/assets/yikz-clipboard-1.1.0.apk", Releases.assetPath(parsed.copy(asset = parsed.asset.copy(url = null))))
        assertEquals("/api/releases/1.1.0/assets/yikz-clipboard-1.1.0.apk", Releases.assetPath(parsed.copy(asset = parsed.asset.copy(url = "https://evil.example/x.apk"))))
        assertTrue(UpdatePolicy.decide("1.0.0", parsed) is UpdateDecision.Available)
    }

    @Test
    fun parsesNoRelease() {
        assertNull(Releases.parseLatest(204, ""))
        assertNull(Releases.parseLatest(404, """{"code":"not_found","message":"no release"}"""))
        try {
            Releases.parseLatest(200, "{not json")
            fail("expected ApiException")
        } catch (e: ApiException) {
            assertEquals("invalid_response", e.code)
        }
        try {
            Releases.parseLatest(500, "")
            fail("expected ApiException")
        } catch (e: ApiException) {
            assertEquals(500, e.status)
        }
    }

    @Test
    fun releaseAvailableMessageDecodes() {
        val msg = WsCodec.decode("""{"type":"release_available","version":"1.1.0"}""")
        assertEquals(ReleaseAvailableMsg(version = "1.1.0"), msg)
        assertEquals("""{"type":"release_available","version":"1.1.0"}""", WsCodec.encode(msg))
    }

    @Test
    fun parsesNotesMarkdown() {
        val md = "### macOS\n- Dock icon **toggle** with `defaults`\n- See [docs](https://clip.yikz.dev/changelog)\n\nPlain **bold** text\ncontinued\n### Android\n* Updates\n"
        val blocks = NotesMarkdown.parse(md)
        assertEquals(6, blocks.size)
        assertEquals(MdBlock.Heading(3, listOf(MdSpan("macOS"))), blocks[0])
        assertEquals(
            MdBlock.Bullet(listOf(MdSpan("Dock icon "), MdSpan("toggle", bold = true), MdSpan(" with "), MdSpan("defaults", code = true))),
            blocks[1],
        )
        assertEquals(MdBlock.Bullet(listOf(MdSpan("See "), MdSpan("docs", url = "https://clip.yikz.dev/changelog"))), blocks[2])
        assertEquals(MdBlock.Paragraph(listOf(MdSpan("Plain "), MdSpan("bold", bold = true), MdSpan(" text continued"))), blocks[3])
        assertEquals(MdBlock.Heading(3, listOf(MdSpan("Android"))), blocks[4])
        assertEquals(MdBlock.Bullet(listOf(MdSpan("Updates"))), blocks[5])
        assertEquals(listOf(MdSpan("2 ** 3 and [x] `")), NotesMarkdown.inline("2 ** 3 and [x] `"))
    }
}
