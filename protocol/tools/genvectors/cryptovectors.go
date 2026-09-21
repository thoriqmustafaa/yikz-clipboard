package main

import (
	"bytes"
	"encoding/binary"
	"strings"
	"unicode/utf16"
	"unicode/utf8"
)

func kdfVectors() obj {
	unicodeSalt := mustHex("00112233445566778899aabbccddeeff")
	nfd := "pa\u0308sswo\u0308rd \U0001f510 \u65e5\u672c\u8a9e"
	nfc := "p\u00e4ssw\u00f6rd \U0001f510 \u65e5\u672c\u8a9e"
	cases := []obj{
		{
			{"name", "primary"},
			{"password_input", testPassword},
			{"password_normalized", testPassword},
			{"password_utf8_hex", hexs([]byte(testPassword))},
			{"salt_b64", b64(testSalt)},
			{"salt_hex", hexs(testSalt)},
			{"iterations", kdfIterations},
			{"key_hex", hexs(deriveKey(testPassword, testSalt, kdfIterations))},
		},
		{
			{"name", "fast"},
			{"password_input", testPassword},
			{"password_normalized", testPassword},
			{"password_utf8_hex", hexs([]byte(testPassword))},
			{"salt_b64", b64(testSalt)},
			{"salt_hex", hexs(testSalt)},
			{"iterations", fastKDFIterations},
			{"key_hex", hexs(deriveKey(testPassword, testSalt, fastKDFIterations))},
		},
		{
			{"name", "unicode_nfd_input"},
			{"password_input", nfd},
			{"password_normalized", nfc},
			{"password_utf8_hex", hexs([]byte(nfc))},
			{"salt_b64", b64(unicodeSalt)},
			{"salt_hex", hexs(unicodeSalt)},
			{"iterations", fastKDFIterations},
			{"key_hex", hexs(deriveKey(nfc, unicodeSalt, fastKDFIterations))},
		},
	}
	return obj{
		{"description", "PBKDF2-HMAC-SHA256. password_input is what the user typed; implementations MUST normalize it to Unicode NFC (password_normalized) and hash its UTF-8 bytes (password_utf8_hex). The protocol always uses 600000 iterations; the 1000-iteration cases exist only for fast unit tests."},
		{"algorithm", "pbkdf2-sha256"},
		{"key_length", keyLength},
		{"cases", cases},
	}
}

func keyVectors(f *fixture) obj {
	fastKey := deriveKey(testPassword, testSalt, fastKDFIterations)
	samples := []obj{}
	for _, s := range []string{"Hello from yikz-clipboard \U0001f44b\nSecond line.", "a", "https://yikz.dev/"} {
		samples = append(samples, obj{
			{"content_utf8", s},
			{"content_hex", hexs([]byte(s))},
			{"content_hash", contentHash(f.Key, []byte(s))},
			{"sha256", sha256Hex([]byte(s))},
		})
	}
	return obj{
		{"description", "Subkeys derived from the 32-byte master key K. content_hash_key = HMAC-SHA256(K, UTF-8 \"yikz-clipboard/v1/content-hash\"). key_check = lowercase hex of HMAC-SHA256(K, UTF-8 \"yikz-clipboard/v1/key-check\"). content_hash = lowercase hex of HMAC-SHA256(content_hash_key, content plaintext bytes)."},
		{"content_hash_label_utf8", string(contentHashLabel)},
		{"key_check_label_utf8", string(keyCheckLabel)},
		{"keys", []obj{
			{
				{"name", "primary"},
				{"key_hex", hexs(f.Key)},
				{"content_hash_key_hex", hexs(contentHashKey(f.Key))},
				{"key_check", f.KeyCheck},
				{"content_hash_samples", samples},
			},
			{
				{"name", "fast"},
				{"key_hex", hexs(fastKey)},
				{"content_hash_key_hex", hexs(contentHashKey(fastKey))},
				{"key_check", keyCheck(fastKey)},
				{"content_hash_samples", []obj{{
					{"content_utf8", "a"},
					{"content_hex", "61"},
					{"content_hash", contentHash(fastKey, []byte("a"))},
					{"sha256", sha256Hex([]byte("a"))},
				}}},
			},
		}},
	}
}

