package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"image"
	"image/color"
	"image/jpeg"
	"image/png"
	"strconv"
	"time"
	"unicode/utf8"
)

const (
	testPassword      = "correct horse battery staple"
	testLoginPassword = "login-password-example"
	testUsername      = "thoriq"
	serverVersion     = "1.0.0"
)

var (
	baseTime = time.Date(2026, 9, 21, 10, 0, 0, 0, time.UTC)
	testSalt = mustHex("5c1e8f2a9b3d47e6a0c4f81d2e6b9a73")
)

func mustHex(s string) []byte {
	b, err := hex.DecodeString(s)
	must(err)
	return b
}

func rfc3339ms(t time.Time) string {
	return t.UTC().Format("2006-01-02T15:04:05.000Z")
}

type device struct {
	ID         string
	Name       string
	Platform   string
	CreatedAt  time.Time
	LastSeenAt time.Time
	Online     bool
	Revoked    bool
}

func (d device) json(current bool) obj {
	return obj{
		{"id", d.ID},
		{"name", d.Name},
		{"platform", d.Platform},
		{"created_at", rfc3339ms(d.CreatedAt)},
		{"last_seen_at", rfc3339ms(d.LastSeenAt)},
		{"online", d.Online},
		{"revoked", d.Revoked},
		{"current", current},
	}
}

func (d device) presence() obj {
	return obj{
		{"device_id", d.ID},
		{"name", d.Name},
		{"platform", d.Platform},
	}
}

type item struct {
	Label        string
	ID           string
	Seq          int64
	DeviceID     string
	Kind         string
	CreatedAt    time.Time
	Pinned       bool
	Content      []byte
	Meta         obj
	MetaJSON     []byte
	MetaNonce    []byte
	MetaSealed   []byte
	PayloadNonce []byte
	Payload      []byte
	ThumbPlain   []byte
	ThumbNonce   []byte
	Thumb        []byte
	ChunkCount   int
	ChunkNonces  [][]byte
	Chunks       [][]byte
	ContentHash  string
}

func (it *item) size() int64 {
	return int64(len(it.Content))
}

func (it *item) storedBytes() int64 {
	total := int64(len(it.MetaSealed)) + int64(len(it.Payload)) + int64(len(it.Thumb))
	for _, c := range it.Chunks {
		total += int64(len(c))
	}
	return total
}

func (it *item) header(includePayload bool) obj {
	h := obj{
		{"id", it.ID},
		{"seq", it.Seq},
		{"device_id", it.DeviceID},
		{"kind", it.Kind},
		{"size", it.size()},
		{"chunk_count", it.ChunkCount},
		{"created_at", rfc3339ms(it.CreatedAt)},
		{"pinned", it.Pinned},
		{"content_hash", it.ContentHash},
		{"has_thumb", it.Thumb != nil},
		{"stored_bytes", it.storedBytes()},
		{"meta", b64(it.MetaSealed)},
	}
	if includePayload && it.Payload != nil {
		h = append(h, kv{"payload", b64(it.Payload)})
	}
	return h
}

func (it *item) createRequest() obj {
	r := obj{
		{"id", it.ID},
		{"kind", it.Kind},
		{"size", it.size()},
		{"chunk_count", it.ChunkCount},
		{"content_hash", it.ContentHash},
		{"meta", b64(it.MetaSealed)},
	}
	if it.Payload != nil {
		r = append(r, kv{"payload", b64(it.Payload)})
	}
	return r
}

func (it *item) commitRequest() obj {
	return obj{
		{"kind", it.Kind},
		{"size", it.size()},
		{"chunk_count", it.ChunkCount},
		{"content_hash", it.ContentHash},
		{"meta", b64(it.MetaSealed)},
	}
}

type fixture struct {
	Key        []byte
	KeyCheck   string
	ServerID   string
	Token      string
	TokenHash  string
	Mac        device
	Android    device
	Windows    device
	Web        device
	Text       *item
	Image      *item
	Files      *item
	Large      *item
	FilesList  []archiveFile
	LargeFiles []archiveFile
	ServerTime time.Time
	CurrentSeq int64
	StateRev   int64
}

