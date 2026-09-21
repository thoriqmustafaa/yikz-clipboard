package proto

import (
	"crypto/sha256"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"fmt"
	"io"
	"regexp"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"
)

const (
	ProtocolVersion        = 1
	KDFAlgorithm           = "pbkdf2-sha256"
	KDFIterations          = 600000
	KeyLength              = 32
	SaltLength             = 16
	SealOverhead           = 28
	InlineMaxBytes         = 262144
	ChunkSizeBytes         = 4194304
	MaxChunkBodyBytes      = ChunkSizeBytes + SealOverhead
	MetaMaxBytes           = 65536
	ThumbMaxBytes          = 65536
	MaxJSONBodyBytes       = 1048576
	MaxFilesPerItem        = 1000
	HistoryDefaultLimit    = 100
	HistoryMaxLimit        = 500
	MaxWSClientFrameBytes  = 65536
	MaxWSServerFrameBytes  = 1048576
	MaxConnectionsPerDev   = 8
	UploadTTLSeconds       = 3600
	DiskLowMaxUploadBytes  = 1048576
	DiskLowMaxChunkBody    = DiskLowMaxUploadBytes + SealOverhead
	MaxChunkIndex          = 65536
	MaxDeviceNameRunes     = 64
	MaxIDsPerDeleteMessage = 500
	MissingChunksMaxListed = 100
	TimeLayout             = "2006-01-02T15:04:05.000Z"
)

var SupportedProtocolVersions = []int{ProtocolVersion}