func aeadCase(name, purpose string, key, nonce, aad, plaintext []byte, textual bool) obj {
	sealed := seal(key, nonce, aad, plaintext)
	c := obj{
		{"name", name},
		{"purpose", purpose},
		{"key_hex", hexs(key)},
		{"nonce_hex", hexs(nonce)},
		{"aad_utf8", string(aad)},
		{"aad_hex", hexs(aad)},
		{"plaintext_hex", hexs(plaintext)},
	}
	if textual && utf8.Valid(plaintext) {
		c = append(c, kv{"plaintext_utf8", string(plaintext)})
	}
	c = append(c,
		kv{"sealed_hex", hexs(sealed)},
		kv{"sealed_b64", b64(sealed)},
		kv{"sealed_length", len(sealed)},
	)
	return c
}

func negativeCase(name, reason string, key, sealed, aad []byte) obj {
	if _, err := open(key, sealed, aad); err == nil {
		panic("negative vector unexpectedly opened: " + name)
	}
	return obj{
		{"name", name},
		{"reason", reason},
		{"key_hex", hexs(key)},
		{"aad_utf8", string(aad)},
		{"aad_hex", hexs(aad)},
		{"sealed_hex", hexs(sealed)},
		{"sealed_b64", b64(sealed)},
		{"expect", "decrypt_error"},
	}
}

func aeadVectors(f *fixture) obj {
	fastKey := deriveKey(testPassword, testSalt, fastKDFIterations)
	chunkPlain := []byte("chunk plaintext example; real chunks are 4194304 bytes except the last")
	chunkNonce := testNonce("aead|chunk")
	chunkAAD := aadChunk(f.Text.ID, 1, 3)
	chunkSealed := seal(f.Key, chunkNonce, chunkAAD, chunkPlain)

	positive := []obj{
		aeadCase("payload_text", "payload", f.Key, f.Text.PayloadNonce, aadPayload(f.Text.ID), f.Text.Content, true),
		aeadCase("meta_text", "meta", f.Key, f.Text.MetaNonce, aadMeta(f.Text.ID), f.Text.MetaJSON, true),
		aeadCase("meta_image", "meta", f.Key, f.Image.MetaNonce, aadMeta(f.Image.ID), f.Image.MetaJSON, true),
		aeadCase("payload_image", "payload", f.Key, f.Image.PayloadNonce, aadPayload(f.Image.ID), f.Image.Content, false),
		aeadCase("thumb_image", "thumb", f.Key, f.Image.ThumbNonce, aadThumb(f.Image.ID), f.Image.ThumbPlain, false),
		aeadCase("payload_files", "payload", f.Key, f.Files.PayloadNonce, aadPayload(f.Files.ID), f.Files.Content, false),
		aeadCase("chunk_1_of_3", "chunk", f.Key, chunkNonce, chunkAAD, chunkPlain, true),
		aeadCase("chunk_0_of_1_fast_key", "chunk", fastKey, testNonce("aead|fastchunk"), aadChunk(f.Large.ID, 0, 1), []byte("x"), true),
		aeadCase("payload_text_fast_key", "payload", fastKey, testNonce("aead|fast"), aadPayload(f.Text.ID), f.Text.Content, true),
	}

	tampered := append([]byte{}, f.Text.Payload...)
	tampered[len(tampered)-1] ^= 0x01
	flippedCipher := append([]byte{}, f.Text.Payload...)
	flippedCipher[nonceLength] ^= 0x80
	wrongKey := append([]byte{}, f.Key...)
	wrongKey[0] ^= 0x01

	negative := []obj{
		negativeCase("tampered_tag", "last tag byte flipped", f.Key, tampered, aadPayload(f.Text.ID)),
		negativeCase("tampered_ciphertext", "first ciphertext byte flipped", f.Key, flippedCipher, aadPayload(f.Text.ID)),
		negativeCase("chunk_index_swapped", "chunk 1 of 3 opened with the AAD of chunk 0 of 3", f.Key, chunkSealed, aadChunk(f.Text.ID, 0, 3)),
		negativeCase("chunk_count_changed", "chunk 1 of 3 opened with the AAD of chunk 1 of 2 (truncation)", f.Key, chunkSealed, aadChunk(f.Text.ID, 1, 2)),
		negativeCase("meta_moved_to_other_item", "meta of the text item opened with the AAD of the image item", f.Key, f.Text.MetaSealed, aadMeta(f.Image.ID)),
		negativeCase("purpose_confusion", "payload opened with the meta AAD of the same item", f.Key, f.Text.Payload, aadMeta(f.Text.ID)),
		negativeCase("wrong_key", "first key byte flipped", wrongKey, f.Text.Payload, aadPayload(f.Text.ID)),
		negativeCase("too_short", "sealed box shorter than 28 bytes", f.Key, f.Text.Payload[:27], aadPayload(f.Text.ID)),
	}

	return obj{
		{"description", "AES-256-GCM sealed boxes. sealed = nonce (12) || ciphertext || tag (16). JSON fields carry sealed as standard base64 with padding. Nonces here are fixed so outputs are deterministic; real implementations MUST use 12 fresh random bytes for every seal."},
		{"aad_formats", obj{
			{"meta", "yc1|meta|<item_id>"},
			{"payload", "yc1|payload|<item_id>"},
			{"thumb", "yc1|thumb|<item_id>"},
			{"chunk", "yc1|chunk|<item_id>|<index>|<chunk_count>"},
		}},
		{"positive", positive},
		{"negative", negative},
	}
}

