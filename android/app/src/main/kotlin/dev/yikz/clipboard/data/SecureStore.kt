package dev.yikz.clipboard.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class SecureStore(context: Context) {
    private val prefs = context.getSharedPreferences("secure_store", Context.MODE_PRIVATE)
    private val lock = Any()

    private fun wrappingKey(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (keyStore.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return generator.generateKey()
    }

    fun put(name: String, value: ByteArray?) = synchronized(lock) {
        if (value == null) {
            prefs.edit().remove(name).commit()
            return@synchronized
        }
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, wrappingKey())
        cipher.updateAAD(name.toByteArray())
        val sealed = cipher.iv + cipher.doFinal(value)
        prefs.edit().putString(name, Base64.encodeToString(sealed, Base64.NO_WRAP)).commit()
    }

    fun get(name: String): ByteArray? = synchronized(lock) {
        val stored = prefs.getString(name, null) ?: return@synchronized null
        try {
            val sealed = Base64.decode(stored, Base64.NO_WRAP)
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, wrappingKey(), GCMParameterSpec(128, sealed, 0, 12))
            cipher.updateAAD(name.toByteArray())
            cipher.doFinal(sealed, 12, sealed.size - 12)
        } catch (_: Exception) {
            null
        }
    }

    var token: String?
        get() = get(TOKEN)?.toString(Charsets.US_ASCII)
        set(value) = put(TOKEN, value?.toByteArray(Charsets.US_ASCII))

    var masterKey: ByteArray?
        get() = get(KEY)
        set(value) = put(KEY, value)

    companion object {
        private const val ANDROID_KEYSTORE = "AndroidKeyStore"
        private const val ALIAS = "yikz_wrapping_key"
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
        private const val TOKEN = "device_token"
        private const val KEY = "master_key"
    }
}
