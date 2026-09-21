import { KDF_ALGORITHM, KEY_LENGTH, type Limits, mergeLimits, PROTOCOL_VERSION } from './constants';
import type { ItemKind } from './meta';

export type Platform = 'macos' | 'android' | 'windows' | 'web';
export const PLATFORMS: Platform[] = ['macos', 'android', 'windows', 'web'];

export interface ItemHeader {
  id: string;
  seq: number;
  device_id: string;
  kind: ItemKind;
  size: number;
  chunk_count: number;
  created_at: string;
  pinned: boolean;
  content_hash: string;
  has_thumb: boolean;
  stored_bytes: number;
  meta: string;
  payload?: string;
}

export interface Device {
  id: string;
  name: string;
  platform: Platform;
  created_at: string;
  last_seen_at: string;
  online: boolean;
  revoked: boolean;
  current: boolean;
}

export interface Kdf {
  algorithm: string;
  iterations: number;
  key_length: number;
}

export interface LoginResponse {
  device_id: string;
  token: string;
  username: string;
  salt: string;
  kdf: Kdf;
  key_check: string | null;
  server_id: string;
  server_version: string;
  protocol_version: number;
}

export interface MeResponse {
  username: string;
  device: Device;
  salt: string;
  kdf: Kdf;
  key_check: string | null;
  server_id: string;
  server_version: string;
  protocol_version: number;
  limits: Limits;
}

export interface HistoryResponse {
  items: ItemHeader[];
  has_more: boolean;
}

export interface HistoryIndexEntry {
  id: string;
  seq: number;
  pinned: boolean;
}

export interface HistoryIndex {
  current_seq: number;
  state_rev: number;
  items: HistoryIndexEntry[];
}

export interface StorageInfo {
  used_bytes: number;
  limit_bytes: number;
  pinned_bytes: number;
  pinned_limit_bytes: number;
  item_count: number;
  free_disk_bytes: number;
  min_free_disk_bytes: number;
  retention_days: number;
  disk_low: boolean;
}

export interface ErrorBody {
  code: string;
  message: string;
  details?: Record<string, unknown>;
}

export interface HealthResponse {
  status: string;
  server_version: string;
  protocol_version: number;
}

export class ParseError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ParseError';
  }
}

type Obj = Record<string, unknown>;

function obj(v: unknown, what: string): Obj {
  if (typeof v !== 'object' || v === null || Array.isArray(v)) throw new ParseError(`${what} must be an object`);
  return v as Obj;
}

function str(o: Obj, k: string): string {
  const v = o[k];
  if (typeof v !== 'string') throw new ParseError(`${k} must be a string`);
  return v;
}

function int(o: Obj, k: string): number {
  const v = o[k];
  if (typeof v !== 'number' || !Number.isSafeInteger(v)) throw new ParseError(`${k} must be an integer`);
  return v;
}

function bool(o: Obj, k: string): boolean {
  const v = o[k];
  if (typeof v !== 'boolean') throw new ParseError(`${k} must be a boolean`);
  return v;
}

function nullableStr(o: Obj, k: string): string | null {
  const v = o[k];
  if (v === null || v === undefined) return null;
  if (typeof v !== 'string') throw new ParseError(`${k} must be a string or null`);
  return v;
}

function platform(o: Obj, k: string): Platform {
  const v = str(o, k);
  return (PLATFORMS as string[]).includes(v) ? (v as Platform) : 'web';
}

export function parseKdf(v: unknown): Kdf {
  const o = obj(v, 'kdf');
  return {
    algorithm: str(o, 'algorithm'),
    iterations: int(o, 'iterations'),
    key_length: int(o, 'key_length')
  };
}

export function kdfSupported(k: Kdf): boolean {
  return k.algorithm === KDF_ALGORITHM && k.key_length === KEY_LENGTH;
}

