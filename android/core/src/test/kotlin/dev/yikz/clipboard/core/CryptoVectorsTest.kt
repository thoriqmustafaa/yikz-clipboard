package dev.yikz.clipboard.core

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

class CryptoVectorsTest {
    private val kdf = Vectors.load("kdf.json")
    private val keys = Vectors.load("keys.json")
    private val aead = Vectors.load("aead.json")

    private fun kdfCase(name: String) = kdf.arr("cases").map { it.o() }.first { it.str("name") == name }

    @Test
    fun kdfParameters() {
        assertEquals(Protocol.KDF_ALGORITHM, kdf.str("algorithm"))
        assertEquals(Protocol.KEY_LENGTH.toLong(), kdf.num("key_length"))
    }

    @Test
    fun kdfFastAndUnicodeCases() {
        for (name in listOf("fast", "unicode_nfd_input")) {
            val case = kdfCase(name)
            val salt = StrictBase64.decode(case.str("salt_b64"))
            assertArrayEquals(hex(case.str("salt_hex")), salt)
            val normalized = Kdf.normalize(case.str("password_input"))
            assertEquals(case.str("password_normalized"), normalized)
            assertEquals(case.str("password_utf8_hex"), Hex.encode(normalized.toByteArray(Charsets.UTF_8)))
            val iterations = case.num("iterations").toInt()
            assertEquals(name, case.str("key_hex"), Hex.encode(Kdf.derive(case.str("password_input"), salt, iterations)))
            assertEquals(
                name,
                case.str("key_hex"),
                Hex.encode(Kdf.pbkdf2(normalized.toByteArray(Charsets.UTF_8), salt, iterations, 32)),
            )
        }
    }

    @Test
    fun kdfPrimaryCaseWith600000Iterations() {
        val case = kdfCase("primary")
        assertEquals(600000L, case.num("iterations"))
        val key = Kdf.derive(case.str("password_input"), StrictBase64.decode(case.str("salt_b64")))
        assertEquals(case.str("key_hex"), Hex.encode(key))
    }

    @Test
    fun platformKdfPassesSelfTest() {
        assertTrue(Kdf.platformUsable)
    }

    @Test
    fun subkeysAndContentHashes() {
        assertEquals("yikz-clipboard/v1/content-hash", keys.str("content_hash_label_utf8"))
        assertEquals("yikz-clipboard/v1/key-check", keys.str("key_check_label_utf8"))
        for (entry in keys.arr("keys").map { it.o() }) {
            val key = MasterKey(hex(entry.str("key_hex")))
            assertEquals(entry.str("content_hash_key_hex"), Hex.encode(key.contentHashKey))
            assertEquals(entry.str("key_check"), key.keyCheck)
            for (sample in entry.arr("content_hash_samples").map { it.o() }) {
                val content = hex(sample.str("content_hex"))
                assertEquals(sample.str("content_utf8"), String(content, Charsets.UTF_8))
                assertEquals(sample.str("content_hash"), key.contentHash(content))
                assertEquals(sample.str("sha256"), Hashing.sha256Hex(content))
                val digests = key.digest().apply { update(content) }.finish()
                assertEquals(sample.str("content_hash"), digests.contentHash)
                assertEquals(sample.str("sha256"), digests.sha256)
            }
        }
    }

    @Test
    fun aadFormats() {
        val formats = aead.obj("aad_formats")
        assertEquals("yc1|meta|<item_id>", formats.str("meta"))
        assertEquals("yc1|payload|<item_id>", formats.str("payload"))
        assertEquals("yc1|thumb|<item_id>", formats.str("thumb"))
        assertEquals("yc1|chunk|<item_id>|<index>|<chunk_count>", formats.str("chunk"))
    }

    @Test
    fun aeadPositiveCases() {
        for (case in aead.arr("positive").map { it.o() }) {
            val name = case.str("name")
            val key = hex(case.str("key_hex"))
            val aadText = case.str("aad_utf8")
            val aad = hex(case.str("aad_hex"))
            assertEquals(name, aadText, String(aad, Charsets.UTF_8))
            val parts = aadText.split("|")
            val rebuilt = when (case.str("purpose")) {
                "meta" -> Aad.meta(parts[2])
                "payload" -> Aad.payload(parts[2])
                "thumb" -> Aad.thumb(parts[2])
                "chunk" -> Aad.chunk(parts[2], parts[3].toInt(), parts[4].toInt())
                else -> error("unknown purpose")
            }
            assertArrayEquals(name, aad, rebuilt)
            val plaintext = hex(case.str("plaintext_hex"))
            case.strOrNull("plaintext_utf8")?.let { assertEquals(name, it, String(plaintext, Charsets.UTF_8)) }
            val sealed = Aead.seal(key, rebuilt, plaintext, hex(case.str("nonce_hex")))
            assertEquals(name, case.str("sealed_hex"), Hex.encode(sealed))
            assertEquals(name, case.str("sealed_b64"), StrictBase64.encode(sealed))
            assertEquals(name, case.num("sealed_length"), sealed.size.toLong())
            assertEquals(plaintext.size + 28, sealed.size)
            assertArrayEquals(name, plaintext, Aead.open(key, aad, StrictBase64.decode(case.str("sealed_b64"))))
        }
    }

    @Test
    fun aeadNegativeCases() {
        for (case in aead.arr("negative").map { it.o() }) {
            assertEquals("decrypt_error", case.str("expect"))
            val sealed = hex(case.str("sealed_hex"))
            if (case.str("sealed_b64").isNotEmpty()) assertArrayEquals(sealed, StrictBase64.decode(case.str("sealed_b64")))
            try {
                Aead.open(hex(case.str("key_hex")), hex(case.str("aad_hex")), sealed)
                fail("${case.str("name")} should not decrypt")
            } catch (_: CryptoException) {
            }
        }
    }

    @Test
    fun randomNoncesDiffer() {
        val key = primaryKey
        val a = key.seal(Aad.payload("x"), byteArrayOf(1, 2, 3))
        val b = key.seal(Aad.payload("x"), byteArrayOf(1, 2, 3))
        assertFalse(a.contentEquals(b))
        assertArrayEquals(byteArrayOf(1, 2, 3), key.open(Aad.payload("x"), a))
    }

    @Test
    fun strictBase64RejectsUnpadded() {
        assertArrayEquals("ab".toByteArray(), StrictBase64.decode("YWI="))
        for (bad in listOf("YWI", "YW=I", "YWI=\n", "YW-_")) {
            try {
                StrictBase64.decode(bad)
                fail("accepted $bad")
            } catch (_: IllegalArgumentException) {
            }
        }
    }
}
