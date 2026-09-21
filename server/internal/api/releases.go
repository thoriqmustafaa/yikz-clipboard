package api

import (
	"crypto/sha256"
	"crypto/subtle"
	"errors"
	"net/http"
	"strconv"
	"strings"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/apierr"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/release"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/store"
)

type releaseAvailable struct {
	Type    string `json:"type"`
	Version string `json:"version"`
}

func bearer(r *http.Request) string {
	scheme, tok, ok := strings.Cut(r.Header.Get("Authorization"), " ")
	if !ok || !strings.EqualFold(scheme, "Bearer") {
		return ""
	}
	return strings.TrimSpace(tok)
}

func tokenEqual(got, want string) bool {
	a := sha256.Sum256([]byte(got))
	b := sha256.Sum256([]byte(want))
	return subtle.ConstantTimeCompare(a[:], b[:]) == 1
}

func (a *API) admin(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if a.releaseToken == "" || a.releases == nil {
			routeNotFound(w, r)
			return
		}
		tok := bearer(r)
		if tok == "" || !tokenEqual(tok, a.releaseToken) {
			writeError(w, apierr.Unauthorized())
			return
		}
		next(w, r)
	}
}

func (a *API) releasesEnabled(w http.ResponseWriter, r *http.Request) bool {
	if a.releases == nil {
		routeNotFound(w, r)
		return false
	}
	return true
}

func (a *API) putReleaseAsset(w http.ResponseWriter, r *http.Request) {
	version, file := r.PathValue("version"), r.PathValue("file")
	if r.ContentLength > release.MaxAssetBytes {
		writeError(w, apierr.BodyTooLarge("release assets must be at most 1 GiB"))
		return
	}
	if err := a.releases.PutAsset(version, file, r.Body, r.ContentLength); err != nil {
		a.fail(w, r, err)
		return
	}
	a.log.Info("release asset uploaded", "version", version, "file", file)
	w.WriteHeader(http.StatusNoContent)
}

func (a *API) putReleaseManifest(w http.ResponseWriter, r *http.Request) {
	version := r.PathValue("version")
	var m release.Manifest
	if err := decodeJSON(w, r, &m); err != nil {
		a.fail(w, r, err)
		return
	}
	out, err := a.releases.Publish(version, m)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	a.log.Info("release published", "version", out.Version, "assets", len(out.Assets))
	if a.hub != nil {
		a.hub.Broadcast(releaseAvailable{Type: "release_available", Version: out.Version})
	}
	writeJSON(w, http.StatusCreated, out)
}

func (a *API) listReleases(w http.ResponseWriter, r *http.Request, _ store.Device) {
	if !a.releasesEnabled(w, r) {
		return
	}
	limit := 20
	if v := r.URL.Query().Get("limit"); v != "" {
		n, err := strconv.Atoi(v)
		if err != nil || n < 1 {
			writeError(w, apierr.InvalidRequest("limit must be a positive integer"))
			return
		}
		limit = min(n, 100)
	}
	list, err := a.releases.List(limit)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"releases": list})
}

func (a *API) latestRelease(w http.ResponseWriter, r *http.Request, _ store.Device) {
	if !a.releasesEnabled(w, r) {
		return
	}
	platform := r.URL.Query().Get("platform")
	if !release.ValidPlatform(platform) {
		writeError(w, apierr.InvalidRequest("platform must be one of "+strings.Join(release.Platforms, ", ")))
		return
	}
	latest, ok, err := a.releases.Latest(platform)
	if err != nil {
		a.fail(w, r, err)
		return
	}
	if !ok {
		w.Header().Set("Cache-Control", "no-store")
		w.WriteHeader(http.StatusNoContent)
		return
	}
	writeJSON(w, http.StatusOK, latest)
}

func (a *API) getReleaseAsset(w http.ResponseWriter, r *http.Request, _ store.Device) {
	if !a.releasesEnabled(w, r) {
		return
	}
	file := r.PathValue("file")
	f, st, err := a.releases.OpenAsset(r.PathValue("version"), file)
	if err != nil {
		var e *apierr.Error
		if errors.As(err, &e) {
			writeError(w, e)
			return
		}
		a.fail(w, r, err)
		return
	}
	defer f.Close()
	h := w.Header()
	h.Set("Content-Type", "application/octet-stream")
	h.Set("Content-Disposition", `attachment; filename="`+file+`"`)
	h.Set("Cache-Control", "private, no-cache")
	h.Set("X-Content-Type-Options", "nosniff")
	http.ServeContent(w, r, "", st.ModTime(), f)
}
