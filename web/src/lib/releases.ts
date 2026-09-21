import { request } from './api';
import { toHex } from './protocol/encoding';
import { ParseError } from './protocol/messages';
import { saveBlob } from './save';

export type ReleasePlatform = 'macos' | 'android' | 'windows-x64' | 'windows-arm64';

export const RELEASE_PLATFORMS: { id: ReleasePlatform; label: string }[] = [
  { id: 'macos', label: 'macOS' },
  { id: 'android', label: 'Android' },
  { id: 'windows-x64', label: 'Windows x64' },
  { id: 'windows-arm64', label: 'Windows arm64' }
];

export interface ReleaseAsset {
  platform: string;
  file: string;
  size: number;
  sha256: string;
  signature: string;
}

export interface Release {
  version: string;
  published_at: string;
  notes_md: string;
  assets: ReleaseAsset[];
}

export interface PlatformDownload {
  platform: ReleasePlatform;
  label: string;
  version: string;
  published_at: string;
  asset: ReleaseAsset;
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

function parseAsset(v: unknown): ReleaseAsset {
  const o = obj(v, 'asset');
  const size = o.size;
  if (typeof size !== 'number' || !Number.isSafeInteger(size) || size < 0) throw new ParseError('size must be an integer');
  return { platform: str(o, 'platform'), file: str(o, 'file'), size, sha256: str(o, 'sha256'), signature: str(o, 'signature') };
}

export function parseRelease(v: unknown): Release {
  const o = obj(v, 'release');
  const assets = Array.isArray(o.assets) ? o.assets.map(parseAsset) : [];
  return {
    version: str(o, 'version'),
    published_at: typeof o.published_at === 'string' ? o.published_at : '',
    notes_md: typeof o.notes_md === 'string' ? o.notes_md : '',
    assets
  };
}

export function parseReleases(v: unknown): Release[] {
  const o = obj(v, 'releases response');
  if (!Array.isArray(o.releases)) throw new ParseError('releases must be an array');
  return o.releases.map(parseRelease);
}

function versionParts(v: string): number[] {
  return v.split('.').map((p) => {
    const n = Number.parseInt(p, 10);
    return Number.isFinite(n) ? n : 0;
  });
}

export function compareVersions(a: string, b: string): number {
  const pa = versionParts(a);
  const pb = versionParts(b);
  for (let i = 0; i < Math.max(pa.length, pb.length, 3); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d !== 0) return d < 0 ? -1 : 1;
  }
  return 0;
}

export function sortReleases(list: Release[]): Release[] {
  return [...list].sort((a, b) => compareVersions(b.version, a.version));
}

export function latestDownloads(list: Release[]): PlatformDownload[] {
  const sorted = sortReleases(list);
  const out: PlatformDownload[] = [];
  for (const p of RELEASE_PLATFORMS) {
    for (const r of sorted) {
      const asset = r.assets.find((a) => a.platform === p.id);
      if (asset) {
        out.push({ platform: p.id, label: p.label, version: r.version, published_at: r.published_at, asset });
        break;
      }
    }
  }
  return out;
}

export async function fetchReleases(limit = 20, signal?: AbortSignal): Promise<Release[]> {
  const res = await request('GET', `/api/releases?limit=${limit}`, { retries: 2, signal });
  return sortReleases(parseReleases(res));
}

export function assetUrl(version: string, file: string): string {
  return `/api/releases/${encodeURIComponent(version)}/assets/${encodeURIComponent(file)}`;
}

export class ChecksumError extends Error {
  constructor(file: string) {
    super(`${file} failed its checksum, please try again`);
    this.name = 'ChecksumError';
  }
}

export async function downloadAsset(version: string, asset: ReleaseAsset, signal?: AbortSignal): Promise<void> {
  const blob = await request<Blob>('GET', assetUrl(version, asset.file), { expect: 'blob', signal });
  if (blob.size !== asset.size) throw new ChecksumError(asset.file);
  if (typeof crypto !== 'undefined' && crypto.subtle) {
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', await blob.arrayBuffer()));
    if (toHex(digest) !== asset.sha256) throw new ChecksumError(asset.file);
  }
  saveBlob(new Blob([blob], { type: 'application/octet-stream' }), asset.file);
}
