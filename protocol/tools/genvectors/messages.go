package main

import (
	"strconv"
	"unicode/utf8"
)

const (
	gib = int64(1024 * 1024 * 1024)
)

func itemVector(f *fixture, it *item, description string) obj {
	v := obj{
		{"name", it.Label},
		{"description", description},
		{"key_hex", hexs(f.Key)},
		{"id", it.ID},
		{"kind", it.Kind},
		{"size", it.size()},
		{"chunk_count", it.ChunkCount},
	}
	if it.ChunkCount == 0 {
		if it.Kind == "text" && utf8.Valid(it.Content) {
			v = append(v, kv{"content_utf8", string(it.Content)})
		}
		v = append(v, kv{"content_hex", hexs(it.Content)})
	} else {
		v = append(v, kv{"content_rule", "YCF1 archive of one file named \"pattern.bin\" whose data byte i = i mod 251; see files_archive.json case single_large_file_header"})
	}
	v = append(v,
		kv{"content_sha256", sha256Hex(it.Content)},
		kv{"content_hash", it.ContentHash},
		kv{"meta_plaintext_utf8", string(it.MetaJSON)},
		kv{"meta", it.Meta},
		kv{"meta_aad_utf8", string(aadMeta(it.ID))},
		kv{"meta_nonce_hex", hexs(it.MetaNonce)},
		kv{"meta_sealed_b64", b64(it.MetaSealed)},
	)
	if it.Payload != nil {
		v = append(v,
			kv{"payload_aad_utf8", string(aadPayload(it.ID))},
			kv{"payload_nonce_hex", hexs(it.PayloadNonce)},
			kv{"payload_sealed_b64", b64(it.Payload)},
		)
	}
	if it.Thumb != nil {
		v = append(v,
			kv{"thumb_plaintext_hex", hexs(it.ThumbPlain)},
			kv{"thumb_aad_utf8", string(aadThumb(it.ID))},
			kv{"thumb_nonce_hex", hexs(it.ThumbNonce)},
			kv{"thumb_sealed_b64", b64(it.Thumb)},
		)
	}
	if it.ChunkCount > 0 {
		var chunks []obj
		for n, c := range it.Chunks {
			start := int64(n) * chunkSizeBytes
			plain := it.Content[start : start+chunkPlainSize(it.size(), n, it.ChunkCount)]
			chunks = append(chunks, obj{
				{"index", n},
				{"aad_utf8", string(aadChunk(it.ID, n, it.ChunkCount))},
				{"nonce_hex", hexs(it.ChunkNonces[n])},
				{"plaintext_size", len(plain)},
				{"plaintext_sha256", sha256Hex(plain)},
				{"sealed_size", len(c)},
				{"sealed_sha256", sha256Hex(c)},
				{"sealed_prefix_hex", hexs(c[:44])},
				{"sealed_suffix_hex", hexs(c[len(c)-32:])},
			})
		}
		v = append(v, kv{"chunks", chunks})
	}
	v = append(v,
		kv{"stored_bytes", it.storedBytes()},
		kv{"header", it.header(true)},
	)
	return v
}

func itemsVectors(f *fixture) obj {
	return obj{
		{"description", "End-to-end items encrypted with the primary key from kdf.json. Decrypt meta, payload, thumb and chunks with the given AAD and compare with the plaintext; recompute content_hash and meta.sha256. Nonces are fixed here only for determinism."},
		{"items", []obj{
			itemVector(f, f.Text, "inline text item"),
			itemVector(f, f.Image, "inline image item (PNG) with a JPEG thumbnail"),
			itemVector(f, f.Files, "inline files item containing a YCF1 archive of three files, pinned"),
			itemVector(f, f.Large, "chunked files item of three chunks"),
		}},
	}
}

func errBody(code, message string) obj {
	return obj{{"code", code}, {"message", message}}
}

func authHeader(f *fixture) obj {
	return obj{{"Authorization", "Bearer " + f.Token}}
}

func jsonAuthHeader(f *fixture) obj {
	return obj{{"Authorization", "Bearer " + f.Token}, {"Content-Type", "application/json"}}
}

type httpEx struct {
	Name        string
	Method      string
	Path        string
	ReqHeaders  obj
	ReqBody     any
	ReqBodyNote string
	Status      int
	RespHeaders obj
	RespBody    any
	RespNote    string
}

