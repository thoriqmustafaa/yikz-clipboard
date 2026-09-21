package service

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"strconv"
	"time"

	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/apierr"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/blob"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/proto"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/retention"
	"github.com/thoriqmustafaa/yikz-clipboard/server/internal/store"
)

type ItemRequest struct {
	ID          *string `json:"id"`
	Kind        *string `json:"kind"`
	Size        *int64  `json:"size"`
	ChunkCount  *int64  `json:"chunk_count"`
	ContentHash *string `json:"content_hash"`
	Meta        *string `json:"meta"`
	Payload     *string `json:"payload"`
}

type HistoryPage struct {
	Items   []proto.ItemHeader `json:"items"`
	HasMore bool               `json:"has_more"`
}

type IndexItem struct {
	ID     string `json:"id"`
	Seq    int64  `json:"seq"`
	Pinned bool   `json:"pinned"`
}

type IndexResponse struct {
	CurrentSeq int64       `json:"current_seq"`
	StateRev   int64       `json:"state_rev"`
	Items      []IndexItem `json:"items"`
}

type StorageResponse struct {
	UsedBytes        int64 `json:"used_bytes"`
	LimitBytes       int64 `json:"limit_bytes"`
	PinnedBytes      int64 `json:"pinned_bytes"`
	PinnedLimitBytes int64 `json:"pinned_limit_bytes"`
	ItemCount        int64 `json:"item_count"`
	FreeDiskBytes    int64 `json:"free_disk_bytes"`
	MinFreeDiskBytes int64 `json:"min_free_disk_bytes"`
	RetentionDays    int   `json:"retention_days"`
	DiskLow          bool  `json:"disk_low"`
}

func Header(it store.Item, withPayload bool) proto.ItemHeader {
	h := proto.ItemHeader{
		ID:          it.ID,
		Seq:         it.Seq,
		DeviceID:    it.DeviceID,
		Kind:        it.Kind,
		Size:        it.Size,
		ChunkCount:  it.ChunkCount,
		CreatedAt:   proto.Time{Time: it.CreatedAt},
		Pinned:      it.Pinned,
		ContentHash: it.ContentHash,
		HasThumb:    it.HasThumb,
		StoredBytes: it.StoredBytes,
		Meta:        proto.EncodeBase64(it.Meta),
	}
	if withPayload && it.Payload != nil {
		h.Payload = proto.EncodeBase64(it.Payload)
	}
	return h
}

func (s *Service) requireKeyCheck(ctx context.Context) error {
	acct, err := s.store.Account(ctx)
	if err != nil {
		return err
	}
	if acct.KeyCheck == nil {
		return apierr.KeyCheckMissing()
	}
	return nil
}

type validated struct {
	kind        string
	size        int64
	chunkCount  int
	contentHash string
	meta        []byte
}

func validateCommon(req ItemRequest) (validated, error) {
	var v validated
	if req.Kind == nil || !proto.ValidKind(*req.Kind) {
		return v, apierr.InvalidRequest("kind must be one of text, image, files")
	}
	if req.ContentHash == nil || !proto.ValidHex64(*req.ContentHash) {
		return v, apierr.InvalidRequest("content_hash must be 64 lowercase hex characters")
	}
	if req.Size == nil || *req.Size < 1 {
		return v, apierr.InvalidRequest("size must be a positive integer")
	}
	if req.ChunkCount == nil || *req.ChunkCount < 0 {
		return v, apierr.InvalidRequest("chunk_count must be a non-negative integer")
	}
	if req.Meta == nil {
		return v, apierr.InvalidRequest("meta is required")
	}
	v.kind, v.contentHash, v.size = *req.Kind, *req.ContentHash, *req.Size
	return v, nil
}

func decodeMeta(req ItemRequest) ([]byte, error) {
	meta, ok := proto.DecodeBase64(*req.Meta)
	if !ok {
		return nil, apierr.InvalidRequest("meta must be valid base64")
	}
	if len(meta) < proto.SealOverhead {
		return nil, apierr.InvalidRequest("meta is shorter than a sealed box")
	}
	if len(meta) > proto.MetaMaxBytes {
		return nil, apierr.BodyTooLarge("meta must be at most 65536 bytes")
	}
	return meta, nil
}

