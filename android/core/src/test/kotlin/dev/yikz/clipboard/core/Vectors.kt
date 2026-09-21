package dev.yikz.clipboard.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import java.io.File

object Vectors {
    private val dir: File by lazy {
        val path = System.getProperty("yikz.vectors.dir") ?: error("yikz.vectors.dir is not set")
        File(path).also { require(it.isDirectory) { "vectors directory $it does not exist" } }
    }

    private val cache = HashMap<String, JsonObject>()

    fun load(name: String): JsonObject = cache.getOrPut(name) {
        Json.parseToJsonElement(File(dir, name).readText(Charsets.UTF_8)).jsonObject
    }
}

fun JsonObject.str(key: String): String = getValue(key).jsonPrimitive.content
fun JsonObject.strOrNull(key: String): String? = this[key]?.jsonPrimitive?.content
fun JsonObject.num(key: String): Long = getValue(key).jsonPrimitive.long
fun JsonObject.bool(key: String): Boolean = getValue(key).jsonPrimitive.content.toBoolean()
fun JsonObject.arr(key: String): JsonArray = getValue(key).jsonArray
fun JsonObject.obj(key: String): JsonObject = getValue(key).jsonObject
fun JsonElement.o(): JsonObject = jsonObject
fun hex(s: String): ByteArray = Hex.decode(s)

val primaryKey: MasterKey by lazy {
    MasterKey(hex(Vectors.load("keys.json").arr("keys").map { it.o() }.first { it.str("name") == "primary" }.str("key_hex")))
}

fun patternArchive(): ByteArray {
    val case = Vectors.load("files_archive.json").arr("cases").map { it.o() }.first { it.str("name") == "single_large_file_header" }
    val file = case.arr("files")[0].o()
    val size = file.num("size").toInt()
    val data = ByteArray(size) { (it % 251).toByte() }
    return Ycf1.pack(listOf(file.str("name") to data))
}
