# protocol

Wire protocol and cross-language test vectors for yikz-clipboard. Every implementation (Go server, Svelte web, Swift macOS, Kotlin Android, C# Windows) MUST follow `SPEC.md` and pass every vector in `vectors/`.

## Contents

| Path | What it is |
|---|---|
| `SPEC.md` | Normative protocol specification (version 1) |
| `vectors/kdf.json` | PBKDF2-HMAC-SHA256 key derivation, including NFC normalization |
| `vectors/keys.json` | Subkeys: content hash key, key check, content hash samples |
| `vectors/aead.json` | AES-256-GCM sealed boxes with AAD, positive and negative cases |
| `vectors/uuidv7.json` | Item id generation and validation |
| `vectors/files_archive.json` | YCF1 files archive format and file name rules |
| `vectors/chunking.json` | Inline threshold, chunk counts and sealed sizes |
| `vectors/preview.json` | 500 code point preview truncation |
| `vectors/token.json` | Device token format and stored hash |
| `vectors/items.json` | Complete encrypted items (text, image with thumbnail, files, chunked) |
| `vectors/http.json` | Canonical HTTP requests and responses for every endpoint |
| `vectors/ws.json` | Canonical WebSocket messages and close codes |
| `tools/genvectors/` | Go program that generates all vector files |
| `tools/verify/verify_vectors.py` | Independent Python verifier |

`SPEC.md` section 16 documents the fields of every vector file.

## Regenerating vectors

Requires Go 1.24 or newer. Output is deterministic, so regenerating without code changes produces identical files.

```sh
cd protocol/tools
go run ./genvectors -out ../vectors
```

Never edit files in `vectors/` by hand. Change the generator and regenerate.

## Verifying vectors

Requires Python 3.9 or newer with the `cryptography` package.

```sh
python3 -m pip install cryptography
python3 protocol/tools/verify/verify_vectors.py
```

It prints `OK: <n> checks passed` and exits 0, or prints the first failing check and exits non-zero. An optional argument points it at another vectors directory.

## Using vectors in platform tests

Load the JSON files directly from `protocol/vectors/` (or copy them into the platform test resources as part of the build). Use the `fast` KDF case (1000 iterations) in tight test loops and the `primary` case at least once. Seal functions under test must accept an explicit nonce so outputs can be compared byte for byte; production code always uses random nonces.