func uuidVectors() obj {
	gen := []obj{}
	for _, c := range []struct {
		label string
		ms    int64
	}{
		{"a", baseTime.UnixMilli()},
		{"b", baseTime.UnixMilli() + 1},
		{"c", 1},
	} {
		r := testBytes("uuidvec|"+c.label, 10)
		gen = append(gen, obj{
			{"unix_ms", c.ms},
			{"random_hex", hexs(r)},
			{"expected", uuidV7(c.ms, r)},
		})
	}
	valid := []string{gen[0][2].V.(string), gen[1][2].V.(string), "01926f3c-8d2a-7b3e-9f10-0123456789ab"}
	invalid := []obj{
		{{"value", strings.ToUpper(valid[0])}, {"reason", "uppercase hex"}},
		{{"value", "01926f3c-8d2a-4b3e-9f10-0123456789ab"}, {"reason", "version 4"}},
		{{"value", "01926f3c-8d2a-7b3e-cf10-0123456789ab"}, {"reason", "variant bits not 10"}},
		{{"value", "01926f3c8d2a7b3e9f100123456789ab"}, {"reason", "missing hyphens"}},
		{{"value", "{01926f3c-8d2a-7b3e-9f10-0123456789ab}"}, {"reason", "braces"}},
		{{"value", "01926f3c-8d2a-7b3e-9f10-0123456789a"}, {"reason", "too short"}},
		{{"value", ""}, {"reason", "empty"}},
	}
	return obj{
		{"description", "UUIDv7 (RFC 9562). bytes 0..5 = unix_ms as 48-bit big-endian; bytes 6..15 = random_hex, then byte6 = 0x70 | (byte6 & 0x0f) and byte8 = 0x80 | (byte8 & 0x3f). Rendered as lowercase 8-4-4-4-12 hex with hyphens."},
		{"regex", "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"},
		{"generate", gen},
		{"valid", valid},
		{"invalid", invalid},
	}
}

func archiveVectors(f *fixture) obj {
	var files []obj
	for _, fl := range f.FilesList {
		files = append(files, obj{
			{"name", fl.Name},
			{"name_utf8_hex", hexs([]byte(fl.Name))},
			{"size", len(fl.Data)},
			{"data_hex", hexs(fl.Data)},
		})
	}
	archive := packFiles(f.FilesList)
	var single bytes.Buffer
	single.WriteString(archiveMagic)
	var u32 [4]byte
	var u64 [8]byte
	binary.BigEndian.PutUint32(u32[:], 1)
	single.Write(u32[:])
	binary.BigEndian.PutUint32(u32[:], uint32(len(f.LargeFiles[0].Name)))
	single.Write(u32[:])
	single.WriteString(f.LargeFiles[0].Name)
	binary.BigEndian.PutUint64(u64[:], uint64(len(f.LargeFiles[0].Data)))
	single.Write(u64[:])
	return obj{
		{"description", "YCF1 files archive: magic \"YCF1\" (59 43 46 31), u32be file_count, then per file: u32be name_length, name (UTF-8, NFC), u64be data_length, data. No padding, no trailer."},
		{"cases", []obj{
			{
				{"name", "three_files"},
				{"files", files},
				{"archive_hex", hexs(archive)},
				{"archive_size", len(archive)},
				{"archive_sha256", sha256Hex(archive)},
			},
			{
				{"name", "single_large_file_header"},
				{"files", []obj{{{"name", f.LargeFiles[0].Name}, {"size", len(f.LargeFiles[0].Data)}, {"data_rule", "byte i = i mod 251"}}}},
				{"archive_prefix_hex", hexs(single.Bytes())},
				{"archive_size", archiveSize(f.LargeFiles)},
				{"archive_sha256", sha256Hex(packFiles(f.LargeFiles))},
			},
		}},
		{"invalid_names", []obj{
			{{"name", ""}, {"reason", "empty"}},
			{{"name", "."}, {"reason", "dot"}},
			{{"name", ".."}, {"reason", "dot dot"}},
			{{"name", "a/b.txt"}, {"reason", "contains slash"}},
			{{"name", "a\\b.txt"}, {"reason", "contains backslash"}},
			{{"name", "a\x00b"}, {"reason", "contains NUL"}},
			{{"name", "line\nbreak"}, {"reason", "contains control character"}},
			{{"name", strings.Repeat("x", 256)}, {"reason", "longer than 255 UTF-8 bytes"}},
		}},
	}
}