func (h httpEx) json() obj {
	o := obj{
		{"name", h.Name},
		{"method", h.Method},
		{"path", h.Path},
	}
	if h.ReqHeaders != nil {
		o = append(o, kv{"request_headers", h.ReqHeaders})
	}
	if h.ReqBody != nil {
		o = append(o, kv{"request_body", h.ReqBody})
	}
	if h.ReqBodyNote != "" {
		o = append(o, kv{"request_body_note", h.ReqBodyNote})
	}
	o = append(o, kv{"status", h.Status})
	if h.RespHeaders != nil {
		o = append(o, kv{"response_headers", h.RespHeaders})
	}
	if h.RespBody != nil {
		o = append(o, kv{"response_body", h.RespBody})
	}
	if h.RespNote != "" {
		o = append(o, kv{"response_body_note", h.RespNote})
	}
	return o
}

func kdfObj() obj {
	return obj{{"algorithm", "pbkdf2-sha256"}, {"iterations", kdfIterations}, {"key_length", keyLength}}
}

func limitsObj() obj {
	return obj{
		{"inline_max_bytes", inlineMaxBytes},
		{"chunk_size_bytes", chunkSizeBytes},
		{"max_chunk_body_bytes", chunkSizeBytes + sealOverhead},
		{"thumb_max_bytes", 65536},
		{"meta_max_bytes", 65536},
		{"max_json_body_bytes", 1048576},
		{"max_files_per_item", 1000},
		{"history_default_limit", 100},
		{"history_max_limit", 500},
		{"max_ws_client_frame_bytes", 65536},
		{"max_ws_server_frame_bytes", 1048576},
		{"max_connections_per_device", 8},
		{"upload_ttl_seconds", 3600},
		{"disk_low_max_upload_bytes", 1048576},
	}
}

func storageObj(f *fixture, diskLow bool) obj {
	used := f.Text.storedBytes() + f.Image.storedBytes() + f.Files.storedBytes() + f.Large.storedBytes()
	free := int64(11) * gib
	if diskLow {
		free = 1717986918
	}
	return obj{
		{"used_bytes", used},
		{"limit_bytes", 5 * gib},
		{"pinned_bytes", f.Files.storedBytes()},
		{"pinned_limit_bytes", 1 * gib},
		{"item_count", 4},
		{"free_disk_bytes", free},
		{"min_free_disk_bytes", 2 * gib},
		{"retention_days", 7},
		{"disk_low", diskLow},
	}
}

