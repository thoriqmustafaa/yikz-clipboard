import base64
import hashlib
import hmac
import json
import re
import struct
import sys
import unicodedata
from pathlib import Path

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

VECTORS = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[2] / "vectors"
CHUNK = 4194304
INLINE_MAX = 262144
UUID_RE = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")
checks = 0


def load(name):
    return json.loads((VECTORS / name).read_text(encoding="utf-8"))


def ok(condition, label):
    global checks
    if not condition:
        raise SystemExit("FAIL: " + label)
    checks += 1


def seal_open(key, sealed, aad):
    return AESGCM(key).decrypt(sealed[:12], sealed[12:], aad)


def seal(key, nonce, aad, plaintext):
    return nonce + AESGCM(key).encrypt(nonce, plaintext, aad)


def hash_key(key):
    return hmac.new(key, b"yikz-clipboard/v1/content-hash", hashlib.sha256).digest()


def content_hash(key, content):
    return hmac.new(hash_key(key), content, hashlib.sha256).hexdigest()


def key_check(key):
    return hmac.new(key, b"yikz-clipboard/v1/key-check", hashlib.sha256).hexdigest()


def aad_chunk(item_id, index, count):
    return f"yc1|chunk|{item_id}|{index}|{count}".encode()


def verify_kdf():
    keys = {}
    for case in load("kdf.json")["cases"]:
        normalized = unicodedata.normalize("NFC", case["password_input"])
        ok(normalized == case["password_normalized"], "nfc " + case["name"])
        ok(normalized.encode().hex() == case["password_utf8_hex"], "utf8 " + case["name"])
        salt = base64.b64decode(case["salt_b64"], validate=True)
        ok(salt.hex() == case["salt_hex"], "salt " + case["name"])
        key = hashlib.pbkdf2_hmac("sha256", normalized.encode(), salt, case["iterations"], 32)
        ok(key.hex() == case["key_hex"], "kdf " + case["name"])
        keys[case["name"]] = key
    unicode_case = load("kdf.json")["cases"][2]
    ok(unicode_case["password_input"] != unicode_case["password_normalized"], "unicode input is not already NFC")
    return keys


def verify_keys(kdf_keys):
    for entry in load("keys.json")["keys"]:
        key = bytes.fromhex(entry["key_hex"])
        ok(key == kdf_keys[entry["name"]], "key matches kdf " + entry["name"])
        ok(hash_key(key).hex() == entry["content_hash_key_hex"], "hash key " + entry["name"])
        ok(key_check(key) == entry["key_check"], "key check " + entry["name"])
        for sample in entry["content_hash_samples"]:
            content = bytes.fromhex(sample["content_hex"])
            ok(content.decode() == sample["content_utf8"], "sample utf8")
            ok(content_hash(key, content) == sample["content_hash"], "content hash sample")
            ok(hashlib.sha256(content).hexdigest() == sample["sha256"], "sha256 sample")


def verify_aead():
    data = load("aead.json")
    for case in data["positive"]:
        key = bytes.fromhex(case["key_hex"])
        nonce = bytes.fromhex(case["nonce_hex"])
        aad = bytes.fromhex(case["aad_hex"])
        ok(aad.decode() == case["aad_utf8"], "aad utf8 " + case["name"])
        plaintext = bytes.fromhex(case["plaintext_hex"])
        sealed = bytes.fromhex(case["sealed_hex"])
        ok(base64.b64decode(case["sealed_b64"], validate=True) == sealed, "b64 " + case["name"])
        ok(len(sealed) == case["sealed_length"] == len(plaintext) + 28, "length " + case["name"])
        ok(seal(key, nonce, aad, plaintext) == sealed, "seal " + case["name"])
        ok(seal_open(key, sealed, aad) == plaintext, "open " + case["name"])
        if "plaintext_utf8" in case:
            ok(plaintext.decode() == case["plaintext_utf8"], "plaintext utf8 " + case["name"])
    for case in data["negative"]:
        key = bytes.fromhex(case["key_hex"])
        sealed = bytes.fromhex(case["sealed_hex"])
        aad = bytes.fromhex(case["aad_hex"])
        failed = False
        try:
            if len(sealed) < 28:
                raise InvalidTag()
            seal_open(key, sealed, aad)
        except InvalidTag:
            failed = True
        ok(failed, "negative " + case["name"])


def uuid_v7(unix_ms, random):
    b = bytearray(unix_ms.to_bytes(6, "big") + random)
    b[6] = 0x70 | (b[6] & 0x0F)
    b[8] = 0x80 | (b[8] & 0x3F)
    h = b.hex()
    return f"{h[0:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:32]}"