export function parseItemHeader(v: unknown): ItemHeader {
  const o = obj(v, 'item');
  const kind = str(o, 'kind');
  if (kind !== 'text' && kind !== 'image' && kind !== 'files') throw new ParseError('unknown kind');
  const h: ItemHeader = {
    id: str(o, 'id'),
    seq: int(o, 'seq'),
    device_id: str(o, 'device_id'),
    kind,
    size: int(o, 'size'),
    chunk_count: int(o, 'chunk_count'),
    created_at: str(o, 'created_at'),
    pinned: bool(o, 'pinned'),
    content_hash: str(o, 'content_hash'),
    has_thumb: bool(o, 'has_thumb'),
    stored_bytes: int(o, 'stored_bytes'),
    meta: str(o, 'meta')
  };
  if (typeof o.payload === 'string') h.payload = o.payload;
  return h;
}

export function parseDevice(v: unknown): Device {
  const o = obj(v, 'device');
  return {
    id: str(o, 'id'),
    name: str(o, 'name'),
    platform: platform(o, 'platform'),
    created_at: str(o, 'created_at'),
    last_seen_at: str(o, 'last_seen_at'),
    online: bool(o, 'online'),
    revoked: bool(o, 'revoked'),
    current: bool(o, 'current')
  };
}

export function parseDevices(v: unknown): Device[] {
  const o = obj(v, 'devices response');
  if (!Array.isArray(o.devices)) throw new ParseError('devices must be an array');
  return o.devices.map(parseDevice);
}

export function parseLoginResponse(v: unknown): LoginResponse {
  const o = obj(v, 'login response');
  return {
    device_id: str(o, 'device_id'),
    token: str(o, 'token'),
    username: str(o, 'username'),
    salt: str(o, 'salt'),
    kdf: parseKdf(o.kdf),
    key_check: nullableStr(o, 'key_check'),
    server_id: str(o, 'server_id'),
    server_version: str(o, 'server_version'),
    protocol_version: int(o, 'protocol_version')
  };
}

export function parseMe(v: unknown): MeResponse {
  const o = obj(v, 'me response');
  return {
    username: str(o, 'username'),
    device: parseDevice(o.device),
    salt: str(o, 'salt'),
    kdf: parseKdf(o.kdf),
    key_check: nullableStr(o, 'key_check'),
    server_id: str(o, 'server_id'),
    server_version: str(o, 'server_version'),
    protocol_version: int(o, 'protocol_version'),
    limits: mergeLimits(typeof o.limits === 'object' ? (o.limits as Partial<Limits>) : undefined)
  };
}

export function parseHistory(v: unknown): HistoryResponse {
  const o = obj(v, 'history response');
  if (!Array.isArray(o.items)) throw new ParseError('items must be an array');
  return { items: o.items.map(parseItemHeader), has_more: bool(o, 'has_more') };
}

export function parseHistoryIndex(v: unknown): HistoryIndex {
  const o = obj(v, 'history index');
  if (!Array.isArray(o.items)) throw new ParseError('items must be an array');
  return {
    current_seq: int(o, 'current_seq'),
    state_rev: int(o, 'state_rev'),
    items: o.items.map((e) => {
      const x = obj(e, 'index entry');
      return {
        id: str(x, 'id'),
        seq: int(x, 'seq'),
        pinned: bool(x, 'pinned')
      };
    })
  };
}

export function parseStorage(v: unknown): StorageInfo {
  const o = obj(v, 'storage');
  return {
    used_bytes: int(o, 'used_bytes'),
    limit_bytes: int(o, 'limit_bytes'),
    pinned_bytes: int(o, 'pinned_bytes'),
    pinned_limit_bytes: int(o, 'pinned_limit_bytes'),
    item_count: int(o, 'item_count'),
    free_disk_bytes: int(o, 'free_disk_bytes'),
    min_free_disk_bytes: int(o, 'min_free_disk_bytes'),
    retention_days: int(o, 'retention_days'),
    disk_low: bool(o, 'disk_low')
  };
}

