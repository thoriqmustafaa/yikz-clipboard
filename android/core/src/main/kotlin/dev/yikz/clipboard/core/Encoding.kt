package dev.yikz.clipboard.core

import java.time.Instant
import java.time.OffsetDateTime
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.Base64

object Hex {
    private val digits = "0123456789abcdef".toCharArray()

    fun encode(bytes: ByteArray): String {
        val out = CharArray(bytes.size * 2)
        for (i in bytes.indices) {
            val v = bytes[i].toInt() and 0xff
            out[i * 2] = digits[v ushr 4]
            out[i * 2 + 1] = digits[v and 0x0f]
        }
        return String(out)
    }

    fun decode(text: String): ByteArray {
        require(text.length % 2 == 0) { "odd hex length" }
        return ByteArray(text.length / 2) { i ->
            ((nibble(text[i * 2]) shl 4) or nibble(text[i * 2 + 1])).toByte()
        }
    }

    fun isLowerHex(text: String, length: Int): Boolean =
        text.length == length && text.all { it in '0'..'9' || it in 'a'..'f' }

    private fun nibble(c: Char): Int = when (c) {
        in '0'..'9' -> c - '0'
        in 'a'..'f' -> c - 'a' + 10
        in 'A'..'F' -> c - 'A' + 10
        else -> throw IllegalArgumentException("invalid hex character")
    }
}

object StrictBase64 {
    fun encode(bytes: ByteArray): String = Base64.getEncoder().encodeToString(bytes)

    fun decode(text: String): ByteArray {
        if (text.length % 4 != 0) throw IllegalArgumentException("base64 length is not a multiple of 4")
        var padding = 0
        for ((index, c) in text.withIndex()) {
            val ok = c in 'A'..'Z' || c in 'a'..'z' || c in '0'..'9' || c == '+' || c == '/'
            if (c == '=') {
                if (index < text.length - 2) throw IllegalArgumentException("misplaced base64 padding")
                padding++
            } else if (!ok || padding > 0) {
                throw IllegalArgumentException("invalid base64 character")
            }
        }
        return Base64.getDecoder().decode(text)
    }
}

object Timestamps {
    private val formatter = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'").withZone(ZoneOffset.UTC)

    fun format(epochMillis: Long): String = formatter.format(Instant.ofEpochMilli(epochMillis))

    fun parse(text: String): Long =
        OffsetDateTime.parse(text.uppercase(), DateTimeFormatter.ISO_OFFSET_DATE_TIME).toInstant().toEpochMilli()

    fun parseOrNull(text: String?): Long? = try {
        if (text.isNullOrEmpty()) null else parse(text)
    } catch (_: Exception) {
        null
    }
}
