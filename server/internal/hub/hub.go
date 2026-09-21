package hub

import (
	"context"
	"encoding/json"
	"log/slog"
	"sync"
	"time"

	"github.com/coder/websocket"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/clock"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/proto"
)

const (
	CloseUnauthorized    websocket.StatusCode = 4001
	CloseTooManyConns    websocket.StatusCode = 4002
	CloseVersion         websocket.StatusCode = 4003
	CloseHello           websocket.StatusCode = 4004
	CloseHeartbeat       websocket.StatusCode = 4005
	CloseGoingAway                            = websocket.StatusGoingAway
	CloseSlowConsumer                         = websocket.StatusPolicyViolation
	CloseInternalFailure                      = websocket.StatusInternalError
)

type Config struct {
	PingInterval      time.Duration
	DeadTimeout       time.Duration
	HelloTimeout      time.Duration
	WriteTimeout      time.Duration
	MaxConnsPerDevice int
	QueueSize         int
	ReadLimit         int64
	ServerVersion     string
}

func DefaultConfig() Config {
	return Config{
		PingInterval:      20 * time.Second,
		DeadTimeout:       45 * time.Second,
		HelloTimeout:      10 * time.Second,
		WriteTimeout:      15 * time.Second,
		MaxConnsPerDevice: proto.MaxConnectionsPerDev,
		QueueSize:         64,
		ReadLimit:         proto.MaxWSClientFrameBytes,
		ServerVersion:     "1.0.0",
	}
}

type DeviceInfo struct {
	ID       string
	Name     string
	Platform string
}

type Snapshot struct {
	ServerID         string
	CurrentSeq       int64
	StateRev         int64
	DiskLow          bool
	FreeDiskBytes    int64
	MinFreeDiskBytes int64
}

type Backend interface {
	Snapshot(ctx context.Context) (Snapshot, error)
	Touch(deviceID string)
}

type Hub struct {
	cfg   Config
	clock clock.Clock
	log   *slog.Logger

	mu      sync.Mutex
	conns   map[*conn]struct{}
	nextSeq uint64
	closed  bool
	empty   chan struct{}
}

func New(cfg Config, clk clock.Clock, log *slog.Logger) *Hub {
	d := DefaultConfig()
	if cfg.PingInterval <= 0 {
		cfg.PingInterval = d.PingInterval
	}
	if cfg.DeadTimeout <= 0 {
		cfg.DeadTimeout = d.DeadTimeout
	}
	if cfg.HelloTimeout <= 0 {
		cfg.HelloTimeout = d.HelloTimeout
	}
	if cfg.WriteTimeout <= 0 {
		cfg.WriteTimeout = d.WriteTimeout
	}
	if cfg.MaxConnsPerDevice <= 0 {
		cfg.MaxConnsPerDevice = d.MaxConnsPerDevice
	}
	if cfg.QueueSize <= 0 {
		cfg.QueueSize = d.QueueSize
	}
	if cfg.ReadLimit <= 0 {
		cfg.ReadLimit = d.ReadLimit
	}
	if cfg.ServerVersion == "" {
		cfg.ServerVersion = d.ServerVersion
	}
	if clk == nil {
		clk = clock.Real{}
	}
	if log == nil {
		log = slog.Default()
	}
	return &Hub{cfg: cfg, clock: clk, log: log, conns: map[*conn]struct{}{}}
}

func (h *Hub) Broadcast(msg any) {
	data, err := json.Marshal(msg)
	if err != nil {
		h.log.Error("marshal broadcast", "err", err)
		return
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	for c := range h.conns {
		if c.subscribed {
			c.enqueue(data)
		}
	}
}

func (h *Hub) broadcastExceptLocked(deviceID string, msg any) {
	data, err := json.Marshal(msg)
	if err != nil {
		h.log.Error("marshal broadcast", "err", err)
		return
	}
	for c := range h.conns {
		if c.subscribed && c.dev.ID != deviceID {
			c.enqueue(data)
		}
	}
}

func (h *Hub) CloseDevice(deviceID string, code websocket.StatusCode, reason string) {
	h.mu.Lock()
	defer h.mu.Unlock()
	for c := range h.conns {
		if c.dev.ID == deviceID {
			c.closeWith(code, reason)
		}
	}
}

func (h *Hub) RenameDevice(deviceID, name string) {
	h.mu.Lock()
	defer h.mu.Unlock()
	for c := range h.conns {
		if c.dev.ID == deviceID {
			c.dev.Name = name
		}
	}
}

func (h *Hub) IsOnline(deviceID string) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	for c := range h.conns {
		if c.live && c.dev.ID == deviceID {
			return true
		}
	}
	return false
}