func chunkingVectors() obj {
	var cases []obj
	for _, size := range []int64{1, 262143, 262144, 262145, 4194304, 4194305, 8388608, 8389652, 10485760, 5368709120} {
		count := chunkCount(size)
		c := obj{
			{"size", size},
			{"inline", count == 0},
			{"chunk_count", count},
		}
		if count == 0 {
			c = append(c, kv{"payload_sealed_length", size + sealOverhead})
		} else {
			last := chunkPlainSize(size, count-1, count)
			c = append(c,
				kv{"full_chunk_plaintext_size", int64(chunkSizeBytes)},
				kv{"full_chunk_sealed_length", int64(chunkSizeBytes + sealOverhead)},
				kv{"last_chunk_index", count - 1},
				kv{"last_chunk_plaintext_size", last},
				kv{"last_chunk_sealed_length", last + sealOverhead},
				kv{"total_chunk_sealed_bytes", size + int64(count)*sealOverhead},
			)
		}
		cases = append(cases, c)
	}
	return obj{
		{"description", "Inline if size <= 262144, otherwise chunk_count = ceil(size / 4194304). Every chunk except the last has exactly 4194304 plaintext bytes. Sealed length = plaintext length + 28."},
		{"inline_max_bytes", inlineMaxBytes},
		{"chunk_size_bytes", chunkSizeBytes},
		{"seal_overhead_bytes", sealOverhead},
		{"cases", cases},
	}
}

func previewVectors() obj {
	inputs := []struct{ name, s string }{
		{"short", "Hello"},
		{"exactly_500_ascii", strings.Repeat("a", 500)},
		{"ascii_1000", strings.Repeat("0123456789", 100)},
		{"emoji_at_boundary", strings.Repeat("a", 499) + "\U0001f600bcd"},
		{"emoji_after_boundary", strings.Repeat("a", 500) + "\U0001f600"},
		{"precomposed_600", strings.Repeat("\u00e9", 600)},
		{"combining_marks", strings.Repeat("e\u0301", 300)},
		{"multiline", "line one\nline two\r\nline three\t tab"},
	}
	var cases []obj
	for _, in := range inputs {
		out := preview(in.s)
		cases = append(cases, obj{
			{"name", in.name},
			{"input", in.s},
			{"input_code_points", utf8.RuneCountInString(in.s)},
			{"input_utf16_units", len(utf16.Encode([]rune(in.s)))},
			{"expected", out},
			{"expected_code_points", utf8.RuneCountInString(out)},
		})
	}
	return obj{
		{"description", "preview = the first 500 Unicode code points (scalar values) of the text, with no trimming or whitespace normalization. Never split a surrogate pair. Combining marks count as separate code points."},
		{"max_code_points", previewMaxPoints},
		{"cases", cases},
	}
}

func tokenVectors(f *fixture) obj {
	t2, h2 := makeToken("android")
	return obj{
		{"description", "token = \"yc_\" + base64url without padding of 32 random bytes (46 ASCII characters total). The server stores only token_hash = lowercase hex SHA-256 of the token's ASCII bytes."},
		{"regex", "^yc_[A-Za-z0-9_-]{43}$"},
		{"cases", []obj{
			{{"random_hex", hexs(testBytes("token|mac", 32))}, {"token", f.Token}, {"token_hash", f.TokenHash}},
			{{"random_hex", hexs(testBytes("token|android", 32))}, {"token", t2}, {"token_hash", h2}},
		}},
	}
}