export function parseErrorBody(v: unknown): ErrorBody | null {
  if (typeof v !== 'object' || v === null) return null;
  const o = v as Obj;
  if (typeof o.code !== 'string') return null;
  const e: ErrorBody = {
    code: o.code,
    message: typeof o.message === 'string' ? o.message : ''
  };
  if (typeof o.details === 'object' && o.details !== null) e.details = o.details as Record<string, unknown>;
  return e;
}

export function parseHealth(v: unknown): HealthResponse {
  const o = obj(v, 'health');
  return {
    status: str(o, 'status'),
    server_version: str(o, 'server_version'),
    protocol_version: int(o, 'protocol_version')
  };
}

export interface LoginRequest {
  username: string;
  password: string;
  device_name: string;
  platform: Platform;
  device_id?: string;
}

export function buildLoginRequest(input: {
  username: string;
  password: string;
  deviceName: string;
  platform?: Platform;
  deviceId?: string | null;
}): LoginRequest {
  const req: LoginRequest = {
    username: input.username,
    password: input.password,
    device_name: input.deviceName.trim(),
    platform: input.platform ?? 'web'
  };
  if (input.deviceId) req.device_id = input.deviceId;
  return req;
}

export interface CreateItemRequest {
  id: string;
  kind: ItemKind;
  size: number;
  chunk_count: 0;
  content_hash: string;
  meta: string;
  payload: string;
}

export function buildCreateItemRequest(input: {
  id: string;
  kind: ItemKind;
  size: number;
  contentHash: string;
  meta: string;
  payload: string;
}): CreateItemRequest {
  return {
    id: input.id,
    kind: input.kind,
    size: input.size,
    chunk_count: 0,
    content_hash: input.contentHash,
    meta: input.meta,
    payload: input.payload
  };
}

export interface CommitRequest {
  kind: ItemKind;
  size: number;
  chunk_count: number;
  content_hash: string;
  meta: string;
}

export function buildCommitRequest(input: {
  kind: ItemKind;
  size: number;
  chunkCount: number;
  contentHash: string;
  meta: string;
}): CommitRequest {
  return {
    kind: input.kind,
    size: input.size,
    chunk_count: input.chunkCount,
    content_hash: input.contentHash,
    meta: input.meta
  };
}

export function buildPinRequest(pinned: boolean): { pinned: boolean } {
  return { pinned };
}

export function buildKeyCheckRequest(keyCheck: string): { key_check: string } {
  return { key_check: keyCheck };
}

export function buildRenameRequest(name: string): { name: string } {
  return { name: name.trim() };
}

export function validateDeviceName(name: string): string | null {
  const t = name.trim();
  const n = Array.from(t).length;
  if (n < 1) return 'Enter a device name';
  if (n > 64) return 'Use at most 64 characters';
  if (/[\u0000-\u001f\u007f-\u009f]/.test(t)) return 'Remove control characters';
  return null;
}

export interface HelloMessage {
  type: 'hello';
  protocol_version: number;
  device_id: string;
  last_seq: number;
  app_version: string;
  platform: Platform;
}

export interface PingMessage {
  type: 'ping';
  ts: number;
}

export interface PongMessage {
  type: 'pong';
  ts: number;
}

export type ClientMessage = HelloMessage | PingMessage | PongMessage;

export function buildHello(input: { deviceId: string; lastSeq: number; appVersion: string; platform?: Platform }): HelloMessage {
  return {
    type: 'hello',
    protocol_version: PROTOCOL_VERSION,
    device_id: input.deviceId,
    last_seq: input.lastSeq,
    app_version: input.appVersion,
    platform: input.platform ?? 'web'
  };
}

export function buildPing(ts: number = Date.now()): PingMessage {
  return { type: 'ping', ts };
}

export function buildPong(ts: number): PongMessage {
  return { type: 'pong', ts };
}

export interface OnlineDevice {
  device_id: string;
  name: string;
  platform: Platform;
}

export interface WelcomeMessage {
  type: 'welcome';
  protocol_version: number;
  server_version: string;
  server_id: string;
  server_time: string;
  device_id: string;
  current_seq: number;
  state_rev: number;
  online_devices: OnlineDevice[];
}