func sameItem(it store.Item, v validated) bool {
	return it.Kind == v.kind && it.Size == v.size && it.ChunkCount == v.chunkCount && it.ContentHash == v.contentHash
}

func (s *Service) CreateInline(ctx context.Context, caller store.Device, req ItemRequest) (proto.ItemHeader, bool, error) {
	if req.ID == nil || !proto.ValidID(*req.ID) {
		return proto.ItemHeader{}, false, apierr.InvalidID()
	}
	id := *req.ID
	v, err := validateCommon(req)
	if err != nil {
		return proto.ItemHeader{}, false, err
	}
	if *req.ChunkCount != 0 {
		return proto.ItemHeader{}, false, apierr.InvalidRequest("chunk_count must be 0 for inline items")
	}
	if v.size > proto.InlineMaxBytes {
		return proto.ItemHeader{}, false, apierr.InvalidRequest("size must be at most 262144 for inline items")
	}
	if v.meta, err = decodeMeta(req); err != nil {
		return proto.ItemHeader{}, false, err
	}
	if req.Payload == nil {
		return proto.ItemHeader{}, false, apierr.InvalidRequest("payload is required")
	}
	payload, ok := proto.DecodeBase64(*req.Payload)
	if !ok {
		return proto.ItemHeader{}, false, apierr.InvalidRequest("payload must be valid base64")
	}
	if int64(len(payload)) != v.size+proto.SealOverhead {
		return proto.ItemHeader{}, false, apierr.New(http.StatusBadRequest, apierr.CodeSizeMismatch, "payload length must equal size + 28")
	}
	if err := s.requireKeyCheck(ctx); err != nil {
		return proto.ItemHeader{}, false, err
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	existing, err := s.store.Item(ctx, id, false)
	switch {
	case err == nil:
		if sameItem(existing, v) {
			return Header(existing, false), false, nil
		}
		return proto.ItemHeader{}, false, apierr.IDConflict()
	case !errors.Is(err, store.ErrNotFound):
		return proto.ItemHeader{}, false, err
	}
	pending, err := s.store.Pending(ctx, id)
	hasThumb := false
	var thumbSize int64
	switch {
	case err == nil:
		if len(pending.Chunks) > 0 {
			return proto.ItemHeader{}, false, apierr.InvalidRequest("chunks were uploaded for this id; commit it instead")
		}
		hasThumb, thumbSize = pending.HasThumb, pending.ThumbSize
	case !errors.Is(err, store.ErrNotFound):
		return proto.ItemHeader{}, false, err
	}
	it := store.Item{
		ID:          id,
		DeviceID:    caller.ID,
		Kind:        v.kind,
		Size:        v.size,
		ChunkCount:  0,
		CreatedAt:   s.clock.Now(),
		ContentHash: v.contentHash,
		HasThumb:    hasThumb,
		StoredBytes: int64(len(v.meta)) + int64(len(payload)) + thumbSize,
		Meta:        v.meta,
		Payload:     payload,
	}
	return s.finishCommitLocked(ctx, it)
}

func (s *Service) finishCommitLocked(ctx context.Context, it store.Item) (proto.ItemHeader, bool, error) {
	committed, err := s.store.CommitItem(ctx, it)
	if err != nil {
		return proto.ItemHeader{}, false, fmt.Errorf("commit item: %w", err)
	}
	s.events.Broadcast(proto.Clip{Type: "clip", Item: Header(committed, true)})
	s.log.Info("item committed", "id", committed.ID, "seq", committed.Seq, "device_id", committed.DeviceID, "kind", committed.Kind, "size", committed.Size, "chunk_count", committed.ChunkCount, "stored_bytes", committed.StoredBytes)
	if err := s.dedupeLocked(ctx, committed); err != nil {
		s.log.Error("dedupe failed", "id", committed.ID, "err", err)
	}
	if err := s.retentionLocked(ctx, committed.ID); err != nil {
		s.log.Error("retention after commit failed", "id", committed.ID, "err", err)
	}
	return Header(committed, false), true, nil
}

func (s *Service) dedupeLocked(ctx context.Context, it store.Item) error {
	ids, err := s.store.DuplicateIDs(ctx, it.ContentHash, it.ID)
	if err != nil || len(ids) == 0 {
		return err
	}
	return s.deleteLocked(ctx, ids, proto.DeleteReasonDedupe)
}

func (s *Service) deleteLocked(ctx context.Context, ids []string, reason string) error {
	for start := 0; start < len(ids); start += proto.MaxIDsPerDeleteMessage {
		end := min(start+proto.MaxIDsPerDeleteMessage, len(ids))
		deleted, rev, err := s.store.DeleteItems(ctx, ids[start:end])
		if err != nil {
			return err
		}
		if len(deleted) == 0 {
			continue
		}
		for _, id := range deleted {
			if err := s.blobs.RemoveItem(id); err != nil {
				s.log.Warn("remove item blobs", "id", id, "err", err)
			}
		}
		s.events.Broadcast(proto.ClipDeleted{Type: "clip_deleted", IDs: deleted, Reason: reason, StateRev: rev})
		s.log.Info("items deleted", "count", len(deleted), "reason", reason, "state_rev", rev)
	}
	return nil
}

func (s *Service) RunRetention(ctx context.Context, keepID string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.retentionLocked(ctx, keepID)
}

func (s *Service) retentionLocked(ctx context.Context, keepID string) error {
	now := s.clock.Now()
	expired, err := s.store.ExpiredPending(ctx, now.Add(-s.cfg.UploadTTL))
	if err != nil {
		return fmt.Errorf("list expired uploads: %w", err)
	}
	for _, id := range expired {
		if _, err := s.store.DeletePending(ctx, id); err != nil {
			return fmt.Errorf("delete expired upload: %w", err)
		}
		committed, err := s.store.IsCommitted(ctx, id)
		if err != nil {
			return err
		}
		if !committed {
			if err := s.blobs.RemoveItem(id); err != nil {
				s.log.Warn("remove expired upload blobs", "id", id, "err", err)
			}
		}
		s.log.Info("expired pending upload removed", "id", id)
	}
	items, err := s.store.Summaries(ctx)
	if err != nil {
		return fmt.Errorf("list items: %w", err)
	}
	policy := retention.Policy{MaxAge: s.retentionAge(), StorageMaxBytes: s.cfg.StorageMaxBytes}
	ids := retention.Plan(items, now, policy, keepID)
	if len(ids) == 0 {
		return nil
	}
	if err := s.deleteLocked(ctx, ids, proto.DeleteReasonRetention); err != nil {
		return err
	}
	if err := s.store.Vacuum(ctx); err != nil {
		s.log.Warn("incremental vacuum", "err", err)
	}
	return nil
}

func (s *Service) retentionAge() time.Duration {
	return time.Duration(s.cfg.RetentionDays) * 24 * time.Hour
}

func (s *Service) diskLowFor(size int64) bool {
	return size > proto.DiskLowMaxUploadBytes && s.disk.Status().Low
}

func ParseChunkIndex(raw string) (int, bool) {
	if raw == "" || len(raw) > 5 {
		return 0, false
	}
	for _, c := range raw {
		if c < '0' || c > '9' {
			return 0, false
		}
	}
	if len(raw) > 1 && raw[0] == '0' {
		return 0, false
	}
	n, err := strconv.Atoi(raw)
	if err != nil || n < 0 || n >= proto.MaxChunkIndex {
		return 0, false
	}
	return n, true
}

func (s *Service) checkUploadable(ctx context.Context, id string) error {
	if err := s.requireKeyCheck(ctx); err != nil {
		return err
	}
	committed, err := s.store.IsCommitted(ctx, id)
	if err != nil {
		return err
	}
	if committed {
		return apierr.AlreadyCommitted()
	}
	return nil
}

func (s *Service) PutChunk(ctx context.Context, caller store.Device, id string, index int, body io.Reader, contentLength int64) error {
	if err := s.checkUploadable(ctx, id); err != nil {
		return err
	}
	tooLarge := apierr.BodyTooLarge(fmt.Sprintf("chunk body must be at most %d bytes", proto.MaxChunkBodyBytes))
	if contentLength > proto.MaxChunkBodyBytes {
		return tooLarge
	}
	limit := int64(proto.MaxChunkBodyBytes)
	diskLow := s.disk.Status().Low
	if diskLow {
		if index >= 1 || contentLength > proto.DiskLowMaxChunkBody {
			return apierr.DiskLow()
		}
		limit = proto.DiskLowMaxChunkBody
	}
	if contentLength >= 0 {
		if err := s.checkPendingFits(ctx, id, index, contentLength); err != nil {
			return err
		}
	}
	if err := s.touchPending(ctx, id, caller.ID); err != nil {
		return err
	}
	tmp, err := s.blobs.WriteTemp(body, limit)
	if errors.Is(err, blob.ErrTooLarge) {
		if diskLow {
			return apierr.DiskLow()
		}
		return tooLarge
	}
	if err != nil {
		return uploadReadError(err)
	}
	defer s.blobs.Discard(tmp)
	if tmp.Size() < proto.SealOverhead {
		return apierr.InvalidRequest("chunk body must be at least 28 bytes")
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	committed, err := s.store.IsCommitted(ctx, id)
	if err != nil {
		return err
	}
	if committed {
		return apierr.AlreadyCommitted()
	}
	if err := s.checkPendingFits(ctx, id, index, tmp.Size()); err != nil {
		return err
	}
	if err := s.blobs.Install(tmp, id, blob.ChunkName(index)); err != nil {
		return err
	}
	return s.store.PutPendingChunk(ctx, id, caller.ID, index, tmp.Size(), s.clock.Now())
}

func (s *Service) checkPendingFits(ctx context.Context, id string, index int, size int64) error {
	p, err := s.store.Pending(ctx, id)
	if err != nil && !errors.Is(err, store.ErrNotFound) {
		return err
	}
	var total int64
	for idx, n := range p.Chunks {
		if idx != index {
			total += n
		}
	}
	total += p.ThumbSize + size
	if total > s.cfg.StorageMaxBytes {
		return apierr.ItemTooLarge()
	}
	return nil
}

func (s *Service) touchPending(ctx context.Context, id, deviceID string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	committed, err := s.store.IsCommitted(ctx, id)
	if err != nil {
		return err
	}
	if committed {
		return apierr.AlreadyCommitted()
	}
	return s.store.TouchPending(ctx, id, deviceID, s.clock.Now())
}

func uploadReadError(err error) error {
	var mbe *http.MaxBytesError
	if errors.As(err, &mbe) {
		return apierr.BodyTooLarge("request body too large")
	}
	return apierr.InvalidRequest("failed to read request body: " + err.Error())
}

func (s *Service) PutThumb(ctx context.Context, caller store.Device, id string, body io.Reader, contentLength int64) error {
	if err := s.checkUploadable(ctx, id); err != nil {
		return err
	}
	tooLarge := apierr.BodyTooLarge(fmt.Sprintf("thumbnail body must be at most %d bytes", proto.ThumbMaxBytes))
	if contentLength > proto.ThumbMaxBytes {
		return tooLarge
	}
	if err := s.touchPending(ctx, id, caller.ID); err != nil {
		return err
	}
	tmp, err := s.blobs.WriteTemp(body, proto.ThumbMaxBytes)
	if errors.Is(err, blob.ErrTooLarge) {
		return tooLarge
	}
	if err != nil {
		return uploadReadError(err)
	}
	defer s.blobs.Discard(tmp)
	if tmp.Size() < proto.SealOverhead {
		return apierr.InvalidRequest("thumbnail body must be at least 28 bytes")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	committed, err := s.store.IsCommitted(ctx, id)
	if err != nil {
		return err
	}
	if committed {
		return apierr.AlreadyCommitted()
	}
	if err := s.blobs.Install(tmp, id, blob.ThumbName); err != nil {
		return err
	}
	return s.store.PutPendingThumb(ctx, id, caller.ID, tmp.Size(), s.clock.Now())
}

func (s *Service) Commit(ctx context.Context, caller store.Device, id string, req ItemRequest) (proto.ItemHeader, bool, error) {
	v, err := validateCommon(req)
	if err != nil {
		return proto.ItemHeader{}, false, err
	}
	if v.meta, err = decodeMeta(req); err != nil {
		return proto.ItemHeader{}, false, err
	}
	if v.size <= proto.InlineMaxBytes {
		return proto.ItemHeader{}, false, apierr.InvalidRequest("size must be greater than 262144 for chunked items")
	}
	expected := proto.ChunkCount(v.size)
	if *req.ChunkCount != int64(expected) {
		return proto.ItemHeader{}, false, apierr.New(http.StatusBadRequest, apierr.CodeSizeMismatch, fmt.Sprintf("chunk_count must be %d for size %d", expected, v.size))
	}
	v.chunkCount = expected
	if err := s.requireKeyCheck(ctx); err != nil {
		return proto.ItemHeader{}, false, err
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	existing, err := s.store.Item(ctx, id, false)
	switch {
	case err == nil:
		if sameItem(existing, v) {
			return Header(existing, false), false, nil
		}
		return proto.ItemHeader{}, false, apierr.IDConflict()
	case !errors.Is(err, store.ErrNotFound):
		return proto.ItemHeader{}, false, err
	}
	pending, err := s.store.Pending(ctx, id)
	if err != nil && !errors.Is(err, store.ErrNotFound) {
		return proto.ItemHeader{}, false, err
	}
	var missing []int
	total := 0
	for i := 0; i < expected; i++ {
		if _, ok := pending.Chunks[i]; !ok {
			total++
			if len(missing) < proto.MissingChunksMaxListed {
				missing = append(missing, i)
			}
		}
	}
	if total > 0 {
		return proto.ItemHeader{}, false, apierr.New(http.StatusConflict, apierr.CodeMissingChunks, "not all chunks were uploaded").
			WithDetails(map[string]any{"missing": missing})
	}
	var chunkBytes int64
	for i := 0; i < expected; i++ {
		want := proto.ChunkSealedSize(v.size, i, expected)
		got := pending.Chunks[i]
		if got != want {
			return proto.ItemHeader{}, false, apierr.New(http.StatusBadRequest, apierr.CodeSizeMismatch, fmt.Sprintf("chunk %d has %d bytes, expected %d", i, got, want)).
				WithDetails(map[string]any{"index": i, "expected": want, "actual": got})
		}
		chunkBytes += got
	}
	if s.diskLowFor(v.size) {
		return proto.ItemHeader{}, false, apierr.DiskLow()
	}
	stored := int64(len(v.meta)) + pending.ThumbSize + chunkBytes
	usage, err := s.store.Usage(ctx)
	if err != nil {
		return proto.ItemHeader{}, false, err
	}
	if stored > s.cfg.StorageMaxBytes-usage.PinnedBytes {
		return proto.ItemHeader{}, false, apierr.ItemTooLarge()
	}
	extra, err := s.store.DeletePendingChunksFrom(ctx, id, expected)
	if err != nil {
		return proto.ItemHeader{}, false, err
	}
	for _, idx := range extra {
		if err := s.blobs.Remove(id, blob.ChunkName(idx)); err != nil {
			s.log.Warn("remove extra chunk", "id", id, "index", idx, "err", err)
		}
	}
	it := store.Item{
		ID:          id,
		DeviceID:    caller.ID,
		Kind:        v.kind,
		Size:        v.size,
		ChunkCount:  expected,
		CreatedAt:   s.clock.Now(),
		ContentHash: v.contentHash,
		HasThumb:    pending.HasThumb,
		StoredBytes: stored,
		Meta:        v.meta,
	}
	return s.finishCommitLocked(ctx, it)
}

func (s *Service) Item(ctx context.Context, id string) (proto.ItemHeader, error) {
	it, err := s.store.Item(ctx, id, true)
	if errors.Is(err, store.ErrNotFound) {
		return proto.ItemHeader{}, apierr.ItemNotFound()
	}
	if err != nil {
		return proto.ItemHeader{}, err
	}
	return Header(it, true), nil
}

func (s *Service) OpenChunk(ctx context.Context, id string, index int) (*os.File, int64, error) {
	it, err := s.store.Item(ctx, id, false)
	if errors.Is(err, store.ErrNotFound) {
		return nil, 0, apierr.ItemNotFound()
	}
	if err != nil {
		return nil, 0, err
	}
	if index >= it.ChunkCount {
		return nil, 0, apierr.NotFound("chunk not found")
	}
	f, size, err := s.blobs.Open(id, blob.ChunkName(index))
	if errors.Is(err, os.ErrNotExist) {
		return nil, 0, apierr.NotFound("chunk not found")
	}
	return f, size, err
}

func (s *Service) OpenThumb(ctx context.Context, id string) (*os.File, int64, error) {
	it, err := s.store.Item(ctx, id, false)
	if errors.Is(err, store.ErrNotFound) {
		return nil, 0, apierr.ItemNotFound()
	}
	if err != nil {
		return nil, 0, err
	}
	if !it.HasThumb {
		return nil, 0, apierr.NotFound("thumbnail not found")
	}
	f, size, err := s.blobs.Open(id, blob.ThumbName)
	if errors.Is(err, os.ErrNotExist) {
		return nil, 0, apierr.NotFound("thumbnail not found")
	}
	return f, size, err
}

func (s *Service) History(ctx context.Context, before, after *int64, limit int) (HistoryPage, error) {
	items, more, err := s.store.History(ctx, before, after, limit)
	if err != nil {
		return HistoryPage{}, err
	}
	page := HistoryPage{Items: make([]proto.ItemHeader, 0, len(items)), HasMore: more}
	for _, it := range items {
		page.Items = append(page.Items, Header(it, false))
	}
	return page, nil
}

func (s *Service) Index(ctx context.Context) (IndexResponse, error) {
	cur, rev, entries, err := s.store.Index(ctx)
	if err != nil {
		return IndexResponse{}, err
	}
	out := IndexResponse{CurrentSeq: cur, StateRev: rev, Items: make([]IndexItem, 0, len(entries))}
	for _, e := range entries {
		out.Items = append(out.Items, IndexItem{ID: e.ID, Seq: e.Seq, Pinned: e.Pinned})
	}
	return out, nil
}

func (s *Service) Pin(ctx context.Context, id string, pinned *bool) (proto.ItemHeader, error) {
	if pinned == nil {
		return proto.ItemHeader{}, apierr.InvalidRequest("pinned must be a boolean")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	it, err := s.store.Item(ctx, id, false)
	if errors.Is(err, store.ErrNotFound) {
		return proto.ItemHeader{}, apierr.ItemNotFound()
	}
	if err != nil {
		return proto.ItemHeader{}, err
	}
	if it.Pinned == *pinned {
		return Header(it, false), nil
	}
	if *pinned {
		usage, err := s.store.Usage(ctx)
		if err != nil {
			return proto.ItemHeader{}, err
		}
		if usage.PinnedBytes+it.StoredBytes > s.cfg.PinnedMaxBytes {
			return proto.ItemHeader{}, apierr.New(http.StatusConflict, apierr.CodePinnedLimit, "pinning this item would exceed the pinned storage limit")
		}
	}
	rev, err := s.store.SetPinned(ctx, id, *pinned)
	if err != nil {
		return proto.ItemHeader{}, err
	}
	it.Pinned = *pinned
	s.events.Broadcast(proto.ClipPinned{Type: "clip_pinned", ID: id, Pinned: *pinned, StateRev: rev})
	s.log.Info("item pin changed", "id", id, "pinned", *pinned, "state_rev", rev)
	return Header(it, false), nil
}

func (s *Service) Delete(ctx context.Context, id string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	committed, err := s.store.IsCommitted(ctx, id)
	if err != nil {
		return err
	}
	if committed {
		return s.deleteLocked(ctx, []string{id}, proto.DeleteReasonUser)
	}
	found, err := s.store.DeletePending(ctx, id)
	if err != nil {
		return err
	}
	if !found {
		return apierr.ItemNotFound()
	}
	if err := s.blobs.RemoveItem(id); err != nil {
		s.log.Warn("remove cancelled upload blobs", "id", id, "err", err)
	}
	s.log.Info("pending upload cancelled", "id", id)
	return nil
}

func (s *Service) Storage(ctx context.Context) (StorageResponse, error) {
	u, err := s.store.Usage(ctx)
	if err != nil {
		return StorageResponse{}, err
	}
	st := s.disk.Status()
	return StorageResponse{
		UsedBytes:        u.UsedBytes,
		LimitBytes:       s.cfg.StorageMaxBytes,
		PinnedBytes:      u.PinnedBytes,
		PinnedLimitBytes: s.cfg.PinnedMaxBytes,
		ItemCount:        u.ItemCount,
		FreeDiskBytes:    st.FreeBytes,
		MinFreeDiskBytes: s.cfg.MinFreeDiskBytes,
		RetentionDays:    s.cfg.RetentionDays,
		DiskLow:          st.Low,
	}, nil
}