def verify_uuid():
    data = load("uuidv7.json")
    ok(data["regex"] == UUID_RE.pattern, "uuid regex")
    for case in data["generate"]:
        ok(uuid_v7(case["unix_ms"], bytes.fromhex(case["random_hex"])) == case["expected"], "uuid generate")
    for value in data["valid"]:
        ok(UUID_RE.match(value) is not None, "uuid valid " + value)
    for case in data["invalid"]:
        ok(UUID_RE.match(case["value"]) is None, "uuid invalid " + case["reason"])


def pack(files):
    out = b"YCF1" + struct.pack(">I", len(files))
    for name, data in files:
        encoded = name.encode()
        out += struct.pack(">I", len(encoded)) + encoded + struct.pack(">Q", len(data)) + data
    return out


def unpack(archive):
    ok(archive[:4] == b"YCF1", "archive magic")
    (count,) = struct.unpack(">I", archive[4:8])
    pos = 8
    files = []
    for _ in range(count):
        (name_len,) = struct.unpack(">I", archive[pos:pos + 4])
        pos += 4
        name = archive[pos:pos + name_len].decode()
        pos += name_len
        (size,) = struct.unpack(">Q", archive[pos:pos + 8])
        pos += 8
        files.append((name, archive[pos:pos + size]))
        pos += size
    ok(pos == len(archive), "archive has no trailing bytes")
    return files


def valid_name(name):
    raw = name.encode()
    if not raw or len(raw) > 255 or name in (".", ".."):
        return False
    return not any(c in "/\\" or ord(c) < 0x20 or ord(c) == 0x7F for c in name)


def pattern(n):
    return bytes(i % 251 for i in range(n))


def verify_archive():
    data = load("files_archive.json")
    first = data["cases"][0]
    files = [(f["name"], bytes.fromhex(f["data_hex"])) for f in first["files"]]
    for f in first["files"]:
        ok(f["name"].encode().hex() == f["name_utf8_hex"], "archive name hex")
        ok(unicodedata.normalize("NFC", f["name"]) == f["name"], "archive name nfc")
        ok(valid_name(f["name"]), "archive name valid")
    archive = pack(files)
    ok(archive.hex() == first["archive_hex"], "archive bytes")
    ok(len(archive) == first["archive_size"], "archive size")
    ok(hashlib.sha256(archive).hexdigest() == first["archive_sha256"], "archive sha")
    ok(unpack(archive) == files, "archive roundtrip")
    second = data["cases"][1]
    spec = second["files"][0]
    big = pack([(spec["name"], pattern(spec["size"]))])
    ok(big.hex().startswith(second["archive_prefix_hex"]), "large archive prefix")
    ok(len(big) == second["archive_size"], "large archive size")
    ok(hashlib.sha256(big).hexdigest() == second["archive_sha256"], "large archive sha")
    for case in data["invalid_names"]:
        ok(not valid_name(case["name"]), "invalid name " + case["reason"])
    return big


