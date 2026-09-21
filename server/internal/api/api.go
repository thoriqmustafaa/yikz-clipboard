package api

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"strconv"
	"strings"
	"time"

	"github.com/coder/websocket"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/apierr"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/hub"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/proto"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/service"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/store"
)

type Options struct {
	Service        *service.Service
	Hub            *hub.Hub
	WebUI          http.Handler
	TrustedProxies []netip.Prefix
	Logger         *slog.Logger
	ServerVersion  string
}

type API struct {
	svc     *service.Service
	hub     *hub.Hub
	webui   http.Handler
	proxies []netip.Prefix
	log     *slog.Logger
	version string
}

func New(o Options) http.Handler {
	if o.Logger == nil {
		o.Logger = slog.Default()
	}
	if o.ServerVersion == "" {
		o.ServerVersion = "1.0.0"
	}
	a := &API{svc: o.Service, hub: o.Hub, webui: o.WebUI, proxies: o.TrustedProxies, log: o.Logger, version: o.ServerVersion}
	mux := http.NewServeMux()
	mux.Handle("/healthz", methods{http.MethodGet: a.healthz, http.MethodHead: a.healthz})
	mux.Handle("/api/login", methods{http.MethodPost: a.login})
	mux.Handle("/api/me", methods{http.MethodGet: a.auth(a.me)})
	mux.Handle("/api/logout", methods{http.MethodPost: a.auth(a.logout)})
	mux.Handle("/api/account/key-check", methods{http.MethodPut: a.auth(a.putKeyCheck), http.MethodDelete: a.auth(a.deleteKeyCheck)})
	mux.Handle("/api/devices", methods{http.MethodGet: a.auth(a.devices)})
	mux.Handle("/api/devices/{id}", methods{http.MethodPatch: a.auth(a.renameDevice), http.MethodDelete: a.auth(a.deleteDevice)})
	mux.Handle("/api/items", methods{http.MethodPost: a.auth(a.createItem)})
	mux.Handle("/api/items/{id}", methods{http.MethodGet: a.auth(a.itemID(a.getItem)), http.MethodDelete: a.auth(a.itemID(a.deleteItem))})
	mux.Handle("/api/items/{id}/thumb", methods{http.MethodPut: a.auth(a.itemID(a.putThumb)), http.MethodGet: a.auth(a.itemID(a.getThumb))})
	mux.Handle("/api/items/{id}/chunks/{n}", methods{http.MethodPut: a.auth(a.itemID(a.putChunk)), http.MethodGet: a.auth(a.itemID(a.getChunk))})
	mux.Handle("/api/items/{id}/commit", methods{http.MethodPost: a.auth(a.itemID(a.commit))})
	mux.Handle("/api/items/{id}/pin", methods{http.MethodPost: a.auth(a.itemID(a.pin))})
	mux.Handle("/api/history", methods{http.MethodGet: a.auth(a.history)})
	mux.Handle("/api/history/index", methods{http.MethodGet: a.auth(a.historyIndex)})
	mux.Handle("/api/storage", methods{http.MethodGet: a.auth(a.storage)})
	mux.Handle("/api/", http.HandlerFunc(routeNotFound))
	mux.Handle("/api", http.HandlerFunc(routeNotFound))
	mux.Handle("/ws", methods{http.MethodGet: a.ws})
	mux.Handle("/ws/", http.HandlerFunc(routeNotFound))
	mux.Handle("/", a.static())
	return a.recoverer(a.logRequests(mux))
}

type methods map[string]http.HandlerFunc

func (m methods) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if h, ok := m[r.Method]; ok {
		h(w, r)
		return
	}
	allowed := make([]string, 0, len(m))
	for k := range m {
		allowed = append(allowed, k)
	}
	w.Header().Set("Allow", strings.Join(allowed, ", "))
	writeError(w, apierr.New(http.StatusMethodNotAllowed, apierr.CodeMethodNotAllowed, "method not allowed"))
}