func idAt(t time.Time, label string) string {
	return uuidV7(t.UnixMilli(), testBytes("uuid|"+label, 10))
}

func makeToken(label string) (string, string) {
	token := "yc_" + base64.RawURLEncoding.EncodeToString(testBytes("token|"+label, 32))
	sum := sha256.Sum256([]byte(token))
	return token, hex.EncodeToString(sum[:])
}

func testImage() *image.NRGBA {
	img := image.NewNRGBA(image.Rect(0, 0, 4, 3))
	for y := 0; y < 3; y++ {
		for x := 0; x < 4; x++ {
			img.Set(x, y, color.NRGBA{R: uint8(x * 60), G: uint8(y * 100), B: uint8(200 - x*40), A: 255})
		}
	}
	return img
}

func encodePNG(img image.Image) []byte {
	var buf bytes.Buffer
	enc := png.Encoder{CompressionLevel: png.DefaultCompression}
	must(enc.Encode(&buf, img))
	return buf.Bytes()
}

func encodeJPEG(img image.Image) []byte {
	var buf bytes.Buffer
	must(jpeg.Encode(&buf, img, &jpeg.Options{Quality: 80}))
	return buf.Bytes()
}

func patternBytes(n int) []byte {
	out := make([]byte, n)
	for i := range out {
		out[i] = byte(i % 251)
	}
	return out
}

func finishItem(key []byte, it *item) {
	it.ContentHash = contentHash(key, it.Content)
	metaJSON, err := marshalCompact(it.Meta)
	must(err)
	it.MetaJSON = metaJSON
	it.MetaNonce = testNonce(it.Label + "|meta")
	it.MetaSealed = seal(key, it.MetaNonce, aadMeta(it.ID), it.MetaJSON)
	it.ChunkCount = chunkCount(it.size())
	if it.ChunkCount == 0 {
		it.PayloadNonce = testNonce(it.Label + "|payload")
		it.Payload = seal(key, it.PayloadNonce, aadPayload(it.ID), it.Content)
	} else {
		for n := 0; n < it.ChunkCount; n++ {
			start := int64(n) * chunkSizeBytes
			end := start + chunkPlainSize(it.size(), n, it.ChunkCount)
			nonce := testNonce(it.Label + "|chunk|" + strconv.Itoa(n))
			it.ChunkNonces = append(it.ChunkNonces, nonce)
			it.Chunks = append(it.Chunks, seal(key, nonce, aadChunk(it.ID, n, it.ChunkCount), it.Content[start:end]))
		}
	}
	if it.ThumbPlain != nil {
		it.ThumbNonce = testNonce(it.Label + "|thumb")
		it.Thumb = seal(key, it.ThumbNonce, aadThumb(it.ID), it.ThumbPlain)
	}
}

func filesMeta(files []archiveFile, archive []byte) obj {
	var list []obj
	names := ""
	for i, f := range files {
		list = append(list, obj{{"name", f.Name}, {"size", len(f.Data)}})
		if i > 0 {
			names += "\n"
		}
		names += f.Name
	}
	return obj{
		{"v", 1},
		{"mime", "application/x-yikz-files"},
		{"preview", preview(names)},
		{"sha256", sha256Hex(archive)},
		{"files", list},
	}
}

