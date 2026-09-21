package main

import (
	"bytes"
	"crypto/aes"
	"crypto/cipher"
	"crypto/hmac"
	"crypto/pbkdf2"
	"crypto/sha256"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"fmt"
	"strconv"
)

const (
	protocolVersion   = 1
	kdfIterations     = 600000
	fastKDFIterations = 1000
	keyLength         = 32
	nonceLength       = 12
	tagLength         = 16
	sealOverhead      = nonceLength + tagLength
	inlineMaxBytes    = 262144
	chunkSizeBytes    = 4194304
	previewMaxPoints  = 500
	archiveMagic      = "YCF1"
)

var (
	contentHashLabel = []byte("yikz-clipboard/v1/content-hash")
	keyCheckLabel    = []byte("yikz-clipboard/v1/key-check")
)

func must(err error) {
	if err != nil {
		panic(err)
	}
}

func deriveKey(password string, salt []byte, iterations int) []byte {
	key, err := pbkdf2.Key(sha256.New, password, salt, iterations, keyLength)
	must(err)
	return key
}

func hmacSHA256(key, message []byte) []byte {
	mac := hmac.New(sha256.New, key)
	mac.Write(message)
	return mac.Sum(nil)
}

func contentHashKey(key []byte) []byte {
	return hmacSHA256(key, contentHashLabel)
}

func keyCheck(key []byte) string {
	return hex.EncodeToString(hmacSHA256(key, keyCheckLabel))
}

func contentHash(key, content []byte) string {
	return hex.EncodeToString(hmacSHA256(contentHashKey(key), content))
}

func newGCM(key []byte) cipher.AEAD {
	block, err := aes.NewCipher(key)
	must(err)
	gcm, err := cipher.NewGCM(block)
	must(err)
	return gcm
}

func seal(key, nonce, aad, plaintext []byte) []byte {
	if len(nonce) != nonceLength {
		panic("bad nonce length")
	}
	out := append([]byte{}, nonce...)
	return newGCM(key).Seal(out, nonce, plaintext, aad)
}

func open(key, sealed, aad []byte) ([]byte, error) {
	if len(sealed) < sealOverhead {
		return nil, fmt.Errorf("sealed box too short")
	}
	return newGCM(key).Open(nil, sealed[:nonceLength], sealed[nonceLength:], aad)
}

func aadMeta(id string) []byte {
	return []byte("yc1|meta|" + id)
}

func aadPayload(id string) []byte {
	return []byte("yc1|payload|" + id)
}

func aadThumb(id string) []byte {
	return []byte("yc1|thumb|" + id)
}

func aadChunk(id string, index, count int) []byte {
	return []byte("yc1|chunk|" + id + "|" + strconv.Itoa(index) + "|" + strconv.Itoa(count))
}

func testBytes(label string, n int) []byte {
	var out []byte
	for counter := uint32(0); len(out) < n; counter++ {
		var c [4]byte
		binary.BigEndian.PutUint32(c[:], counter)
		sum := sha256.Sum256(append([]byte("yikz-clipboard test bytes|"+label+"|"), c[:]...))
		out = append(out, sum[:]...)
	}
	return out[:n]
}

func testNonce(label string) []byte {
	return testBytes("nonce|"+label, nonceLength)
}

func uuidV7(unixMillis int64, random []byte) string {
	if len(random) != 10 {
		panic("uuidv7 needs 10 random bytes")
	}
	var b [16]byte
	var ts [8]byte
	binary.BigEndian.PutUint64(ts[:], uint64(unixMillis))
	copy(b[0:6], ts[2:8])
	copy(b[6:16], random)
	b[6] = 0x70 | (b[6] & 0x0f)
	b[8] = 0x80 | (b[8] & 0x3f)
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

func chunkCount(size int64) int {
	if size <= inlineMaxBytes {
		return 0
	}
	return int((size + chunkSizeBytes - 1) / chunkSizeBytes)
}

func chunkPlainSize(size int64, index, count int) int64 {
	if index < count-1 {
		return chunkSizeBytes
	}
	return size - int64(count-1)*chunkSizeBytes
}

type archiveFile struct {
	Name string
	Data []byte
}

func packFiles(files []archiveFile) []byte {
	var buf bytes.Buffer
	buf.WriteString(archiveMagic)
	var u32 [4]byte
	var u64 [8]byte
	binary.BigEndian.PutUint32(u32[:], uint32(len(files)))
	buf.Write(u32[:])
	for _, f := range files {
		name := []byte(f.Name)
		binary.BigEndian.PutUint32(u32[:], uint32(len(name)))
		buf.Write(u32[:])
		buf.Write(name)
		binary.BigEndian.PutUint64(u64[:], uint64(len(f.Data)))
		buf.Write(u64[:])
		buf.Write(f.Data)
	}
	return buf.Bytes()
}

func archiveSize(files []archiveFile) int64 {
	total := int64(8)
	for _, f := range files {
		total += 4 + int64(len(f.Name)) + 8 + int64(len(f.Data))
	}
	return total
}

func preview(s string) string {
	runes := []rune(s)
	if len(runes) > previewMaxPoints {
		runes = runes[:previewMaxPoints]
	}
	return string(runes)
}

func b64(b []byte) string {
	return base64.StdEncoding.EncodeToString(b)
}

func hexs(b []byte) string {
	return hex.EncodeToString(b)
}

func sha256Hex(b []byte) string {
	sum := sha256.Sum256(b)
	return hex.EncodeToString(sum[:])
}