export interface PresenceMessage {
  type: 'presence';
  device_id: string;
  name: string;
  platform: Platform;
  online: boolean;
}

export interface DevicesChangedMessage {
  type: 'devices_changed';
}

export interface ClipMessage {
  type: 'clip';
  item: ItemHeader;
}

export interface ClipDeletedMessage {
  type: 'clip_deleted';
  ids: string[];
  reason: string;
  state_rev: number;
}

export interface ClipPinnedMessage {
  type: 'clip_pinned';
  id: string;
  pinned: boolean;
  state_rev: number;
}

export interface StorageWarningMessage {
  type: 'storage_warning';
  active: boolean;
  reason: string;
  free_disk_bytes: number;
  min_free_disk_bytes: number;
}

export interface ServerErrorMessage {
  type: 'error';
  code: string;
  message: string;
  details?: Record<string, unknown>;
}

export type ServerMessage =
  | WelcomeMessage
  | PresenceMessage
  | DevicesChangedMessage
  | ClipMessage
  | ClipDeletedMessage
  | ClipPinnedMessage
  | StorageWarningMessage
  | PingMessage
  | PongMessage
  | ServerErrorMessage;

function onlineDevice(v: unknown): OnlineDevice {
  const o = obj(v, 'online device');
  return {
    device_id: str(o, 'device_id'),
    name: str(o, 'name'),
    platform: platform(o, 'platform')
  };
}

export function parseServerMessage(input: string | unknown): ServerMessage | null {
  let raw: unknown = input;
  if (typeof input === 'string') {
    try {
      raw = JSON.parse(input);
    } catch {
      throw new ParseError('frame is not JSON');
    }
  }
  const o = obj(raw, 'message');
  const type = str(o, 'type');
  switch (type) {
    case 'welcome':
      if (!Array.isArray(o.online_devices)) throw new ParseError('online_devices must be an array');
      return {
        type,
        protocol_version: int(o, 'protocol_version'),
        server_version: str(o, 'server_version'),
        server_id: str(o, 'server_id'),
        server_time: str(o, 'server_time'),
        device_id: str(o, 'device_id'),
        current_seq: int(o, 'current_seq'),
        state_rev: int(o, 'state_rev'),
        online_devices: o.online_devices.map(onlineDevice)
      };
    case 'presence':
      return {
        type,
        device_id: str(o, 'device_id'),
        name: str(o, 'name'),
        platform: platform(o, 'platform'),
        online: bool(o, 'online')
      };
    case 'devices_changed':
      return { type };
    case 'clip':
      return { type, item: parseItemHeader(o.item) };
    case 'clip_deleted':
      if (!Array.isArray(o.ids) || !o.ids.every((x) => typeof x === 'string')) throw new ParseError('ids must be strings');
      return {
        type,
        ids: o.ids as string[],
        reason: str(o, 'reason'),
        state_rev: int(o, 'state_rev')
      };
    case 'clip_pinned':
      return {
        type,
        id: str(o, 'id'),
        pinned: bool(o, 'pinned'),
        state_rev: int(o, 'state_rev')
      };
    case 'storage_warning':
      return {
        type,
        active: bool(o, 'active'),
        reason: str(o, 'reason'),
        free_disk_bytes: int(o, 'free_disk_bytes'),
        min_free_disk_bytes: int(o, 'min_free_disk_bytes')
      };
    case 'ping':
      return { type, ts: int(o, 'ts') };
    case 'pong':
      return { type, ts: int(o, 'ts') };
    case 'error': {
      const e: ServerErrorMessage = {
        type,
        code: str(o, 'code'),
        message: typeof o.message === 'string' ? o.message : ''
      };
      if (typeof o.details === 'object' && o.details !== null) e.details = o.details as Record<string, unknown>;
      return e;
    }
    default:
      return null;
  }
}

export function parseTimestamp(s: string): number {
  const t = Date.parse(s);
  if (Number.isNaN(t)) throw new ParseError('invalid timestamp');
  return t;
}

export function formatTimestamp(ms: number): string {
  return new Date(ms).toISOString();
}