func buildFixture() *fixture {
	f := &fixture{}
	f.Key = deriveKey(testPassword, testSalt, kdfIterations)
	f.KeyCheck = keyCheck(f.Key)
	f.ServerID = idAt(time.Date(2026, 9, 1, 8, 0, 0, 0, time.UTC), "server")
	f.Token, f.TokenHash = makeToken("mac")
	f.ServerTime = baseTime.Add(10 * time.Minute)

	f.Mac = device{ID: idAt(time.Date(2026, 9, 1, 8, 5, 0, 0, time.UTC), "mac"), Name: "Thoriq MacBook Pro", Platform: "macos", CreatedAt: time.Date(2026, 9, 1, 8, 5, 0, 0, time.UTC), LastSeenAt: f.ServerTime, Online: true}
	f.Android = device{ID: idAt(time.Date(2026, 9, 1, 8, 10, 0, 0, time.UTC), "android"), Name: "Pixel 9", Platform: "android", CreatedAt: time.Date(2026, 9, 1, 8, 10, 0, 0, time.UTC), LastSeenAt: f.ServerTime, Online: true}
	f.Windows = device{ID: idAt(time.Date(2026, 9, 2, 9, 0, 0, 0, time.UTC), "windows"), Name: "Desktop PC", Platform: "windows", CreatedAt: time.Date(2026, 9, 2, 9, 0, 0, 0, time.UTC), LastSeenAt: baseTime.Add(4 * time.Minute), Online: false}
	f.Web = device{ID: idAt(time.Date(2026, 9, 3, 12, 0, 0, 0, time.UTC), "web"), Name: "Firefox on Linux", Platform: "web", CreatedAt: time.Date(2026, 9, 3, 12, 0, 0, 0, time.UTC), LastSeenAt: baseTime.Add(-48 * time.Hour), Online: false, Revoked: true}

	text := "Hello from yikz-clipboard \U0001f44b\nSecond line."
	f.Text = &item{Label: "text", Seq: 41, DeviceID: f.Mac.ID, Kind: "text", CreatedAt: baseTime.Add(1*time.Second + 250*time.Millisecond), Content: []byte(text)}
	f.Text.ID = idAt(f.Text.CreatedAt, "item-text")
	f.Text.Meta = obj{
		{"v", 1},
		{"mime", "text/plain; charset=utf-8"},
		{"preview", preview(text)},
		{"sha256", sha256Hex(f.Text.Content)},
		{"source_app", "Safari"},
	}
	finishItem(f.Key, f.Text)

	img := testImage()
	pngBytes := encodePNG(img)
	f.Image = &item{Label: "image", Seq: 42, DeviceID: f.Android.ID, Kind: "image", CreatedAt: baseTime.Add(2 * time.Minute), Content: pngBytes, ThumbPlain: encodeJPEG(img)}
	f.Image.ID = idAt(f.Image.CreatedAt, "item-image")
	f.Image.Meta = obj{
		{"v", 1},
		{"mime", "image/png"},
		{"preview", ""},
		{"sha256", sha256Hex(pngBytes)},
		{"image", obj{{"width", img.Bounds().Dx()}, {"height", img.Bounds().Dy()}}},
	}
	finishItem(f.Key, f.Image)

	f.FilesList = []archiveFile{
		{Name: "hello.txt", Data: []byte("Hello, world!\n")},
		{Name: "donn\u00e9es \u00e9.bin", Data: []byte{0, 1, 2, 3, 4, 5, 6, 7, 8, 9}},
		{Name: "empty.txt", Data: []byte{}},
	}
	archive := packFiles(f.FilesList)
	f.Files = &item{Label: "files", Seq: 43, DeviceID: f.Windows.ID, Kind: "files", CreatedAt: baseTime.Add(3 * time.Minute), Content: archive, Pinned: true}
	f.Files.ID = idAt(f.Files.CreatedAt, "item-files")
	f.Files.Meta = filesMeta(f.FilesList, archive).with(kv{"source_app", "Explorer"})
	finishItem(f.Key, f.Files)

	f.LargeFiles = []archiveFile{{Name: "pattern.bin", Data: patternBytes(2*chunkSizeBytes + 1000)}}
	large := packFiles(f.LargeFiles)
	f.Large = &item{Label: "large", Seq: 44, DeviceID: f.Mac.ID, Kind: "files", CreatedAt: baseTime.Add(5 * time.Minute), Content: large}
	f.Large.ID = idAt(f.Large.CreatedAt, "item-large")
	f.Large.Meta = filesMeta(f.LargeFiles, large).with(kv{"source_app", "Finder"})
	finishItem(f.Key, f.Large)

	for _, it := range []*item{f.Text, f.Image, f.Files, f.Large} {
		if !utf8.Valid(it.MetaJSON) {
			panic("meta not utf8")
		}
		back, err := open(f.Key, it.MetaSealed, aadMeta(it.ID))
		must(err)
		if !bytes.Equal(back, it.MetaJSON) {
			panic("meta roundtrip")
		}
	}

	f.CurrentSeq = 44
	f.StateRev = 17
	return f
}
