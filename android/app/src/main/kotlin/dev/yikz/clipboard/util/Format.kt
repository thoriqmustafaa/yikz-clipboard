package dev.yikz.clipboard.util

import java.text.DateFormat
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Date
import java.util.Locale
import kotlin.math.abs

object Format {
    fun bytes(value: Long): String {
        if (value < 1024) return "$value B"
        val units = listOf("KB", "MB", "GB", "TB")
        var v = value.toDouble() / 1024
        var i = 0
        while (v >= 1024 && i < units.size - 1) {
            v /= 1024
            i++
        }
        val text = if (v >= 100 || v % 1.0 == 0.0) String.format(Locale.US, "%.0f", v) else String.format(Locale.US, "%.1f", v)
        return "$text ${units[i]}"
    }

    fun time(ms: Long): String = DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(ms))

    fun dateTime(ms: Long): String = DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM)
        .withZone(ZoneId.systemDefault())
        .format(Instant.ofEpochMilli(ms))

    fun dayLabel(ms: Long, today: LocalDate = LocalDate.now()): String {
        val date = Instant.ofEpochMilli(ms).atZone(ZoneId.systemDefault()).toLocalDate()
        return when (date) {
            today -> "Today"
            today.minusDays(1) -> "Yesterday"
            else -> if (date.year == today.year) {
                DateTimeFormatter.ofPattern("EEEE, MMM d", Locale.getDefault()).format(date)
            } else {
                DateTimeFormatter.ofPattern("MMM d, yyyy", Locale.getDefault()).format(date)
            }
        }
    }

    fun ago(ms: Long, now: Long = System.currentTimeMillis()): String {
        val diff = abs(now - ms) / 1000
        return when {
            diff < 45 -> "just now"
            diff < 3600 -> "${(diff + 30) / 60} min ago"
            diff < 86_400 -> "${diff / 3600} h ago"
            diff < 7 * 86_400 -> "${diff / 86_400} d ago"
            else -> DateFormat.getDateInstance(DateFormat.MEDIUM).format(Date(ms))
        }
    }

    private val linkRegex = Regex("^(https?://|www\\.)\\S+$", RegexOption.IGNORE_CASE)

    fun isLink(text: String): Boolean {
        val t = text.trim()
        return t.length < 2048 && !t.contains('\n') && linkRegex.matches(t)
    }
}
