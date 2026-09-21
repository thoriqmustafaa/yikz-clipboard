package proto

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"
)

const vectorsDir = "../../../protocol/vectors"

func loadVector(t *testing.T, name string, v any) {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(vectorsDir, name))
	if err != nil {
		t.Fatalf("read vector %s: %v", name, err)
	}
	if err := json.Unmarshal(data, v); err != nil {
		t.Fatalf("parse vector %s: %v", name, err)
	}
}

func TestUUIDv7Vectors(t *testing.T) {
	var v struct {
		Regex    string `json:"regex"`
		Generate []struct {
			UnixMS    int64  `json:"unix_ms"`
			RandomHex string `json:"random_hex"`
			Expected  string `json:"expected"`
		} `json:"generate"`
		Valid   []string `json:"valid"`
		Invalid []struct {
			Value  string `json:"value"`
			Reason string `json:"reason"`
		} `json:"invalid"`
	}
	loadVector(t, "uuidv7.json", &v)
	if v.Regex != itemIDPattern.String() {
		t.Fatalf("regex mismatch: vector %q, server %q", v.Regex, itemIDPattern.String())
	}
	for _, g := range v.Generate {
		rnd, _ := hex.DecodeString(g.RandomHex)
		if got := FormatUUIDv7(g.UnixMS, rnd); got != g.Expected {
			t.Errorf("FormatUUIDv7(%d) = %s, want %s", g.UnixMS, got, g.Expected)
		}
		id, err := NewUUIDv7(time.UnixMilli(g.UnixMS), bytes.NewReader(rnd))
		if err != nil || id != g.Expected {
			t.Errorf("NewUUIDv7 = %s, %v, want %s", id, err, g.Expected)
		}
		if !ValidID(g.Expected) {
			t.Errorf("generated id %s rejected", g.Expected)
		}
	}
	for _, s := range v.Valid {
		if !ValidID(s) {
			t.Errorf("valid id %q rejected", s)
		}
	}
	for _, c := range v.Invalid {
		if ValidID(c.Value) {
			t.Errorf("invalid id %q (%s) accepted", c.Value, c.Reason)
		}
	}
}

func TestTokenVectors(t *testing.T) {
	var v struct {
		Regex string `json:"regex"`
		Cases []struct {
			RandomHex string `json:"random_hex"`
			Token     string `json:"token"`
			TokenHash string `json:"token_hash"`
		} `json:"cases"`
	}
	loadVector(t, "token.json", &v)
	if v.Regex != tokenPattern.String() {
		t.Fatalf("regex mismatch: vector %q, server %q", v.Regex, tokenPattern.String())
	}
	for _, c := range v.Cases {
		rnd, _ := hex.DecodeString(c.RandomHex)
		tok, hash, err := NewToken(bytes.NewReader(rnd))
		if err != nil {
			t.Fatal(err)
		}
		if tok != c.Token || hash != c.TokenHash {
			t.Errorf("NewToken = %s %s, want %s %s", tok, hash, c.Token, c.TokenHash)
		}
		if HashToken(c.Token) != c.TokenHash {
			t.Errorf("HashToken(%s) mismatch", c.Token)
		}
		if !ValidToken(c.Token) || len(c.Token) != 46 {
			t.Errorf("token %s rejected", c.Token)
		}
	}
	for _, bad := range []string{"", "yc_short", "xx_WMmC_xkE6-ZpL_QDaSgkhu0mZPAO1jhw3PEkbkI4Ipw", "yc_WMmC_xkE6-ZpL_QDaSgkhu0mZPAO1jhw3PEkbkI4Ip="} {
		if ValidToken(bad) {
			t.Errorf("bad token %q accepted", bad)
		}
	}
}

