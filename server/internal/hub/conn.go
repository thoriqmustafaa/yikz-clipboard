package hub

import (
	"context"
	"encoding/json"
	"fmt"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/proto"
)

type conn struct {
	h          *Hub
	ws         *websocket.Conn
	dev        DeviceInfo
	seq        uint64
	subscribed bool
	live       bool
	send       chan []byte
	closing    atomic.Bool
	done       chan struct{}
}

func (c *conn) enqueue(data []byte) {
	if c.closing.Load() {
		return
	}
	select {
	case c.send <- data:
	default:
		c.h.log.Warn("websocket send queue full, closing connection", "device_id", c.dev.ID)
		c.closeWith(CloseSlowConsumer, "slow consumer")
	}
}

func (c *conn) closeWith(code websocket.StatusCode, reason string) {
	if !c.closing.CompareAndSwap(false, true) {
		return
	}
	go c.ws.Close(code, reason)
}

func (c *conn) writeDirect(ctx context.Context, msg any) error {
	data, err := json.Marshal(msg)
	if err != nil {
		return err
	}
	return c.writeRaw(ctx, data)
}

func (c *conn) writeRaw(ctx context.Context, data []byte) error {
	wctx, cancel := context.WithTimeout(ctx, c.h.cfg.WriteTimeout)
	defer cancel()
	return c.ws.Write(wctx, websocket.MessageText, data)
}

func wsError(code, message string, details any) proto.WSError {
	return proto.WSError{Type: "error", Code: code, Message: message, Details: details}
}

func (c *conn) fail(ctx context.Context, code websocket.StatusCode, errCode, message string, details any) {
	if c.closing.Load() {
		return
	}
	if err := c.writeDirect(ctx, wsError(errCode, message, details)); err != nil {
		c.ws.CloseNow()
		return
	}
	c.closeWith(code, message)
}

type envelope struct {
	typ  string
	data []byte
}

func parseEnvelope(data []byte) (envelope, bool) {
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(data, &fields); err != nil || fields == nil {
		return envelope{}, false
	}
	raw, ok := fields["type"]
	if !ok {
		return envelope{}, false
	}
	var typ string
	if err := json.Unmarshal(raw, &typ); err != nil {
		return envelope{}, false
	}
	return envelope{typ: typ, data: data}, true
}

type helloMsg struct {
	ProtocolVersion *int   `json:"protocol_version"`
	DeviceID        string `json:"device_id"`
	LastSeq         int64  `json:"last_seq"`
	AppVersion      string `json:"app_version"`
	Platform        string `json:"platform"`
}

type pingMsg struct {
	TS json.RawMessage `json:"ts"`
}

type pongReply struct {
	Type string          `json:"type"`
	TS   json.RawMessage `json:"ts"`
}

func pong(data []byte) pongReply {
	var p pingMsg
	json.Unmarshal(data, &p)
	var n json.Number
	if len(p.TS) == 0 || json.Unmarshal(p.TS, &n) != nil {
		return pongReply{Type: "pong", TS: json.RawMessage("0")}
	}
	return pongReply{Type: "pong", TS: json.RawMessage(n.String())}
}

func (h *Hub) Serve(ctx context.Context, ws *websocket.Conn, dev DeviceInfo, b Backend) {
	ws.SetReadLimit(h.cfg.ReadLimit)
	c := &conn{h: h, ws: ws, dev: dev, send: make(chan []byte, h.cfg.QueueSize), done: make(chan struct{})}
	defer ws.CloseNow()
	if !h.add(c) {
		ws.Close(CloseGoingAway, "server shutting down")
		return
	}
	defer h.remove(c)
	defer close(c.done)

	ctx, cancel := context.WithCancel(ctx)
	defer cancel()

	log := h.log.With("device_id", dev.ID, "conn", c.seq)
	log.Debug("websocket connected")

	helloTimer := time.AfterFunc(h.cfg.HelloTimeout, func() {
		c.closeWith(CloseHello, "hello timeout")
	})
	defer helloTimer.Stop()
	deadTimer := time.AfterFunc(h.cfg.DeadTimeout, func() {
		c.closeWith(CloseHeartbeat, "heartbeat timeout")
	})
	defer deadTimer.Stop()

	welcomed := false
	for {
		typ, data, err := ws.Read(ctx)
		if err != nil {
			status := websocket.CloseStatus(err)
			log.Debug("websocket closed", "status", int(status), "err", err)
			return
		}
		if c.closing.Load() {
			continue
		}
		deadTimer.Reset(h.cfg.DeadTimeout)
		b.Touch(dev.ID)

		env, ok := parseEnvelope(data)
		if typ != websocket.MessageText {
			ok = false
		}
		if !welcomed {
			switch {
			case ok && env.typ == "ping":
				if err := c.writeDirect(ctx, pong(data)); err != nil {
					return
				}
			case ok && env.typ == "pong":
			case ok && env.typ == "hello":
				if !c.handshake(ctx, env.data, dev, b, helloTimer) {
					continue
				}
				welcomed = true
				go c.writer(ctx)
			default:
				c.fail(ctx, CloseHello, "hello_required", "the first message must be hello", nil)
			}
			continue
		}
		if !ok {
			c.reply(wsError("invalid_message", "frame is not a JSON object with a string type field", nil))
			continue
		}
		switch env.typ {
		case "ping":
			c.reply(pong(data))
		case "pong", "hello":
		default:
			c.reply(wsError("unknown_type", "unknown message type: "+env.typ, nil))
		}
	}
}

