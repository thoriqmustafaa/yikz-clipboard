package dev.yikz.clipboard.util

import android.content.ContentResolver
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.ImageDecoder
import android.net.Uri
import dev.yikz.clipboard.core.Protocol
import java.io.ByteArrayOutputStream
import java.io.File
import kotlin.math.max
import kotlin.math.roundToInt

data class PngImage(val bytes: ByteArray, val width: Int, val height: Int)

object Images {
    fun toPng(resolver: ContentResolver, uri: Uri, mime: String?): PngImage {
        val original = resolver.openInputStream(uri)?.use { it.readBytes() } ?: throw IllegalStateException("cannot read image")
        return toPng(original, mime)
    }

    fun toPng(original: ByteArray, mime: String?): PngImage {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(original, 0, original.size, bounds)
        val isPng = mime == "image/png" || (original.size > 8 && original[0] == 0x89.toByte() && original[1] == 0x50.toByte())
        if (isPng && bounds.outWidth > 0) return PngImage(original, bounds.outWidth, bounds.outHeight)
        val bitmap = ImageDecoder.decodeBitmap(ImageDecoder.createSource(java.nio.ByteBuffer.wrap(original))) { decoder, _, _ ->
            decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
        }
        val out = ByteArrayOutputStream()
        bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)
        val result = PngImage(out.toByteArray(), bitmap.width, bitmap.height)
        bitmap.recycle()
        return result
    }

    fun decodeSampled(bytes: ByteArray, maxSide: Int): Bitmap? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        if (bounds.outWidth <= 0) return null
        val options = BitmapFactory.Options().apply { inSampleSize = sampleSize(bounds.outWidth, bounds.outHeight, maxSide) }
        return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, options)
    }

    fun decodeSampled(file: File, maxSide: Int): Bitmap? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.path, bounds)
        if (bounds.outWidth <= 0) return null
        val options = BitmapFactory.Options().apply { inSampleSize = sampleSize(bounds.outWidth, bounds.outHeight, maxSide) }
        return BitmapFactory.decodeFile(file.path, options)
    }

    private fun sampleSize(width: Int, height: Int, maxSide: Int): Int {
        var sample = 1
        while (max(width, height) / (sample * 2) >= maxSide) sample *= 2
        return sample
    }

    fun thumbnail(png: ByteArray): ByteArray? {
        val source = decodeSampled(png, Protocol.THUMB_MAX_SIDE) ?: return null
        var side = Protocol.THUMB_MAX_SIDE
        var quality = 80
        try {
            while (side >= 64) {
                val scale = minOf(1f, side.toFloat() / max(source.width, source.height))
                val w = (source.width * scale).roundToInt().coerceAtLeast(1)
                val h = (source.height * scale).roundToInt().coerceAtLeast(1)
                val scaled = if (w == source.width && h == source.height) source else Bitmap.createScaledBitmap(source, w, h, true)
                val out = ByteArrayOutputStream()
                val flat = if (scaled.hasAlpha()) flatten(scaled) else scaled
                flat.compress(Bitmap.CompressFormat.JPEG, quality, out)
                if (flat !== scaled) flat.recycle()
                if (scaled !== source) scaled.recycle()
                val bytes = out.toByteArray()
                if (bytes.size + Protocol.SEAL_OVERHEAD <= Protocol.THUMB_MAX_BYTES) return bytes
                if (quality > 50) quality -= 15 else side = (side * 0.75f).toInt()
            }
            return null
        } finally {
            source.recycle()
        }
    }

    private fun flatten(bitmap: Bitmap): Bitmap {
        val out = Bitmap.createBitmap(bitmap.width, bitmap.height, Bitmap.Config.ARGB_8888)
        val canvas = android.graphics.Canvas(out)
        canvas.drawColor(android.graphics.Color.WHITE)
        canvas.drawBitmap(bitmap, 0f, 0f, null)
        return out
    }
}