var (
	itemIDPattern = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`)
	tokenPattern  = regexp.MustCompile(`^yc_[A-Za-z0-9_-]{43}$`)
	hex64Pattern  = regexp.MustCompile(`^[0-9a-f]{64}$`)
)

func ValidID(s string) bool { return itemIDPattern.MatchString(s) }

func ValidToken(s string) bool { return tokenPattern.MatchString(s) }

func ValidHex64(s string) bool { return hex64Pattern.MatchString(s) }

func ValidKind(s string) bool { return s == "text" || s == "image" || s == "files" }

func ValidPlatform(s string) bool {
	switch s {
	case "macos", "android", "windows", "web":
		return true
	}
	return false
}

func NewUUIDv7(now time.Time, rnd io.Reader) (string, error) {
	var random [10]byte
	if _, err := io.ReadFull(rnd, random[:]); err != nil {
		return "", fmt.Errorf("read random bytes: %w", err)
	}
	return FormatUUIDv7(now.UnixMilli(), random[:]), nil
}

func FormatUUIDv7(unixMillis int64, random []byte) string {
	var b [16]byte
	var ts [8]byte
	binary.BigEndian.PutUint64(ts[:], uint64(unixMillis))
	copy(b[0:6], ts[2:8])
	copy(b[6:16], random)
	b[6] = 0x70 | (b[6] & 0x0f)
	b[8] = 0x80 | (b[8] & 0x3f)
	h := hex.EncodeToString(b[:])
	return h[0:8] + "-" + h[8:12] + "-" + h[12:16] + "-" + h[16:20] + "-" + h[20:32]
}

func NewToken(rnd io.Reader) (token string, hash string, err error) {
	var raw [32]byte
	if _, err := io.ReadFull(rnd, raw[:]); err != nil {
		return "", "", fmt.Errorf("read random bytes: %w", err)
	}
	token = "yc_" + base64.RawURLEncoding.EncodeToString(raw[:])
	return token, HashToken(token), nil
}

func HashToken(token string) string {
	sum := sha256.Sum256([]byte(token))
	return hex.EncodeToString(sum[:])
}

func FormatTime(t time.Time) string { return t.UTC().Format(TimeLayout) }

func FromMillis(ms int64) time.Time { return time.UnixMilli(ms).UTC() }

type Time struct{ time.Time }

func (t Time) MarshalJSON() ([]byte, error) {
	return []byte(`"` + FormatTime(t.Time) + `"`), nil
}

func DecodeBase64(s string) ([]byte, bool) {
	if strings.ContainsAny(s, "\r\n") {
		return nil, false
	}
	b, err := base64.StdEncoding.Strict().DecodeString(s)
	if err != nil {
		return nil, false
	}
	return b, true
}

func EncodeBase64(b []byte) string { return base64.StdEncoding.EncodeToString(b) }

func NormalizeDeviceName(s string) (string, bool) {
	if !utf8.ValidString(s) {
		return "", false
	}
	name := strings.TrimFunc(s, unicode.IsSpace)
	n := utf8.RuneCountInString(name)
	if n < 1 || n > MaxDeviceNameRunes {
		return "", false
	}
	for _, r := range name {
		if unicode.IsControl(r) {
			return "", false
		}
	}
	return name, true
}

func ChunkCount(size int64) int {
	if size <= InlineMaxBytes {
		return 0
	}
	return int((size + ChunkSizeBytes - 1) / ChunkSizeBytes)
}

func ChunkSealedSize(size int64, index, count int) int64 {
	if index < count-1 {
		return MaxChunkBodyBytes
	}
	return size - int64(count-1)*ChunkSizeBytes + SealOverhead
}

type KDF struct {
	Algorithm  string `json:"algorithm"`
	Iterations int    `json:"iterations"`
	KeyLength  int    `json:"key_length"`
}

func DefaultKDF() KDF {
	return KDF{Algorithm: KDFAlgorithm, Iterations: KDFIterations, KeyLength: KeyLength}
}

type Limits struct {
	InlineMaxBytes          int `json:"inline_max_bytes"`
	ChunkSizeBytes          int `json:"chunk_size_bytes"`
	MaxChunkBodyBytes       int `json:"max_chunk_body_bytes"`
	ThumbMaxBytes           int `json:"thumb_max_bytes"`
	MetaMaxBytes            int `json:"meta_max_bytes"`
	MaxJSONBodyBytes        int `json:"max_json_body_bytes"`
	MaxFilesPerItem         int `json:"max_files_per_item"`
	HistoryDefaultLimit     int `json:"history_default_limit"`
	HistoryMaxLimit         int `json:"history_max_limit"`
	MaxWSClientFrameBytes   int `json:"max_ws_client_frame_bytes"`
	MaxWSServerFrameBytes   int `json:"max_ws_server_frame_bytes"`
	MaxConnectionsPerDevice int `json:"max_connections_per_device"`
	UploadTTLSeconds        int `json:"upload_ttl_seconds"`
	DiskLowMaxUploadBytes   int `json:"disk_low_max_upload_bytes"`
}

func DefaultLimits() Limits {
	return Limits{
		InlineMaxBytes:          InlineMaxBytes,
		ChunkSizeBytes:          ChunkSizeBytes,
		MaxChunkBodyBytes:       MaxChunkBodyBytes,
		ThumbMaxBytes:           ThumbMaxBytes,
		MetaMaxBytes:            MetaMaxBytes,
		MaxJSONBodyBytes:        MaxJSONBodyBytes,
		MaxFilesPerItem:         MaxFilesPerItem,
		HistoryDefaultLimit:     HistoryDefaultLimit,
		HistoryMaxLimit:         HistoryMaxLimit,
		MaxWSClientFrameBytes:   MaxWSClientFrameBytes,
		MaxWSServerFrameBytes:   MaxWSServerFrameBytes,
		MaxConnectionsPerDevice: MaxConnectionsPerDev,
		UploadTTLSeconds:        UploadTTLSeconds,
		DiskLowMaxUploadBytes:   DiskLowMaxUploadBytes,
	}
}

type ItemHeader struct {
	ID          string `json:"id"`
	Seq         int64  `json:"seq"`
	DeviceID    string `json:"device_id"`
	Kind        string `json:"kind"`
	Size        int64  `json:"size"`
	ChunkCount  int    `json:"chunk_count"`
	CreatedAt   Time   `json:"created_at"`
	Pinned      bool   `json:"pinned"`
	ContentHash string `json:"content_hash"`
	HasThumb    bool   `json:"has_thumb"`
	StoredBytes int64  `json:"stored_bytes"`
	Meta        string `json:"meta"`
	Payload     string `json:"payload,omitempty"`
}

type Device struct {
	ID         string `json:"id"`
	Name       string `json:"name"`
	Platform   string `json:"platform"`
	CreatedAt  Time   `json:"created_at"`
	LastSeenAt Time   `json:"last_seen_at"`
	Online     bool   `json:"online"`
	Revoked    bool   `json:"revoked"`
	Current    bool   `json:"current"`
}

type OnlineDevice struct {
	DeviceID string `json:"device_id"`
	Name     string `json:"name"`
	Platform string `json:"platform"`
}

type Welcome struct {
	Type            string         `json:"type"`
	ProtocolVersion int            `json:"protocol_version"`
	ServerVersion   string         `json:"server_version"`
	ServerID        string         `json:"server_id"`
	ServerTime      Time           `json:"server_time"`
	DeviceID        string         `json:"device_id"`
	CurrentSeq      int64          `json:"current_seq"`
	StateRev        int64          `json:"state_rev"`
	OnlineDevices   []OnlineDevice `json:"online_devices"`
}

type Presence struct {
	Type     string `json:"type"`
	DeviceID string `json:"device_id"`
	Name     string `json:"name"`
	Platform string `json:"platform"`
	Online   bool   `json:"online"`
}

type DevicesChanged struct {
	Type string `json:"type"`
}

type Clip struct {
	Type string     `json:"type"`
	Item ItemHeader `json:"item"`
}

type ClipDeleted struct {
	Type     string   `json:"type"`
	IDs      []string `json:"ids"`
	Reason   string   `json:"reason"`
	StateRev int64    `json:"state_rev"`
}

type ClipPinned struct {
	Type     string `json:"type"`
	ID       string `json:"id"`
	Pinned   bool   `json:"pinned"`
	StateRev int64  `json:"state_rev"`
}

type StorageWarning struct {
	Type             string `json:"type"`
	Active           bool   `json:"active"`
	Reason           string `json:"reason"`
	FreeDiskBytes    int64  `json:"free_disk_bytes"`
	MinFreeDiskBytes int64  `json:"min_free_disk_bytes"`
}

type Ping struct {
	Type string `json:"type"`
	TS   int64  `json:"ts"`
}

type WSError struct {
	Type    string `json:"type"`
	Code    string `json:"code"`
	Message string `json:"message"`
	Details any    `json:"details,omitempty"`
}

const (
	DeleteReasonUser      = "user"
	DeleteReasonRetention = "retention"
	DeleteReasonDedupe    = "dedupe"
)
