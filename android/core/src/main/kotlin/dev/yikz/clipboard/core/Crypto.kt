package dev.yikz.clipboard.core

import java.io.InputStream
import java.security.GeneralSecurityException
import java.security.MessageDigest
import java.security.SecureRandom
import java.text.Normalizer
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec

class CryptoException(message: String, cause: Throwable? = null) : Exception(message, cause)

internal val secureRandom: SecureRandom by lazy { SecureRandom() }

fun randomBytes(count: Int): ByteArray = ByteArray(count).also { secureRandom.nextBytes(it) }

object Kdf {
    private const val SELF_TEST_PASSWORD = "pässwörd 🔐 日本語"
    private const val SELF_TEST_SALT = "00112233445566778899aabbccddeeff"
    private const val SELF_TEST_KEY = "056f3b0ef923d739c22d74501bf19980b5cb7a4adf3a6964d600594f6651c764"

    fun normalize(password: String): String = Normalizer.normalize(password, Normalizer.Form.NFC)

    val platformUsable: Boolean by lazy {
        val key = platformPbkdf2(normalize(SELF_TEST_PASSWORD), Hex.decode(SELF_TEST_SALT), 1000)
        key != null && Hex.encode(key) == SELF_TEST_KEY
    }

    fun derive(password: String, salt: ByteArray, iterations: Int = Protocol.KDF_ITERATIONS): ByteArray {
        require(password.isNotEmpty()) { "empty password" }
        val normalized = normalize(password)
        if (platformUsable) {
            platformPbkdf2(normalized, salt, iterations)?.let { return it }
        }
        return pbkdf2(normalized.toByteArray(Charsets.UTF_8), salt, iterations, Protocol.KEY_LENGTH)
    }

    fun platformPbkdf2(normalizedPassword: String, salt: ByteArray, iterations: Int): ByteArray? = try {
        val spec = PBEKeySpec(normalizedPassword.toCharArray(), salt, iterations, Protocol.KEY_LENGTH * 8)
        try {
            SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).encoded
        } finally {
            spec.clearPassword()
        }
    } catch (_: Exception) {
        null
    }

    fun pbkdf2(password: ByteArray, salt: ByteArray, iterations: Int, keyLength: Int): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(password, "HmacSHA256"))
        val hashLength = mac.macLength
        val blocks = (keyLength + hashLength - 1) / hashLength
        val output = ByteArray(keyLength)
        val u = ByteArray(hashLength)
        for (block in 1..blocks) {
            mac.update(salt)
            mac.update(
                byteArrayOf(
                    (block ushr 24).toByte(), (block ushr 16).toByte(), (block ushr 8).toByte(), block.toByte(),
                ),
            )
            mac.doFinal(u, 0)
            val t = u.copyOf()
            for (i in 1 until iterations) {
                mac.update(u)
                mac.doFinal(u, 0)
                for (j in 0 until hashLength) t[j] = (t[j].toInt() xor u[j].toInt()).toByte()
            }
            val offset = (block - 1) * hashLength
            t.copyInto(output, offset, 0, minOf(hashLength, keyLength - offset))
        }
        return output
    }
}

object Hashing {
    fun hmac(key: ByteArray, data: ByteArray): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        return mac.doFinal(data)
    }

    fun sha256(data: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(data)

    fun sha256Hex(data: ByteArray): String = Hex.encode(sha256(data))
}

object Aad {
    fun meta(itemId: String): ByteArray = "yc1|meta|$itemId".toByteArray(Charsets.UTF_8)
    fun payload(itemId: String): ByteArray = "yc1|payload|$itemId".toByteArray(Charsets.UTF_8)
    fun thumb(itemId: String): ByteArray = "yc1|thumb|$itemId".toByteArray(Charsets.UTF_8)
    fun chunk(itemId: String, index: Int, chunkCount: Int): ByteArray =
        "yc1|chunk|$itemId|$index|$chunkCount".toByteArray(Charsets.UTF_8)
}