func (h *Hub) OnlineIDs() map[string]bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	out := map[string]bool{}
	for c := range h.conns {
		if c.live {
			out[c.dev.ID] = true
		}
	}
	return out
}

func (h *Hub) ConnCount() int {
	h.mu.Lock()
	defer h.mu.Unlock()
	return len(h.conns)
}

func (h *Hub) onlineList(self DeviceInfo) []proto.OnlineDevice {
	h.mu.Lock()
	defer h.mu.Unlock()
	type entry struct {
		dev DeviceInfo
		seq uint64
	}
	first := map[string]entry{}
	for c := range h.conns {
		if !c.live || c.dev.ID == self.ID {
			continue
		}
		if e, ok := first[c.dev.ID]; !ok || c.seq < e.seq {
			first[c.dev.ID] = entry{dev: c.dev, seq: c.seq}
		}
	}
	list := make([]entry, 0, len(first))
	for _, e := range first {
		list = append(list, e)
	}
	for i := 1; i < len(list); i++ {
		for j := i; j > 0 && list[j].seq < list[j-1].seq; j-- {
			list[j], list[j-1] = list[j-1], list[j]
		}
	}
	out := []proto.OnlineDevice{{DeviceID: self.ID, Name: self.Name, Platform: self.Platform}}
	for _, e := range list {
		out = append(out, proto.OnlineDevice{DeviceID: e.dev.ID, Name: e.dev.Name, Platform: e.dev.Platform})
	}
	return out
}

func (h *Hub) add(c *conn) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.closed {
		return false
	}
	h.nextSeq++
	c.seq = h.nextSeq
	h.conns[c] = struct{}{}
	return true
}

func (h *Hub) subscribe(c *conn) {
	h.mu.Lock()
	c.subscribed = true
	h.mu.Unlock()
}

func (h *Hub) markLive(c *conn) {
	h.mu.Lock()
	defer h.mu.Unlock()
	var live []*conn
	for o := range h.conns {
		if o.live && o.dev.ID == c.dev.ID {
			live = append(live, o)
		}
	}
	c.live = true
	if len(live) == 0 {
		h.broadcastExceptLocked(c.dev.ID, proto.Presence{Type: "presence", DeviceID: c.dev.ID, Name: c.dev.Name, Platform: c.dev.Platform, Online: true})
	}
	live = append(live, c)
	for len(live) > h.cfg.MaxConnsPerDevice {
		oldest := 0
		for i := range live {
			if live[i].seq < live[oldest].seq {
				oldest = i
			}
		}
		live[oldest].closeWith(CloseTooManyConns, "too many connections for this device")
		live = append(live[:oldest], live[oldest+1:]...)
	}
}

func (h *Hub) remove(c *conn) {
	h.mu.Lock()
	defer h.mu.Unlock()
	delete(h.conns, c)
	if c.live {
		c.live = false
		stillOnline := false
		for o := range h.conns {
			if o.live && o.dev.ID == c.dev.ID {
				stillOnline = true
				break
			}
		}
		if !stillOnline {
			h.broadcastExceptLocked(c.dev.ID, proto.Presence{Type: "presence", DeviceID: c.dev.ID, Name: c.dev.Name, Platform: c.dev.Platform, Online: false})
		}
	}
	if len(h.conns) == 0 && h.empty != nil {
		close(h.empty)
		h.empty = nil
	}
}

func (h *Hub) Shutdown(ctx context.Context) {
	h.mu.Lock()
	h.closed = true
	if len(h.conns) == 0 {
		h.mu.Unlock()
		return
	}
	empty := make(chan struct{})
	h.empty = empty
	for c := range h.conns {
		c.closeWith(CloseGoingAway, "server shutting down")
	}
	h.mu.Unlock()
	select {
	case <-empty:
	case <-ctx.Done():
		h.mu.Lock()
		for c := range h.conns {
			go c.ws.CloseNow()
		}
		h.mu.Unlock()
	}
}