func httpVectors(f *fixture) obj {
	newToken, _ := makeToken("android")
	pinnedFiles := *f.Files
	textPinned := *f.Text
	textPinned.Pinned = true
	ex := []httpEx{
		{
			Name: "login_new_device", Method: "POST", Path: "/api/login",
			ReqHeaders: obj{{"Content-Type", "application/json"}},
			ReqBody: obj{
				{"username", testUsername},
				{"password", testLoginPassword},
				{"device_name", f.Mac.Name},
				{"platform", "macos"},
			},
			Status: 200,
			RespBody: obj{
				{"device_id", f.Mac.ID},
				{"token", f.Token},
				{"username", testUsername},
				{"salt", b64(testSalt)},
				{"kdf", kdfObj()},
				{"key_check", nil},
				{"server_id", f.ServerID},
				{"server_version", serverVersion},
				{"protocol_version", protocolVersion},
			},
		},
		{
			Name: "login_existing_device", Method: "POST", Path: "/api/login",
			ReqHeaders: obj{{"Content-Type", "application/json"}},
			ReqBody: obj{
				{"username", testUsername},
				{"password", testLoginPassword},
				{"device_name", f.Android.Name},
				{"platform", "android"},
				{"device_id", f.Android.ID},
			},
			Status: 200,
			RespBody: obj{
				{"device_id", f.Android.ID},
				{"token", newToken},
				{"username", testUsername},
				{"salt", b64(testSalt)},
				{"kdf", kdfObj()},
				{"key_check", f.KeyCheck},
				{"server_id", f.ServerID},
				{"server_version", serverVersion},
				{"protocol_version", protocolVersion},
			},
		},
		{
			Name: "login_invalid_credentials", Method: "POST", Path: "/api/login",
			ReqHeaders: obj{{"Content-Type", "application/json"}},
			ReqBody:    obj{{"username", testUsername}, {"password", "wrong"}, {"device_name", "Pixel 9"}, {"platform", "android"}},
			Status:     401,
			RespBody:   errBody("invalid_credentials", "invalid username or password"),
		},
		{
			Name: "login_invalid_platform", Method: "POST", Path: "/api/login",
			ReqHeaders: obj{{"Content-Type", "application/json"}},
			ReqBody:    obj{{"username", testUsername}, {"password", testLoginPassword}, {"device_name", "iPhone"}, {"platform", "ios"}},
			Status:     400,
			RespBody:   errBody("invalid_request", "platform must be one of macos, android, windows, web"),
		},
		{
			Name: "login_rate_limited", Method: "POST", Path: "/api/login",
			ReqHeaders:  obj{{"Content-Type", "application/json"}},
			ReqBody:     obj{{"username", testUsername}, {"password", "wrong"}, {"device_name", "Pixel 9"}, {"platform", "android"}},
			Status:      429,
			RespHeaders: obj{{"Retry-After", "600"}},
			RespBody:    errBody("rate_limited", "too many failed login attempts"),
		},
		{
			Name: "me", Method: "GET", Path: "/api/me",
			ReqHeaders: authHeader(f),
			Status:     200,
			RespBody: obj{
				{"username", testUsername},
				{"device", f.Mac.json(true)},
				{"salt", b64(testSalt)},
				{"kdf", kdfObj()},
				{"key_check", f.KeyCheck},
				{"server_id", f.ServerID},
				{"server_version", serverVersion},
				{"protocol_version", protocolVersion},
				{"limits", limitsObj()},
			},
		},
		{
			Name: "me_unauthorized", Method: "GET", Path: "/api/me",
			ReqHeaders: obj{{"Authorization", "Bearer yc_invalidtokeninvalidtokeninvalidtokeninval"}},
			Status:     401,
			RespBody:   errBody("unauthorized", "missing, invalid or revoked token"),
		},
		{
			Name: "logout", Method: "POST", Path: "/api/logout",
			ReqHeaders: authHeader(f),
			Status:     204,
		},
		{
			Name: "key_check_set", Method: "PUT", Path: "/api/account/key-check",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    obj{{"key_check", f.KeyCheck}},
			Status:     204,
		},
		{
			Name: "key_check_conflict", Method: "PUT", Path: "/api/account/key-check",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    obj{{"key_check", keyCheck(deriveKey("another password", testSalt, fastKDFIterations))}},
			Status:     409,
			RespBody:   errBody("key_check_exists", "a different key check is already stored"),
		},
		{
			Name: "key_check_reset_refused", Method: "DELETE", Path: "/api/account/key-check",
			ReqHeaders: authHeader(f),
			Status:     409,
			RespBody:   errBody("items_exist", "delete all items before resetting the key check"),
		},
		{
			Name: "devices_list", Method: "GET", Path: "/api/devices",
			ReqHeaders: authHeader(f),
			Status:     200,
			RespBody: obj{{"devices", []obj{
				f.Mac.json(true),
				f.Android.json(false),
				f.Windows.json(false),
				f.Web.json(false),
			}}},
		},
		{
			Name: "device_rename", Method: "PATCH", Path: "/api/devices/" + f.Windows.ID,
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    obj{{"name", "Gaming PC"}},
			Status:     200,
			RespBody:   device{ID: f.Windows.ID, Name: "Gaming PC", Platform: "windows", CreatedAt: f.Windows.CreatedAt, LastSeenAt: f.Windows.LastSeenAt}.json(false),
		},
		{
			Name: "device_revoke", Method: "DELETE", Path: "/api/devices/" + f.Windows.ID,
			ReqHeaders: authHeader(f),
			Status:     204,
		},
		{
			Name: "device_not_found", Method: "DELETE", Path: "/api/devices/01926f3c-8d2a-7b3e-9f10-0123456789ab",
			ReqHeaders: authHeader(f),
			Status:     404,
			RespBody:   errBody("not_found", "device not found"),
		},
		{
			Name: "item_create_inline", Method: "POST", Path: "/api/items",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    f.Text.createRequest(),
			Status:     201,
			RespBody:   f.Text.header(false),
		},
		{
			Name: "item_create_inline_with_thumb_uploaded_first", Method: "POST", Path: "/api/items",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    f.Image.createRequest(),
			Status:     201,
			RespBody:   f.Image.header(false),
		},
		{
			Name: "item_create_repeat_is_idempotent", Method: "POST", Path: "/api/items",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    f.Text.createRequest(),
			Status:     200,
			RespBody:   f.Text.header(false),
		},
		{
			Name: "item_create_id_conflict", Method: "POST", Path: "/api/items",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    f.Image.createRequest().without("id").with(kv{"id", f.Text.ID}),
			Status:     409,
			RespBody:   errBody("id_conflict", "an item with this id already exists with different content"),
		},
		{
			Name: "item_create_payload_size_mismatch", Method: "POST", Path: "/api/items",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    f.Text.createRequest().without("size").with(kv{"size", f.Text.size() + 1}),
			Status:     400,
			RespBody:   errBody("size_mismatch", "payload length must equal size + 28"),
		},
		{
			Name: "item_create_key_check_missing", Method: "POST", Path: "/api/items",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    f.Text.createRequest(),
			Status:     409,
			RespBody:   errBody("key_check_missing", "set the key check before uploading items"),
		},
		{
			Name: "thumb_upload", Method: "PUT", Path: "/api/items/" + f.Image.ID + "/thumb",
			ReqHeaders:  authHeader(f).with(kv{"Content-Type", "application/octet-stream"}),
			ReqBody:     b64(f.Image.Thumb),
			ReqBodyNote: "raw sealed bytes; shown here as base64 only for readability (see items.json image thumb_sealed_b64)",
			Status:      204,
		},
		{
			Name: "chunk_upload", Method: "PUT", Path: "/api/items/" + f.Large.ID + "/chunks/0",
			ReqHeaders:  authHeader(f).with(kv{"Content-Type", "application/octet-stream"}),
			ReqBodyNote: "raw sealed chunk bytes (4194332 bytes for a full chunk); see items.json large chunks[0]",
			Status:      204,
		},
		{
			Name: "chunk_upload_too_large", Method: "PUT", Path: "/api/items/" + f.Large.ID + "/chunks/0",
			ReqHeaders:  authHeader(f).with(kv{"Content-Type", "application/octet-stream"}),
			ReqBodyNote: "4194333 bytes",
			Status:      413,
			RespBody:    errBody("body_too_large", "chunk body must be at most 4194332 bytes"),
		},
		{
			Name: "chunk_upload_disk_low", Method: "PUT", Path: "/api/items/" + f.Large.ID + "/chunks/0",
			ReqHeaders:  authHeader(f).with(kv{"Content-Type", "application/octet-stream"}),
			ReqBodyNote: "4194332 bytes",
			Status:      507,
			RespBody:    errBody("disk_low", "server disk is low; uploads larger than 1 MiB are rejected"),
		},
		{
			Name: "chunk_upload_after_commit", Method: "PUT", Path: "/api/items/" + f.Text.ID + "/chunks/0",
			ReqHeaders:  authHeader(f).with(kv{"Content-Type", "application/octet-stream"}),
			ReqBodyNote: "any bytes",
			Status:      409,
			RespBody:    errBody("already_committed", "item is already committed"),
		},
		{
			Name: "commit", Method: "POST", Path: "/api/items/" + f.Large.ID + "/commit",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    f.Large.commitRequest(),
			Status:     201,
			RespBody:   f.Large.header(false),
		},
		{
			Name: "commit_missing_chunks", Method: "POST", Path: "/api/items/" + f.Large.ID + "/commit",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    f.Large.commitRequest(),
			Status:     409,
			RespBody:   errBody("missing_chunks", "not all chunks were uploaded").with(kv{"details", obj{{"missing", []int{1, 2}}}}),
		},
		{
			Name: "commit_chunk_size_mismatch", Method: "POST", Path: "/api/items/" + f.Large.ID + "/commit",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    f.Large.commitRequest(),
			Status:     400,
			RespBody:   errBody("size_mismatch", "chunk 2 has "+strconv.Itoa(len(f.Large.Chunks[2])-3)+" bytes, expected "+strconv.Itoa(len(f.Large.Chunks[2]))).with(kv{"details", obj{{"index", 2}, {"expected", len(f.Large.Chunks[2])}, {"actual", len(f.Large.Chunks[2]) - 3}}}),
		},
		{
			Name: "commit_item_too_large", Method: "POST", Path: "/api/items/" + f.Large.ID + "/commit",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    f.Large.commitRequest(),
			Status:     413,
			RespBody:   errBody("item_too_large", "item does not fit in storage even after retention"),
		},
		{
			Name: "item_get_inline", Method: "GET", Path: "/api/items/" + f.Text.ID,
			ReqHeaders: authHeader(f),
			Status:     200,
			RespBody:   f.Text.header(true),
		},
		{
			Name: "item_get_chunked", Method: "GET", Path: "/api/items/" + f.Large.ID,
			ReqHeaders: authHeader(f),
			Status:     200,
			RespBody:   f.Large.header(true),
		},
		{
			Name: "item_get_not_found", Method: "GET", Path: "/api/items/01926f3c-8d2a-7b3e-9f10-0123456789ab",
			ReqHeaders: authHeader(f),
			Status:     404,
			RespBody:   errBody("not_found", "item not found"),
		},
		{
			Name: "item_get_invalid_id", Method: "GET", Path: "/api/items/not-a-uuid",
			ReqHeaders: authHeader(f),
			Status:     400,
			RespBody:   errBody("invalid_id", "id must be a lowercase UUIDv7"),
		},
		{
			Name: "chunk_download", Method: "GET", Path: "/api/items/" + f.Large.ID + "/chunks/2",
			ReqHeaders:  authHeader(f),
			Status:      200,
			RespHeaders: obj{{"Content-Type", "application/octet-stream"}, {"Content-Length", strconv.FormatInt(int64(len(f.Large.Chunks[2])), 10)}},
			RespNote:    "raw sealed chunk bytes; see items.json large chunks[2]",
		},
		{
			Name: "thumb_download", Method: "GET", Path: "/api/items/" + f.Image.ID + "/thumb",
			ReqHeaders:  authHeader(f),
			Status:      200,
			RespHeaders: obj{{"Content-Type", "application/octet-stream"}, {"Content-Length", strconv.FormatInt(int64(len(f.Image.Thumb)), 10)}},
			RespBody:    b64(f.Image.Thumb),
			RespNote:    "raw sealed bytes; shown here as base64 only for readability",
		},
		{
			Name: "history_newest", Method: "GET", Path: "/api/history?limit=3",
			ReqHeaders: authHeader(f),
			Status:     200,
			RespBody: obj{
				{"items", []obj{f.Large.header(false), f.Files.header(false), f.Image.header(false)}},
				{"has_more", true},
			},
		},
		{
			Name: "history_before", Method: "GET", Path: "/api/history?before=42&limit=100",
			ReqHeaders: authHeader(f),
			Status:     200,
			RespBody: obj{
				{"items", []obj{f.Text.header(false)}},
				{"has_more", false},
			},
		},
		{
			Name: "history_after_catch_up", Method: "GET", Path: "/api/history?after=40",
			ReqHeaders: authHeader(f),
			Status:     200,
			RespBody: obj{
				{"items", []obj{f.Text.header(false), f.Image.header(false), f.Files.header(false), f.Large.header(false)}},
				{"has_more", false},
			},
		},
		{
			Name: "history_bad_params", Method: "GET", Path: "/api/history?before=10&after=5",
			ReqHeaders: authHeader(f),
			Status:     400,
			RespBody:   errBody("invalid_request", "before and after are mutually exclusive"),
		},
		{
			Name: "history_index", Method: "GET", Path: "/api/history/index",
			ReqHeaders: authHeader(f),
			Status:     200,
			RespBody: obj{
				{"current_seq", f.CurrentSeq},
				{"state_rev", f.StateRev},
				{"items", []obj{
					{{"id", f.Text.ID}, {"seq", f.Text.Seq}, {"pinned", f.Text.Pinned}},
					{{"id", f.Image.ID}, {"seq", f.Image.Seq}, {"pinned", f.Image.Pinned}},
					{{"id", f.Files.ID}, {"seq", f.Files.Seq}, {"pinned", f.Files.Pinned}},
					{{"id", f.Large.ID}, {"seq", f.Large.Seq}, {"pinned", f.Large.Pinned}},
				}},
			},
		},
		{
			Name: "pin", Method: "POST", Path: "/api/items/" + f.Text.ID + "/pin",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    obj{{"pinned", true}},
			Status:     200,
			RespBody:   textPinned.header(false),
		},
		{
			Name: "unpin", Method: "POST", Path: "/api/items/" + f.Files.ID + "/pin",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    obj{{"pinned", false}},
			Status:     200,
			RespBody:   func() obj { pinnedFiles.Pinned = false; return pinnedFiles.header(false) }(),
		},
		{
			Name: "pin_limit", Method: "POST", Path: "/api/items/" + f.Large.ID + "/pin",
			ReqHeaders: jsonAuthHeader(f),
			ReqBody:    obj{{"pinned", true}},
			Status:     409,
			RespBody:   errBody("pinned_limit", "pinning this item would exceed the pinned storage limit"),
		},
		{
			Name: "item_delete", Method: "DELETE", Path: "/api/items/" + f.Image.ID,
			ReqHeaders: authHeader(f),
			Status:     204,
		},
		{
			Name: "storage", Method: "GET", Path: "/api/storage",
			ReqHeaders: authHeader(f),
			Status:     200,
			RespBody:   storageObj(f, false),
		},
		{
			Name: "healthz", Method: "GET", Path: "/healthz",
			Status:   200,
			RespBody: obj{{"status", "ok"}, {"server_version", serverVersion}, {"protocol_version", protocolVersion}},
		},
		{
			Name: "ws_upgrade_unauthorized", Method: "GET", Path: "/ws?token=yc_invalidtokeninvalidtokeninvalidtokeninval",
			ReqHeaders: obj{{"Connection", "Upgrade"}, {"Upgrade", "websocket"}, {"Sec-WebSocket-Version", "13"}},
			Status:     401,
			RespBody:   errBody("unauthorized", "missing, invalid or revoked token"),
		},
		{
			Name: "unknown_route", Method: "GET", Path: "/api/nope",
			ReqHeaders: authHeader(f),
			Status:     404,
			RespBody:   errBody("not_found", "route not found"),
		},
		{
			Name: "method_not_allowed", Method: "PUT", Path: "/api/history",
			ReqHeaders: authHeader(f),
			Status:     405,
			RespBody:   errBody("method_not_allowed", "method not allowed"),
		},
	}
	var out []obj
	for _, e := range ex {
		out = append(out, e.json())
	}
	return obj{
		{"description", "Canonical HTTP request/response examples. Values are consistent with kdf.json primary key and items.json; meta, payload and thumb fields decrypt with that key. Field order is not significant. Clients MUST ignore unknown response fields."},
		{"examples", out},
	}
}

