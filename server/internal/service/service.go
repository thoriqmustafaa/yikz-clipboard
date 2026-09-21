package service

import (
	"context"
	"crypto/rand"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"sync"
	"time"

	"github.com/coder/websocket"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/blob"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/clock"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/disk"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/hub"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/proto"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/ratelimit"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/store"
)

type Events interface {
	Broadcast(msg any)
	CloseDevice(deviceID string, code websocket.StatusCode, reason string)
	RenameDevice(deviceID, name string)
	IsOnline(deviceID string) bool
	OnlineIDs() map[string]bool
}

type Settings struct {
	Username         string
	Password         string
	ServerVersion    string
	RetentionDays    int
	StorageMaxBytes  int64
	PinnedMaxBytes   int64
	MinFreeDiskBytes int64
	UploadTTL        time.Duration
	TouchInterval    time.Duration
}

type Options struct {
	Settings Settings
	Store    *store.Store
	Blobs    *blob.Store
	Disk     *disk.Guard
	Events   Events
	Limiter  *ratelimit.Limiter
	Clock    clock.Clock
	Rand     io.Reader
	Logger   *slog.Logger
}

type Service struct {
	cfg     Settings
	store   *store.Store
	blobs   *blob.Store
	disk    *disk.Guard
	events  Events
	limiter *ratelimit.Limiter
	clock   clock.Clock
	rand    io.Reader
	log     *slog.Logger

	mu      sync.Mutex
	randMu  sync.Mutex
	touchMu sync.Mutex
	touched map[string]time.Time
}

func New(o Options) (*Service, error) {
	if o.Store == nil || o.Blobs == nil || o.Disk == nil || o.Events == nil {
		return nil, errors.New("service: store, blobs, disk and events are required")
	}
	if o.Clock == nil {
		o.Clock = clock.Real{}
	}
	if o.Rand == nil {
		o.Rand = rand.Reader
	}
	if o.Logger == nil {
		o.Logger = slog.Default()
	}
	if o.Limiter == nil {
		o.Limiter = ratelimit.New(10, 10*time.Minute, o.Clock)
	}
	if o.Settings.UploadTTL <= 0 {
		o.Settings.UploadTTL = proto.UploadTTLSeconds * time.Second
	}
	if o.Settings.TouchInterval <= 0 {
		o.Settings.TouchInterval = time.Minute
	}
	if o.Settings.ServerVersion == "" {
		o.Settings.ServerVersion = "1.0.0"
	}
	s := &Service{
		cfg:     o.Settings,
		store:   o.Store,
		blobs:   o.Blobs,
		disk:    o.Disk,
		events:  o.Events,
		limiter: o.Limiter,
		clock:   o.Clock,
		rand:    o.Rand,
		log:     o.Logger,
		touched: map[string]time.Time{},
	}
	return s, nil
}

func (s *Service) Settings() Settings { return s.cfg }

func (s *Service) readRand(n int) ([]byte, error) {
	s.randMu.Lock()
	defer s.randMu.Unlock()
	b := make([]byte, n)
	if _, err := io.ReadFull(s.rand, b); err != nil {
		return nil, fmt.Errorf("read random bytes: %w", err)
	}
	return b, nil
}

func (s *Service) newUUID() (string, error) {
	b, err := s.readRand(10)
	if err != nil {
		return "", err
	}
	return proto.FormatUUIDv7(s.clock.Now().UnixMilli(), b), nil
}

func (s *Service) newToken() (string, string, error) {
	b, err := s.readRand(32)
	if err != nil {
		return "", "", err
	}
	return proto.NewToken(bytesReader(b))
}

type bytesReader []byte

func (b bytesReader) Read(p []byte) (int, error) {
	n := copy(p, b)
	if n < len(p) {
		return n, io.EOF
	}
	return n, nil
}

func (s *Service) Init(ctx context.Context) error {
	acct, err := s.store.InitAccount(ctx, s.cfg.Username,
		func() ([]byte, error) { return s.readRand(proto.SaltLength) },
		s.newUUID)
	if err != nil {
		return err
	}
	s.log.Info("account ready", "username", acct.Username, "server_id", acct.ServerID, "current_seq", acct.CurrentSeq, "state_rev", acct.StateRev, "key_check_set", acct.KeyCheck != nil)
	return s.cleanup(ctx)
}

func (s *Service) cleanup(ctx context.Context) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.store.DropPendingForCommitted(ctx); err != nil {
		return fmt.Errorf("clean pending uploads: %w", err)
	}
	known, err := s.store.KnownIDs(ctx)
	if err != nil {
		return fmt.Errorf("list known ids: %w", err)
	}
	ids, err := s.blobs.IDs()
	if err != nil {
		return fmt.Errorf("list blobs: %w", err)
	}
	removed := 0
	for _, id := range ids {
		if known[id] {
			continue
		}
		if err := s.blobs.RemoveItem(id); err != nil {
			s.log.Warn("remove orphan blob dir", "id", id, "err", err)
			continue
		}
		removed++
	}
	if removed > 0 {
		s.log.Info("removed orphan blob dirs", "count", removed)
	}
	return nil
}

func (s *Service) Snapshot(ctx context.Context) (hub.Snapshot, error) {
	acct, err := s.store.Account(ctx)
	if err != nil {
		return hub.Snapshot{}, err
	}
	st := s.disk.Status()
	return hub.Snapshot{
		ServerID:         acct.ServerID,
		CurrentSeq:       acct.CurrentSeq,
		StateRev:         acct.StateRev,
		DiskLow:          st.Low,
		FreeDiskBytes:    st.FreeBytes,
		MinFreeDiskBytes: st.MinFreeBytes,
	}, nil
}

func (s *Service) Touch(deviceID string) {
	now := s.clock.Now()
	s.touchMu.Lock()
	last, ok := s.touched[deviceID]
	if ok && now.Sub(last) < s.cfg.TouchInterval {
		s.touchMu.Unlock()
		return
	}
	s.touched[deviceID] = now
	s.touchMu.Unlock()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := s.store.TouchDevice(ctx, deviceID, now); err != nil {
		s.log.Warn("update last_seen_at", "device_id", deviceID, "err", err)
	}
}

func (s *Service) forgetTouch(deviceID string) {
	s.touchMu.Lock()
	delete(s.touched, deviceID)
	s.touchMu.Unlock()
}

func (s *Service) StorageWarning(st disk.Status) proto.StorageWarning {
	return proto.StorageWarning{
		Type:             "storage_warning",
		Active:           st.Low,
		Reason:           "disk_low",
		FreeDiskBytes:    st.FreeBytes,
		MinFreeDiskBytes: st.MinFreeBytes,
	}
}