def verify_chunking():
    for case in load("chunking.json")["cases"]:
        size = case["size"]
        count = 0 if size <= INLINE_MAX else -(-size // CHUNK)
        ok(case["chunk_count"] == count, f"chunk count {size}")
        ok(case["inline"] == (count == 0), f"inline {size}")
        if count == 0:
            ok(case["payload_sealed_length"] == size + 28, f"payload length {size}")
        else:
            last = size - (count - 1) * CHUNK
            ok(case["last_chunk_plaintext_size"] == last, f"last chunk {size}")
            ok(case["last_chunk_sealed_length"] == last + 28, f"last sealed {size}")
            ok(case["total_chunk_sealed_bytes"] == size + 28 * count, f"total sealed {size}")


def verify_preview():
    for case in load("preview.json")["cases"]:
        text = case["input"]
        ok(len(text) == case["input_code_points"], "preview input length " + case["name"])
        ok(len(text.encode("utf-16-le")) // 2 == case["input_utf16_units"], "utf16 units " + case["name"])
        ok(text[:500] == case["expected"], "preview " + case["name"])
        ok(len(case["expected"]) == case["expected_code_points"], "preview length " + case["name"])


def verify_token():
    data = load("token.json")
    for case in data["cases"]:
        token = "yc_" + base64.urlsafe_b64encode(bytes.fromhex(case["random_hex"])).decode().rstrip("=")
        ok(token == case["token"], "token format")
        ok(re.match(data["regex"], token) is not None, "token regex")
        ok(hashlib.sha256(token.encode()).hexdigest() == case["token_hash"], "token hash")


def verify_items(key, large_archive):
    headers = {}
    for it in load("items.json")["items"]:
        item_id = it["id"]
        ok(UUID_RE.match(item_id) is not None, "item id")
        meta_sealed = base64.b64decode(it["meta_sealed_b64"], validate=True)
        ok(meta_sealed[:12].hex() == it["meta_nonce_hex"], "meta nonce prefix")
        ok(it["meta_aad_utf8"] == f"yc1|meta|{item_id}", "meta aad")
        meta_plain = seal_open(key, meta_sealed, it["meta_aad_utf8"].encode())
        ok(meta_plain.decode() == it["meta_plaintext_utf8"], "meta plaintext " + it["name"])
        meta = json.loads(meta_plain)
        ok(meta == it["meta"], "meta json " + it["name"])
        ok(meta["v"] == 1, "meta version")
        if it["chunk_count"] == 0:
            content = bytes.fromhex(it["content_hex"])
            payload = base64.b64decode(it["payload_sealed_b64"], validate=True)
            ok(it["payload_aad_utf8"] == f"yc1|payload|{item_id}", "payload aad")
            ok(seal_open(key, payload, it["payload_aad_utf8"].encode()) == content, "payload " + it["name"])
            ok(len(payload) == it["size"] + 28, "payload length")
        else:
            content = large_archive
            ok(it["chunk_count"] == -(-len(content) // CHUNK), "chunk count")
            for chunk in it["chunks"]:
                n = chunk["index"]
                plain = content[n * CHUNK:(n + 1) * CHUNK]
                ok(chunk["aad_utf8"] == aad_chunk(item_id, n, it["chunk_count"]).decode(), "chunk aad")
                ok(len(plain) == chunk["plaintext_size"], "chunk plain size")
                ok(hashlib.sha256(plain).hexdigest() == chunk["plaintext_sha256"], "chunk plain sha")
                sealed = seal(key, bytes.fromhex(chunk["nonce_hex"]), chunk["aad_utf8"].encode(), plain)
                ok(len(sealed) == chunk["sealed_size"], "chunk sealed size")
                ok(hashlib.sha256(sealed).hexdigest() == chunk["sealed_sha256"], "chunk sealed sha")
                ok(sealed[:44].hex() == chunk["sealed_prefix_hex"], "chunk prefix")
                ok(sealed[-32:].hex() == chunk["sealed_suffix_hex"], "chunk suffix")
                ok(seal_open(key, sealed, chunk["aad_utf8"].encode()) == plain, "chunk open")
        ok(len(content) == it["size"], "size " + it["name"])
        ok(hashlib.sha256(content).hexdigest() == it["content_sha256"] == meta["sha256"], "sha256 " + it["name"])
        ok(content_hash(key, content) == it["content_hash"], "content hash " + it["name"])
        if it["kind"] == "text":
            ok(meta["preview"] == content.decode()[:500], "text preview")
        if it["kind"] == "image":
            ok(content[:8] == b"\x89PNG\r\n\x1a\n", "png signature")
            thumb = base64.b64decode(it["thumb_sealed_b64"], validate=True)
            ok(seal_open(key, thumb, it["thumb_aad_utf8"].encode()).hex() == it["thumb_plaintext_hex"], "thumb")
            ok(bytes.fromhex(it["thumb_plaintext_hex"])[:2] == b"\xff\xd8", "jpeg signature")
        if it["kind"] == "files":
            files = unpack(content)
            ok([{"name": n, "size": len(d)} for n, d in files] == meta["files"], "files meta")
        h = it["header"]
        ok(h["id"] == item_id and h["kind"] == it["kind"] and h["size"] == it["size"], "header basics")
        ok(h["chunk_count"] == it["chunk_count"] and h["content_hash"] == it["content_hash"], "header hash")
        ok(h["meta"] == it["meta_sealed_b64"], "header meta")
        stored = len(meta_sealed)
        if it["chunk_count"] == 0:
            ok(h["payload"] == it["payload_sealed_b64"], "header payload")
            stored += len(base64.b64decode(h["payload"]))
        else:
            ok("payload" not in h, "chunked header has no payload")
            stored += sum(c["sealed_size"] for c in it["chunks"])
        if h["has_thumb"]:
            stored += len(base64.b64decode(it["thumb_sealed_b64"]))
        ok(stored == h["stored_bytes"] == it["stored_bytes"], "stored bytes " + it["name"])
        headers[item_id] = h
    return headers


def walk_items(node, found):
    if isinstance(node, dict):
        if "meta" in node and "id" in node and isinstance(node["meta"], str):
            found.append(node)
        for value in node.values():
            walk_items(value, found)
    elif isinstance(node, list):
        for value in node:
            walk_items(value, found)


def verify_messages(key, headers):
    found = []
    walk_items([e for e in load("http.json")["examples"] if e["status"] < 400], found)
    walk_items(load("ws.json"), found)
    ok(len(found) > 10, "message examples contain item headers")
    for node in found:
        item_id = node["id"]
        seal_open(key, base64.b64decode(node["meta"], validate=True), f"yc1|meta|{item_id}".encode())
        if "payload" in node:
            payload = base64.b64decode(node["payload"], validate=True)
            ok(len(payload) == node["size"] + 28, "example payload length")
            seal_open(key, payload, f"yc1|payload|{item_id}".encode())
        ok(item_id in headers, "example refers to a known item")
        ok(node["content_hash"] == headers[item_id]["content_hash"], "example content hash")
    for msg in load("ws.json")["messages"]:
        ok(isinstance(msg["message"].get("type"), str), "ws type " + msg["name"])


HTTP_ERRORS = {
    "invalid_request": 400, "invalid_id": 400, "size_mismatch": 400,
    "invalid_credentials": 401, "unauthorized": 401, "not_found": 404,
    "method_not_allowed": 405, "id_conflict": 409, "already_committed": 409,
    "missing_chunks": 409, "key_check_missing": 409, "key_check_exists": 409,
    "items_exist": 409, "pinned_limit": 409, "body_too_large": 413,
    "item_too_large": 413, "rate_limited": 429, "internal": 500, "disk_low": 507,
}
WS_ERRORS = {"protocol_version_unsupported", "device_mismatch", "hello_required", "invalid_message", "unknown_type"}


def verify_contracts(headers):
    examples = {e["name"]: e for e in load("http.json")["examples"]}
    for e in examples.values():
        if e["status"] >= 400:
            body = e["response_body"]
            ok(HTTP_ERRORS.get(body["code"]) == e["status"], "error code status " + e["name"])
            ok(isinstance(body["message"], str), "error message " + e["name"])
    storage = examples["storage"]["response_body"]
    ok(storage["used_bytes"] == sum(h["stored_bytes"] for h in headers.values()), "storage used bytes")
    ok(storage["pinned_bytes"] == sum(h["stored_bytes"] for h in headers.values() if h["pinned"]), "storage pinned bytes")
    ok(storage["item_count"] == len(headers), "storage item count")
    limits = examples["me"]["response_body"]["limits"]
    chunking = load("chunking.json")
    ok(limits["inline_max_bytes"] == chunking["inline_max_bytes"] == INLINE_MAX, "limits inline")
    ok(limits["chunk_size_bytes"] == chunking["chunk_size_bytes"] == CHUNK, "limits chunk")
    ok(limits["max_chunk_body_bytes"] == CHUNK + chunking["seal_overhead_bytes"], "limits chunk body")
    index = examples["history_index"]["response_body"]
    ok([i["seq"] for i in index["items"]] == sorted(h["seq"] for h in headers.values()), "index ascending")
    ok(index["current_seq"] == max(h["seq"] for h in headers.values()), "index current seq")
    newest = [h["seq"] for h in examples["history_newest"]["response_body"]["items"]]
    ok(newest == sorted(newest, reverse=True), "history newest descending")
    after = [h["seq"] for h in examples["history_after_catch_up"]["response_body"]["items"]]
    ok(after == sorted(after) and all(s > 40 for s in after), "history after ascending")
    for h in load("http.json")["examples"]:
        body = h.get("response_body")
        if isinstance(body, dict) and "items" in body:
            for item in body["items"]:
                ok("payload" not in item, "history has no payload " + h["name"])
    ws = load("ws.json")
    welcome = next(m["message"] for m in ws["messages"] if m["name"] == "welcome")
    ok(welcome["protocol_version"] == 1 and welcome["current_seq"] == index["current_seq"], "welcome seq")
    rev = welcome["state_rev"]
    for m in ws["messages"]:
        msg = m["message"]
        if msg["type"] == "error":
            ok(msg["code"] in WS_ERRORS, "ws error code " + m["name"])
        if msg["type"] in ("clip_deleted", "clip_pinned"):
            ok(msg["state_rev"] == rev + 1, "state_rev increments " + m["name"])
            rev = msg["state_rev"]
        if msg["type"] == "clip_deleted":
            ok(1 <= len(msg["ids"]) <= 500 and msg["reason"] in ("user", "retention", "dedupe"), "clip_deleted " + m["name"])
    codes = {c["code"] for c in ws["close_codes"]}
    ok({1000, 1001, 4001, 4002, 4003, 4004, 4005} <= codes, "close codes")


def verify_no_em_dash():
    for path in sorted(VECTORS.glob("*.json")):
        ok("\u2014" not in path.read_text(encoding="utf-8"), "no em dash in " + path.name)


def main():
    kdf_keys = verify_kdf()
    verify_keys(kdf_keys)
    verify_aead()
    verify_uuid()
    large = verify_archive()
    verify_chunking()
    verify_preview()
    verify_token()
    headers = verify_items(kdf_keys["primary"], large)
    verify_messages(kdf_keys["primary"], headers)
    verify_contracts(headers)
    verify_no_em_dash()
    print(f"OK: {checks} checks passed against {VECTORS}")


main()