func TestChunkingVectors(t *testing.T) {
	var v struct {
		InlineMaxBytes    int64 `json:"inline_max_bytes"`
		ChunkSizeBytes    int64 `json:"chunk_size_bytes"`
		SealOverheadBytes int64 `json:"seal_overhead_bytes"`
		Cases             []struct {
			Size                  int64 `json:"size"`
			Inline                bool  `json:"inline"`
			ChunkCount            int   `json:"chunk_count"`
			PayloadSealedLength   int64 `json:"payload_sealed_length"`
			FullChunkSealedLength int64 `json:"full_chunk_sealed_length"`
			LastChunkIndex        int   `json:"last_chunk_index"`
			LastChunkSealedLength int64 `json:"last_chunk_sealed_length"`
			TotalChunkSealedBytes int64 `json:"total_chunk_sealed_bytes"`
		} `json:"cases"`
	}
	loadVector(t, "chunking.json", &v)
	if v.InlineMaxBytes != InlineMaxBytes || v.ChunkSizeBytes != ChunkSizeBytes || v.SealOverheadBytes != SealOverhead {
		t.Fatalf("constants mismatch")
	}
	for _, c := range v.Cases {
		if got := ChunkCount(c.Size); got != c.ChunkCount {
			t.Errorf("ChunkCount(%d) = %d, want %d", c.Size, got, c.ChunkCount)
		}
		if c.Inline {
			if c.Size+SealOverhead != c.PayloadSealedLength {
				t.Errorf("inline sealed length for %d", c.Size)
			}
			continue
		}
		var total int64
		for i := 0; i < c.ChunkCount; i++ {
			n := ChunkSealedSize(c.Size, i, c.ChunkCount)
			if i < c.ChunkCount-1 && n != c.FullChunkSealedLength {
				t.Errorf("size %d chunk %d = %d, want %d", c.Size, i, n, c.FullChunkSealedLength)
			}
			total += n
		}
		if last := ChunkSealedSize(c.Size, c.LastChunkIndex, c.ChunkCount); last != c.LastChunkSealedLength {
			t.Errorf("size %d last chunk = %d, want %d", c.Size, last, c.LastChunkSealedLength)
		}
		if total != c.TotalChunkSealedBytes {
			t.Errorf("size %d total = %d, want %d", c.Size, total, c.TotalChunkSealedBytes)
		}
		if MaxChunkBodyBytes != c.FullChunkSealedLength {
			t.Errorf("MaxChunkBodyBytes = %d", MaxChunkBodyBytes)
		}
	}
}

func TestTimeFormat(t *testing.T) {
	tm := time.Date(2026, 9, 21, 10, 0, 1, 250_000_000, time.FixedZone("x", 7*3600))
	if got := FormatTime(tm); got != "2026-09-21T03:00:01.250Z" {
		t.Fatalf("FormatTime = %s", got)
	}
	b, _ := json.Marshal(Time{time.Date(2026, 9, 21, 10, 0, 0, 0, time.UTC)})
	if string(b) != `"2026-09-21T10:00:00.000Z"` {
		t.Fatalf("marshal = %s", b)
	}
}

func TestDecodeBase64Strict(t *testing.T) {
	if _, ok := DecodeBase64("AAAA"); !ok {
		t.Fatal("valid rejected")
	}
	for _, bad := range []string{"AAA", "AA\nAA", "AAAA\r\n", "A-_A", "AAB="} {
		if _, ok := DecodeBase64(bad); ok {
			t.Errorf("%q accepted", bad)
		}
	}
}

func TestNormalizeDeviceName(t *testing.T) {
	cases := map[string]bool{
		"  Pixel 9  ":            true,
		"":                       false,
		"   ":                    false,
		"bad\x07name":            false,
		"line\nbreak":            false,
		string(make([]rune, 65)): false,
		"ééé name":               true,
	}
	for in, want := range cases {
		out, ok := NormalizeDeviceName(in)
		if ok != want {
			t.Errorf("NormalizeDeviceName(%q) ok = %v, want %v", in, ok, want)
		}
		if ok && out != "Pixel 9" && in == "  Pixel 9  " {
			t.Errorf("trim failed: %q", out)
		}
	}
	long := ""
	for i := 0; i < 64; i++ {
		long += "\U0001f44b"
	}
	if _, ok := NormalizeDeviceName(long); !ok {
		t.Error("64 code points rejected")
	}
	if _, ok := NormalizeDeviceName(long + "a"); ok {
		t.Error("65 code points accepted")
	}
}