object Aead {
    const val NONCE_LENGTH = 12
    const val TAG_LENGTH = 16
    const val OVERHEAD = NONCE_LENGTH + TAG_LENGTH

    fun randomNonce(): ByteArray = randomBytes(NONCE_LENGTH)

    fun seal(key: ByteArray, aad: ByteArray, plaintext: ByteArray, nonce: ByteArray = randomNonce()): ByteArray =
        seal(key, aad, plaintext, 0, plaintext.size, nonce)

    fun seal(
        key: ByteArray,
        aad: ByteArray,
        plaintext: ByteArray,
        offset: Int,
        length: Int,
        nonce: ByteArray = randomNonce(),
    ): ByteArray {
        require(nonce.size == NONCE_LENGTH) { "nonce must be 12 bytes" }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_LENGTH * 8, nonce))
        cipher.updateAAD(aad)
        val out = ByteArray(NONCE_LENGTH + length + TAG_LENGTH)
        nonce.copyInto(out)
        val written = cipher.doFinal(plaintext, offset, length, out, NONCE_LENGTH)
        check(written == length + TAG_LENGTH)
        return out
    }

    fun open(key: ByteArray, aad: ByteArray, sealed: ByteArray): ByteArray {
        if (sealed.size < OVERHEAD) throw CryptoException("sealed box is shorter than 28 bytes")
        try {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(
                Cipher.DECRYPT_MODE,
                SecretKeySpec(key, "AES"),
                GCMParameterSpec(TAG_LENGTH * 8, sealed, 0, NONCE_LENGTH),
            )
            cipher.updateAAD(aad)
            return cipher.doFinal(sealed, NONCE_LENGTH, sealed.size - NONCE_LENGTH)
        } catch (e: GeneralSecurityException) {
            throw CryptoException("authentication failed", e)
        }
    }
}

class MasterKey(key: ByteArray) {
    private val keyBytes: ByteArray = key.copyOf()

    init {
        require(keyBytes.size == Protocol.KEY_LENGTH) { "key must be 32 bytes" }
    }

    val bytes: ByteArray get() = keyBytes.copyOf()

    val contentHashKey: ByteArray = Hashing.hmac(keyBytes, "yikz-clipboard/v1/content-hash".toByteArray(Charsets.UTF_8))

    val keyCheck: String = Hex.encode(Hashing.hmac(keyBytes, "yikz-clipboard/v1/key-check".toByteArray(Charsets.UTF_8)))

    fun contentHash(content: ByteArray): String = Hex.encode(Hashing.hmac(contentHashKey, content))

    fun digest(): ContentDigest = ContentDigest(contentHashKey)

    fun seal(aad: ByteArray, plaintext: ByteArray, nonce: ByteArray = Aead.randomNonce()): ByteArray =
        Aead.seal(keyBytes, aad, plaintext, nonce)

    fun seal(aad: ByteArray, plaintext: ByteArray, offset: Int, length: Int): ByteArray =
        Aead.seal(keyBytes, aad, plaintext, offset, length)

    fun open(aad: ByteArray, sealed: ByteArray): ByteArray = Aead.open(keyBytes, aad, sealed)
}

class ContentDigest(contentHashKey: ByteArray) {
    private val mac: Mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(contentHashKey, "HmacSHA256")) }
    private val sha: MessageDigest = MessageDigest.getInstance("SHA-256")
    var length: Long = 0
        private set

    fun update(bytes: ByteArray, offset: Int = 0, count: Int = bytes.size) {
        mac.update(bytes, offset, count)
        sha.update(bytes, offset, count)
        length += count
    }

    fun update(input: InputStream): ContentDigest {
        val buffer = ByteArray(64 * 1024)
        while (true) {
            val n = input.read(buffer)
            if (n < 0) break
            if (n > 0) update(buffer, 0, n)
        }
        return this
    }

    fun finish(): Digests = Digests(Hex.encode(mac.doFinal()), Hex.encode(sha.digest()), length)
}

data class Digests(val contentHash: String, val sha256: String, val length: Long)
