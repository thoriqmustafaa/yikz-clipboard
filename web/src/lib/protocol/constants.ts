export const PROTOCOL_VERSION = 1;
export const KDF_ALGORITHM = 'pbkdf2-sha256';
export const KDF_ITERATIONS = 600000;
export const KEY_LENGTH = 32;
export const SALT_LENGTH = 16;
export const NONCE_LENGTH = 12;
export const TAG_LENGTH = 16;
export const SEAL_OVERHEAD = NONCE_LENGTH + TAG_LENGTH;
export const PREVIEW_MAX_CODE_POINTS = 500;
export const THUMB_MAX_SIDE = 320;
export const HEARTBEAT_INTERVAL_MS = 20000;
export const DEAD_TIMEOUT_MS = 45000;
export const FOREGROUND_PROBE_MS = 5000;
export const BACKOFF_BASE_S = 0.5;
export const BACKOFF_MAX_S = 30;
export const AUTO_APPLY_MAX_AGE_MS = 300000;
export const DEFAULT_AUTO_DOWNLOAD_BYTES = 52428800;
export const CONTENT_HASH_LABEL = 'yikz-clipboard/v1/content-hash';
export const KEY_CHECK_LABEL = 'yikz-clipboard/v1/key-check';
export const MIME_TEXT = 'text/plain; charset=utf-8';
export const MIME_IMAGE = 'image/png';
export const MIME_FILES = 'application/x-yikz-files';

export interface Limits {
  inline_max_bytes: number;
  chunk_size_bytes: number;
  max_chunk_body_bytes: number;
  thumb_max_bytes: number;
  meta_max_bytes: number;
  max_json_body_bytes: number;
  max_files_per_item: number;
  history_default_limit: number;
  history_max_limit: number;
  max_ws_client_frame_bytes: number;
  max_ws_server_frame_bytes: number;
  max_connections_per_device: number;
  upload_ttl_seconds: number;
  disk_low_max_upload_bytes: number;
}

export const DEFAULT_LIMITS: Limits = {
  inline_max_bytes: 262144,
  chunk_size_bytes: 4194304,
  max_chunk_body_bytes: 4194332,
  thumb_max_bytes: 65536,
  meta_max_bytes: 65536,
  max_json_body_bytes: 1048576,
  max_files_per_item: 1000,
  history_default_limit: 100,
  history_max_limit: 500,
  max_ws_client_frame_bytes: 65536,
  max_ws_server_frame_bytes: 1048576,
  max_connections_per_device: 8,
  upload_ttl_seconds: 3600,
  disk_low_max_upload_bytes: 1048576
};

export function mergeLimits(partial: Partial<Limits> | undefined | null): Limits {
  const out: Limits = { ...DEFAULT_LIMITS };
  if (!partial || typeof partial !== 'object') return out;
  for (const k of Object.keys(DEFAULT_LIMITS) as (keyof Limits)[]) {
    const v = partial[k];
    if (typeof v === 'number' && Number.isSafeInteger(v) && v > 0) out[k] = v;
  }
  return out;
}