func wsVectors(f *fixture) obj {
	ms := f.ServerTime.UnixMilli()
	type wsEx struct {
		name, direction string
		msg             obj
	}
	ex := []wsEx{
		{"hello", "client_to_server", obj{
			{"type", "hello"},
			{"protocol_version", protocolVersion},
			{"device_id", f.Mac.ID},
			{"last_seq", 40},
			{"app_version", "1.0.0"},
			{"platform", "macos"},
		}},
		{"welcome", "server_to_client", obj{
			{"type", "welcome"},
			{"protocol_version", protocolVersion},
			{"server_version", serverVersion},
			{"server_id", f.ServerID},
			{"server_time", rfc3339ms(f.ServerTime)},
			{"device_id", f.Mac.ID},
			{"current_seq", f.CurrentSeq},
			{"state_rev", f.StateRev},
			{"online_devices", []obj{f.Mac.presence(), f.Android.presence()}},
		}},
		{"presence_online", "server_to_client", f.Windows.presence().with(kv{"online", true}).withType("presence")},
		{"presence_offline", "server_to_client", f.Windows.presence().with(kv{"online", false}).withType("presence")},
		{"devices_changed", "server_to_client", obj{{"type", "devices_changed"}}},
		{"clip_inline_text", "server_to_client", obj{{"type", "clip"}, {"item", f.Text.header(true)}}},
		{"clip_inline_image", "server_to_client", obj{{"type", "clip"}, {"item", f.Image.header(true)}}},
		{"clip_chunked", "server_to_client", obj{{"type", "clip"}, {"item", f.Large.header(true)}}},
		{"clip_deleted_user", "server_to_client", obj{{"type", "clip_deleted"}, {"ids", []string{f.Image.ID}}, {"reason", "user"}, {"state_rev", f.StateRev + 1}}},
		{"clip_deleted_retention", "server_to_client", obj{{"type", "clip_deleted"}, {"ids", []string{f.Text.ID, f.Image.ID}}, {"reason", "retention"}, {"state_rev", f.StateRev + 2}}},
		{"clip_deleted_dedupe", "server_to_client", obj{{"type", "clip_deleted"}, {"ids", []string{f.Text.ID}}, {"reason", "dedupe"}, {"state_rev", f.StateRev + 3}}},
		{"clip_pinned", "server_to_client", obj{{"type", "clip_pinned"}, {"id", f.Text.ID}, {"pinned", true}, {"state_rev", f.StateRev + 4}}},
		{"storage_warning_active", "server_to_client", obj{
			{"type", "storage_warning"},
			{"active", true},
			{"reason", "disk_low"},
			{"free_disk_bytes", 1717986918},
			{"min_free_disk_bytes", 2 * gib},
		}},
		{"storage_warning_cleared", "server_to_client", obj{
			{"type", "storage_warning"},
			{"active", false},
			{"reason", "disk_low"},
			{"free_disk_bytes", 3 * gib},
			{"min_free_disk_bytes", 2 * gib},
		}},
		{"ping_from_client", "client_to_server", obj{{"type", "ping"}, {"ts", ms}}},
		{"pong_from_server", "server_to_client", obj{{"type", "pong"}, {"ts", ms}}},
		{"ping_from_server", "server_to_client", obj{{"type", "ping"}, {"ts", ms + 20000}}},
		{"pong_from_client", "client_to_server", obj{{"type", "pong"}, {"ts", ms + 20000}}},
		{"error_protocol_version", "server_to_client", obj{{"type", "error"}, {"code", "protocol_version_unsupported"}, {"message", "server supports protocol_version 1"}, {"details", obj{{"supported", []int{1}}}}}},
		{"error_device_mismatch", "server_to_client", obj{{"type", "error"}, {"code", "device_mismatch"}, {"message", "device_id does not match the token"}}},
		{"error_unknown_type", "server_to_client", obj{{"type", "error"}, {"code", "unknown_type"}, {"message", "unknown message type: foo"}}},
		{"error_invalid_message", "server_to_client", obj{{"type", "error"}, {"code", "invalid_message"}, {"message", "frame is not a JSON object with a string type field"}}},
		{"error_not_ready", "server_to_client", obj{{"type", "error"}, {"code", "hello_required"}, {"message", "the first message must be hello"}}},
	}
	var out []obj
	for _, e := range ex {
		out = append(out, obj{{"name", e.name}, {"direction", e.direction}, {"message", e.msg}})
	}
	return obj{
		{"description", "Canonical WebSocket text frames (one JSON object per frame). Field order is not significant. Receivers MUST ignore unknown fields and MUST ignore messages whose type they do not know (servers answer those with error unknown_type)."},
		{"close_codes", []obj{
			{{"code", 1000}, {"meaning", "normal close"}, {"client_action", "reconnect with backoff unless the app is quitting or logging out"}},
			{{"code", 1001}, {"meaning", "server shutting down"}, {"client_action", "reconnect with backoff"}},
			{{"code", 4001}, {"meaning", "unauthorized: token revoked or device_mismatch"}, {"client_action", "do not reconnect; require login"}},
			{{"code", 4002}, {"meaning", "closed because the same device opened more than 8 concurrent connections (the oldest is closed)"}, {"client_action", "do not reconnect automatically; reconnect on explicit user action or when the app returns to the foreground"}},
			{{"code", 4003}, {"meaning", "protocol_version_unsupported"}, {"client_action", "do not reconnect; tell the user to update"}},
			{{"code", 4004}, {"meaning", "hello not received within 10 s, or invalid first message"}, {"client_action", "reconnect with backoff"}},
			{{"code", 4005}, {"meaning", "heartbeat timeout (45 s without any message)"}, {"client_action", "reconnect with backoff"}},
		}},
		{"messages", out},
	}
}

func (o obj) withType(t string) obj {
	return append(obj{{"type", t}}, o...)
}
