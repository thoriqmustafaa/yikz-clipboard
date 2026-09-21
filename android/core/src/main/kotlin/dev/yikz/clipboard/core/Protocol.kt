package dev.yikz.clipboard.core

object Protocol {
    const val VERSION = 1
    const val KDF_ALGORITHM = "pbkdf2-sha256"
    const val KDF_ITERATIONS = 600_000
    const val KEY_LENGTH = 32
    const val SALT_LENGTH = 16
    const val INLINE_MAX_BYTES = 262_144L
    const val CHUNK_SIZE_BYTES = 4_194_304L
    const val SEAL_OVERHEAD = 28
    const val MAX_CHUNK_BODY_BYTES = CHUNK_SIZE_BYTES + SEAL_OVERHEAD
    const val META_MAX_BYTES = 65_536
    const val THUMB_MAX_BYTES = 65_536
    const val MAX_FILES_PER_ITEM = 1000
    const val PREVIEW_MAX_CODE_POINTS = 500
    const val THUMB_MAX_SIDE = 320
    const val HISTORY_DEFAULT_LIMIT = 100
    const val HISTORY_MAX_LIMIT = 500
    const val HEARTBEAT_INTERVAL_MS = 20_000L
    const val DEAD_TIMEOUT_MS = 45_000L
    const val FOREGROUND_PROBE_MS = 5_000L
    const val AUTO_APPLY_MAX_AGE_MS = 300_000L
    const val DEFAULT_AUTO_DOWNLOAD_BYTES = 52_428_800L
    const val RECENT_HASHES = 32
    const val PLATFORM_ANDROID = "android"

    const val MIME_TEXT = "text/plain; charset=utf-8"
    const val MIME_PNG = "image/png"
    const val MIME_FILES = "application/x-yikz-files"
}

object Kind {
    const val TEXT = "text"
    const val IMAGE = "image"
    const val FILES = "files"

    fun isValid(kind: String): Boolean = kind == TEXT || kind == IMAGE || kind == FILES
}