func routeNotFound(w http.ResponseWriter, r *http.Request) {
	writeError(w, apierr.NotFound("route not found"))
}

func (a *API) static() http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if a.webui == nil || (r.Method != http.MethodGet && r.Method != http.MethodHead) {
			routeNotFound(w, r)
			return
		}
		a.webui.ServeHTTP(w, r)
	})
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	data, err := json.Marshal(v)
	if err != nil {
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	h := w.Header()
	h.Set("Content-Type", "application/json; charset=utf-8")
	h.Set("Cache-Control", "no-store")
	h.Set("Content-Length", strconv.Itoa(len(data)))
	w.WriteHeader(status)
	w.Write(data)
}

type errorBody struct {
	Code    string `json:"code"`
	Message string `json:"message"`
	Details any    `json:"details,omitempty"`
}

func writeError(w http.ResponseWriter, e *apierr.Error) {
	for k, v := range e.Header {
		w.Header()[k] = v
	}
	writeJSON(w, e.Status, errorBody{Code: e.Code, Message: e.Message, Details: e.Details})
}

func (a *API) fail(w http.ResponseWriter, r *http.Request, err error) {
	if e, ok := apierr.As(err); ok {
		writeError(w, e)
		return
	}
	if errors.Is(err, context.Canceled) && r.Context().Err() != nil {
		return
	}
	a.log.Error("request failed", "method", r.Method, "path", r.URL.Path, "err", err)
	writeError(w, apierr.Internal())
}

func decodeJSON(w http.ResponseWriter, r *http.Request, dst any) error {
	r.Body = http.MaxBytesReader(w, r.Body, proto.MaxJSONBodyBytes)
	data, err := io.ReadAll(r.Body)
	if err != nil {
		var mbe *http.MaxBytesError
		if errors.As(err, &mbe) {
			return apierr.BodyTooLarge("request body must be at most 1048576 bytes")
		}
		return apierr.InvalidRequest("failed to read request body")
	}
	trimmed := bytes.TrimSpace(data)
	if len(trimmed) == 0 || trimmed[0] != '{' {
		return apierr.InvalidRequest("request body must be a JSON object")
	}
	dec := json.NewDecoder(bytes.NewReader(trimmed))
	if err := dec.Decode(dst); err != nil {
		return apierr.InvalidRequest("invalid JSON body: " + err.Error())
	}
	if _, err := dec.Token(); !errors.Is(err, io.EOF) {
		return apierr.InvalidRequest("request body must contain a single JSON object")
	}
	return nil
}

type ctxKey struct{}

func deviceFrom(r *http.Request) store.Device {
	d, _ := r.Context().Value(ctxKey{}).(store.Device)
	return d
}

func tokenFrom(r *http.Request) string {
	if h := r.Header.Get("Authorization"); h != "" {
		scheme, tok, ok := strings.Cut(h, " ")
		if !ok || !strings.EqualFold(scheme, "Bearer") {
			return ""
		}
		return strings.TrimSpace(tok)
	}
	return r.URL.Query().Get("token")
}

func (a *API) authenticate(r *http.Request) (store.Device, error) {
	tok := tokenFrom(r)
	if tok == "" {
		return store.Device{}, apierr.Unauthorized()
	}
	dev, err := a.svc.Authenticate(r.Context(), tok)
	if err != nil {
		return store.Device{}, err
	}
	a.svc.Touch(dev.ID)
	return dev, nil
}

func (a *API) auth(next func(http.ResponseWriter, *http.Request, store.Device)) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		dev, err := a.authenticate(r)
		if err != nil {
			a.fail(w, r, err)
			return
		}
		next(w, r.WithContext(context.WithValue(r.Context(), ctxKey{}, dev)), dev)
	}
}

