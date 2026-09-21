package release

import (
	"bytes"
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/apierr"
)

type sigVector struct {
	Seed      string `json:"private_key_seed_hex"`
	PublicKey string `json:"public_key_b64"`
	File      string `json:"file_b64"`
	SHA256    string `json:"sha256_hex"`
	Signature string `json:"signature_b64"`
}

func loadSigVector(t *testing.T) sigVector {
	t.Helper()
	data, err := os.ReadFile("../../../protocol/vectors/release_signature.json")
	if err != nil {
		t.Fatal(err)
	}
	var v sigVector
	if err := json.Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	return v
}

func b64(t *testing.T, s string) []byte {
	t.Helper()
	b, err := base64.StdEncoding.DecodeString(s)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func TestEmbeddedPublicKey(t *testing.T) {
	if len(EmbeddedPublicKey()) != ed25519.PublicKeySize {
		t.Fatal("embedded key has wrong size")
	}
}

func TestSignatureVector(t *testing.T) {
	v := loadSigVector(t)
	seed, _ := hex.DecodeString(v.Seed)
	priv := ed25519.NewKeyFromSeed(seed)
	pub := b64(t, v.PublicKey)
	if !bytes.Equal(priv.Public().(ed25519.PublicKey), pub) {
		t.Fatal("public key does not match seed")
	}
	file := b64(t, v.File)
	sum := sha256.Sum256(file)
	if hex.EncodeToString(sum[:]) != v.SHA256 {
		t.Fatal("sha256 mismatch")
	}
	sig := b64(t, v.Signature)
	if !ed25519.Verify(pub, sum[:], sig) {
		t.Fatal("vector signature does not verify")
	}
	if !bytes.Equal(ed25519.Sign(priv, sum[:]), sig) {
		t.Fatal("signing does not reproduce the vector")
	}
	badFile := append([]byte{}, file...)
	badFile[0] ^= 1
	badSum := sha256.Sum256(badFile)
	if ed25519.Verify(pub, badSum[:], sig) {
		t.Fatal("verification passed after changing the file")
	}
	badSig := append([]byte{}, sig...)
	badSig[10] ^= 1
	if ed25519.Verify(pub, sum[:], badSig) {
		t.Fatal("verification passed after changing the signature")
	}
}

func TestPublishWithVectorKey(t *testing.T) {
	v := loadSigVector(t)
	s, err := Open(Options{DataDir: t.TempDir(), PublicKey: ed25519.PublicKey(b64(t, v.PublicKey))})
	if err != nil {
		t.Fatal(err)
	}
	file := b64(t, v.File)
	if err := s.PutAsset("1.0.0", "test.zip", bytes.NewReader(file), int64(len(file))); err != nil {
		t.Fatal(err)
	}
	m := Manifest{Version: "1.0.0", NotesMD: "x", Assets: []Asset{{Platform: "macos", File: "test.zip", Size: int64(len(file)), SHA256: v.SHA256, Signature: v.Signature}}}
	out, err := s.Publish("1.0.0", m)
	if err != nil {
		t.Fatal(err)
	}
	if out.PublishedAt == "" {
		t.Fatal("published_at not filled")
	}
	if _, err := time.Parse(time.RFC3339Nano, out.PublishedAt); err != nil {
		t.Fatal(err)
	}
}

func TestCompareAndValidation(t *testing.T) {
	cases := []struct {
		a, b string
		want int
	}{{"1.2.3", "1.2.3", 0}, {"1.10.0", "1.9.9", 1}, {"0.9.0", "1.0.0", -1}, {"2.0.0", "10.0.0", -1}}
	for _, c := range cases {
		if got := Compare(c.a, c.b); got != c.want {
			t.Errorf("Compare(%s, %s) = %d", c.a, c.b, got)
		}
	}
	for _, v := range []string{"1.0", "v1.0.0", "01.0.0", "1.0.0-beta", "", "../1.0.0"} {
		if ValidVersion(v) {
			t.Errorf("version %q should be invalid", v)
		}
	}
	for _, f := range []string{"..", "a/b", "manifest.json", "", "a b", string(make([]byte, 129))} {
		if ValidFile(f) {
			t.Errorf("file %q should be invalid", f)
		}
	}
	if !ValidFile("YikzClipboard-1.1.0-win-arm64.zip") {
		t.Error("valid file rejected")
	}
}

func TestPutAssetLimits(t *testing.T) {
	s, err := Open(Options{DataDir: t.TempDir()})
	if err != nil {
		t.Fatal(err)
	}
	err = s.PutAsset("1.0.0", "a.zip", bytes.NewReader(nil), MaxAssetBytes+1)
	if e, ok := apierr.As(err); !ok || e.Code != apierr.CodeBodyTooLarge {
		t.Fatalf("expected body_too_large, got %v", err)
	}
	err = s.PutAsset("1.0.0", "a.zip", bytes.NewReader([]byte("abc")), 10)
	if e, ok := apierr.As(err); !ok || e.Code != apierr.CodeInvalidRequest {
		t.Fatalf("expected invalid_request, got %v", err)
	}
	low, _ := Open(Options{DataDir: t.TempDir(), DiskLow: func() bool { return true }})
	err = low.PutAsset("1.0.0", "a.zip", bytes.NewReader([]byte("abc")), 3)
	if e, ok := apierr.As(err); !ok || e.Code != apierr.CodeDiskLow {
		t.Fatalf("expected disk_low, got %v", err)
	}
	entries, _ := os.ReadDir(filepath.Join(s.dir, ".tmp"))
	if len(entries) != 0 {
		t.Fatalf("temp files left behind: %d", len(entries))
	}
}
