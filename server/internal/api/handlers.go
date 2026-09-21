package api

import (
	"errors"
	"io"
	"net/http"
	"os"
	"strconv"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/apierr"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/proto"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/service"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/store"
)

func (a *API) login(w http.ResponseWriter, r *http.Request) {
	var req service.LoginRequest
	if err := decodeJSON(w, r, &req); err != nil {
		a.fail(w, r, err)
		return
	}
	resp, err := a.svc.Login(r.Context(), req, a.clientIP(r))
	if err != nil {
		a.fail(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, resp)
}

func (a *API) me(w http.ResponseWriter, r *http.Request, dev store.Device) {
	resp, err := a.svc.Me(r.Context(), dev)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, resp)
}

func (a *API) logout(w http.ResponseWriter, r *http.Request, dev store.Device) {
	if err := a.svc.Logout(r.Context(), dev); err != nil {
		a.fail(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (a *API) putKeyCheck(w http.ResponseWriter, r *http.Request, dev store.Device) {
	var req struct {
		KeyCheck *string `json:"key_check"`
	}
	if err := decodeJSON(w, r, &req); err != nil {
		a.fail(w, r, err)
		return
	}
	if err := a.svc.SetKeyCheck(r.Context(), req.KeyCheck); err != nil {
		a.fail(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (a *API) deleteKeyCheck(w http.ResponseWriter, r *http.Request, dev store.Device) {
	if err := a.svc.ResetKeyCheck(r.Context()); err != nil {
		a.fail(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (a *API) devices(w http.ResponseWriter, r *http.Request, dev store.Device) {
	list, err := a.svc.Devices(r.Context(), dev)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"devices": list})
}

func (a *API) renameDevice(w http.ResponseWriter, r *http.Request, dev store.Device) {
	var req struct {
		Name *string `json:"name"`
	}
	if err := decodeJSON(w, r, &req); err != nil {
		a.fail(w, r, err)
		return
	}
	d, err := a.svc.RenameDevice(r.Context(), dev, r.PathValue("id"), req.Name)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, d)
}

func (a *API) deleteDevice(w http.ResponseWriter, r *http.Request, dev store.Device) {
	if err := a.svc.DeleteDevice(r.Context(), r.PathValue("id")); err != nil {
		a.fail(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (a *API) createItem(w http.ResponseWriter, r *http.Request, dev store.Device) {
	var req service.ItemRequest
	if err := decodeJSON(w, r, &req); err != nil {
		a.fail(w, r, err)
		return
	}
	h, created, err := a.svc.CreateInline(r.Context(), dev, req)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	writeJSON(w, createdStatus(created), h)
}

func createdStatus(created bool) int {
	if created {
		return http.StatusCreated
	}
	return http.StatusOK
}

func (a *API) commit(w http.ResponseWriter, r *http.Request, dev store.Device, id string) {
	var req service.ItemRequest
	if err := decodeJSON(w, r, &req); err != nil {
		a.fail(w, r, err)
		return
	}
	h, created, err := a.svc.Commit(r.Context(), dev, id, req)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	writeJSON(w, createdStatus(created), h)
}

func (a *API) putChunk(w http.ResponseWriter, r *http.Request, dev store.Device, id string) {
	n, ok := service.ParseChunkIndex(r.PathValue("n"))
	if !ok {
		writeError(w, apierr.InvalidRequest("chunk index must be an integer between 0 and 65535"))
		return
	}
	if err := a.svc.PutChunk(r.Context(), dev, id, n, r.Body, r.ContentLength); err != nil {
		drain(r)
		a.fail(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (a *API) putThumb(w http.ResponseWriter, r *http.Request, dev store.Device, id string) {
	if err := a.svc.PutThumb(r.Context(), dev, id, r.Body, r.ContentLength); err != nil {
		drain(r)
		a.fail(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func drain(r *http.Request) {
	io.CopyN(io.Discard, r.Body, 64<<10)
}

func (a *API) getItem(w http.ResponseWriter, r *http.Request, dev store.Device, id string) {
	h, err := a.svc.Item(r.Context(), id)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, h)
}

func (a *API) deleteItem(w http.ResponseWriter, r *http.Request, dev store.Device, id string) {
	if err := a.svc.Delete(r.Context(), id); err != nil {
		a.fail(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (a *API) getChunk(w http.ResponseWriter, r *http.Request, dev store.Device, id string) {
	n, ok := service.ParseChunkIndex(r.PathValue("n"))
	if !ok {
		writeError(w, apierr.NotFound("chunk not found"))
		return
	}
	f, size, err := a.svc.OpenChunk(r.Context(), id, n)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	defer f.Close()
	a.serveBlob(w, r, f, size)
}

func (a *API) getThumb(w http.ResponseWriter, r *http.Request, dev store.Device, id string) {
	f, size, err := a.svc.OpenThumb(r.Context(), id)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	defer f.Close()
	a.serveBlob(w, r, f, size)
}

func (a *API) serveBlob(w http.ResponseWriter, r *http.Request, f *os.File, size int64) {
	h := w.Header()
	h.Set("Content-Type", "application/octet-stream")
	h.Set("Content-Length", strconv.FormatInt(size, 10))
	h.Set("Cache-Control", "private, max-age=86400, immutable")
	h.Set("X-Content-Type-Options", "nosniff")
	w.WriteHeader(http.StatusOK)
	if _, err := io.Copy(w, io.LimitReader(f, size)); err != nil && r.Context().Err() == nil {
		a.log.Debug("blob download interrupted", "path", r.URL.Path, "err", err)
	}
}

func (a *API) pin(w http.ResponseWriter, r *http.Request, dev store.Device, id string) {
	var req struct {
		Pinned *bool `json:"pinned"`
	}
	if err := decodeJSON(w, r, &req); err != nil {
		a.fail(w, r, err)
		return
	}
	h, err := a.svc.Pin(r.Context(), id, req.Pinned)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, h)
}

func parseCursor(raw []string) (*int64, error) {
	if raw == nil {
		return nil, nil
	}
	if len(raw) != 1 || !digits(raw[0]) || len(raw[0]) > 18 {
		return nil, errors.New("bad cursor")
	}
	v, err := strconv.ParseInt(raw[0], 10, 64)
	if err != nil {
		return nil, err
	}
	return &v, nil
}

func digits(s string) bool {
	if s == "" {
		return false
	}
	for _, c := range s {
		if c < '0' || c > '9' {
			return false
		}
	}
	return true
}

func (a *API) history(w http.ResponseWriter, r *http.Request, dev store.Device) {
	q := r.URL.Query()
	before, err := parseCursor(q["before"])
	if err != nil {
		writeError(w, apierr.InvalidRequest("before must be a non-negative integer"))
		return
	}
	after, err := parseCursor(q["after"])
	if err != nil {
		writeError(w, apierr.InvalidRequest("after must be a non-negative integer"))
		return
	}
	if before != nil && after != nil {
		writeError(w, apierr.InvalidRequest("before and after are mutually exclusive"))
		return
	}
	limit := proto.HistoryDefaultLimit
	if raw, ok := q["limit"]; ok {
		if len(raw) != 1 || !digits(raw[0]) || len(raw[0]) > 3 {
			writeError(w, apierr.InvalidRequest("limit must be an integer between 1 and 500"))
			return
		}
		limit, _ = strconv.Atoi(raw[0])
		if limit < 1 || limit > proto.HistoryMaxLimit {
			writeError(w, apierr.InvalidRequest("limit must be an integer between 1 and 500"))
			return
		}
	}
	page, err := a.svc.History(r.Context(), before, after, limit)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, page)
}

func (a *API) historyIndex(w http.ResponseWriter, r *http.Request, dev store.Device) {
	idx, err := a.svc.Index(r.Context())
	if err != nil {
		a.fail(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, idx)
}

func (a *API) storage(w http.ResponseWriter, r *http.Request, dev store.Device) {
	st, err := a.svc.Storage(r.Context())
	if err != nil {
		a.fail(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, st)
}