func (a *API) itemID(next func(http.ResponseWriter, *http.Request, store.Device, string)) func(http.ResponseWriter, *http.Request, store.Device) {
	return func(w http.ResponseWriter, r *http.Request, dev store.Device) {
		id := r.PathValue("id")
		if !proto.ValidID(id) {
			writeError(w, apierr.InvalidID())
			return
		}
		next(w, r, dev, id)
	}
}

func (a *API) clientIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		host = r.RemoteAddr
	}
	addr, err := netip.ParseAddr(host)
	if err != nil {
		return host
	}
	addr = addr.Unmap()
	if !a.trusted(addr) {
		return addr.String()
	}
	var hops []string
	for _, v := range r.Header.Values("X-Forwarded-For") {
		for _, part := range strings.Split(v, ",") {
			if p := strings.TrimSpace(part); p != "" {
				hops = append(hops, p)
			}
		}
	}
	for i := len(hops) - 1; i >= 0; i-- {
		hop, err := netip.ParseAddr(hops[i])
		if err != nil {
			return addr.String()
		}
		hop = hop.Unmap()
		if !a.trusted(hop) {
			return hop.String()
		}
		addr = hop
	}
	return addr.String()
}

func (a *API) trusted(addr netip.Addr) bool {
	for _, p := range a.proxies {
		if p.Contains(addr) {
			return true
		}
	}
	return false
}

type statusRecorder struct {
	http.ResponseWriter
	status int
	bytes  int64
}

func (s *statusRecorder) WriteHeader(code int) {
	if s.status == 0 {
		s.status = code
	}
	s.ResponseWriter.WriteHeader(code)
}

func (s *statusRecorder) Write(p []byte) (int, error) {
	if s.status == 0 {
		s.status = http.StatusOK
	}
	n, err := s.ResponseWriter.Write(p)
	s.bytes += int64(n)
	return n, err
}

func (s *statusRecorder) Unwrap() http.ResponseWriter { return s.ResponseWriter }

func RedactedURI(u *url.URL) string {
	if u.RawQuery == "" {
		return u.Path
	}
	q := u.Query()
	if q.Has("token") {
		q.Set("token", "REDACTED")
	}
	return u.Path + "?" + q.Encode()
}

func (a *API) logRequests(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		rec := &statusRecorder{ResponseWriter: w}
		next.ServeHTTP(rec, r)
		if r.URL.Path == "/ws" && rec.status == 0 {
			rec.status = http.StatusSwitchingProtocols
		}
		level := slog.LevelInfo
		switch {
		case rec.status >= 500:
			level = slog.LevelError
		case r.URL.Path == "/healthz" || (!strings.HasPrefix(r.URL.Path, "/api/") && r.URL.Path != "/ws" && rec.status < 400):
			level = slog.LevelDebug
		}
		a.log.Log(r.Context(), level, "http request",
			"method", r.Method,
			"uri", RedactedURI(r.URL),
			"status", rec.status,
			"bytes", rec.bytes,
			"duration_ms", time.Since(start).Milliseconds(),
			"ip", a.clientIP(r),
		)
	})
}

func (a *API) recoverer(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		defer func() {
			if v := recover(); v != nil {
				if v == http.ErrAbortHandler {
					panic(v)
				}
				a.log.Error("panic in handler", "panic", v, "path", r.URL.Path)
				writeError(w, apierr.Internal())
			}
		}()
		next.ServeHTTP(w, r)
	})
}

func (a *API) healthz(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{"status": "ok", "server_version": a.version, "protocol_version": proto.ProtocolVersion})
}

func (a *API) ws(w http.ResponseWriter, r *http.Request) {
	dev, err := a.authenticate(r)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	conn, err := websocket.Accept(w, r, &websocket.AcceptOptions{CompressionMode: websocket.CompressionDisabled})
	if err != nil {
		a.log.Debug("websocket accept failed", "err", err)
		return
	}
	a.hub.Serve(context.WithoutCancel(r.Context()), conn, hub.DeviceInfo{ID: dev.ID, Name: dev.Name, Platform: dev.Platform}, a.svc)
}