func (c *conn) reply(msg any) {
	data, err := json.Marshal(msg)
	if err != nil {
		return
	}
	c.enqueue(data)
}

func (c *conn) handshake(ctx context.Context, data []byte, dev DeviceInfo, b Backend, helloTimer *time.Timer) bool {
	h := c.h
	var hello helloMsg
	if err := json.Unmarshal(data, &hello); err != nil {
		c.fail(ctx, CloseHello, "hello_required", "the first message must be a valid hello", nil)
		return false
	}
	version := 0
	if hello.ProtocolVersion != nil {
		version = *hello.ProtocolVersion
	}
	supported := false
	for _, v := range proto.SupportedProtocolVersions {
		if v == version {
			supported = true
		}
	}
	if !supported {
		c.fail(ctx, CloseVersion, "protocol_version_unsupported", fmt.Sprintf("server supports protocol_version %d", proto.ProtocolVersion), map[string]any{"supported": proto.SupportedProtocolVersions})
		return false
	}
	if hello.DeviceID != dev.ID {
		c.fail(ctx, CloseUnauthorized, "device_mismatch", "device_id does not match the token", nil)
		return false
	}
	helloTimer.Stop()

	h.subscribe(c)
	snap, err := b.Snapshot(ctx)
	if err != nil {
		h.log.Error("websocket snapshot", "err", err)
		c.closeWith(CloseInternalFailure, "internal error")
		return false
	}
	welcome := proto.Welcome{
		Type:            "welcome",
		ProtocolVersion: proto.ProtocolVersion,
		ServerVersion:   h.cfg.ServerVersion,
		ServerID:        snap.ServerID,
		ServerTime:      proto.Time{Time: h.clock.Now()},
		DeviceID:        dev.ID,
		CurrentSeq:      snap.CurrentSeq,
		StateRev:        snap.StateRev,
		OnlineDevices:   h.onlineList(dev),
	}
	if err := c.writeDirect(ctx, welcome); err != nil {
		c.ws.CloseNow()
		return false
	}
	if snap.DiskLow {
		warn := proto.StorageWarning{Type: "storage_warning", Active: true, Reason: "disk_low", FreeDiskBytes: snap.FreeDiskBytes, MinFreeDiskBytes: snap.MinFreeDiskBytes}
		if err := c.writeDirect(ctx, warn); err != nil {
			c.ws.CloseNow()
			return false
		}
	}
	h.markLive(c)
	h.log.Info("websocket ready", "device_id", dev.ID, "platform", dev.Platform, "last_seq", hello.LastSeq, "app_version", hello.AppVersion, "current_seq", snap.CurrentSeq)
	return true
}

func (c *conn) writer(ctx context.Context) {
	t := time.NewTicker(c.h.cfg.PingInterval)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-c.done:
			return
		case data := <-c.send:
			if err := c.writeRaw(ctx, data); err != nil {
				c.ws.CloseNow()
				return
			}
		case <-t.C:
			if err := c.writeDirect(ctx, proto.Ping{Type: "ping", TS: c.h.clock.Now().UnixMilli()}); err != nil {
				c.ws.CloseNow()
				return
			}
		}
	}
}
