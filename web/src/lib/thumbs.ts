import { ApiError, request } from './api';
import { content } from './content.svelte';
import type { CachedItem } from './history.svelte';
import { idbGet, idbPut } from './idb';
import { activity } from './log.svelte';
import type { Bytes } from './protocol/encoding';
import { openThumb } from './protocol/item';
import { session } from './session.svelte';

const urls = new Map<string, string>();
const inflight = new Map<string, Promise<string | null>>();
const INLINE_PREVIEW_MAX = 262144;
let active = 0;
const queue: (() => void)[] = [];

function acquire(): Promise<void> {
  if (active < 4) {
    active++;
    return Promise.resolve();
  }
  return new Promise((resolve) => queue.push(() => resolve()));
}

function release(): void {
  const next = queue.shift();
  if (next) next();
  else active--;
}

export function cachedThumb(id: string): string | null {
  return urls.get(id) ?? null;
}

export function canHaveThumb(item: CachedItem): boolean {
  return item.kind === 'image' && !!item.m && (item.has_thumb || (item.chunk_count === 0 && item.size <= INLINE_PREVIEW_MAX));
}

export function loadThumb(item: CachedItem): Promise<string | null> {
  const hit = urls.get(item.id);
  if (hit) return Promise.resolve(hit);
  if (!canHaveThumb(item)) return Promise.resolve(null);
  const running = inflight.get(item.id);
  if (running) return running;
  const p = (async () => {
    await acquire();
    try {
      let blob = await idbGet<Blob>('thumbs', item.id);
      if (!blob) {
        const keys = session.keys;
        if (!keys) return null;
        if (item.has_thumb) {
          try {
            const sealed = await request<Bytes>('GET', `/api/items/${item.id}/thumb`, { expect: 'bytes', retries: 2 });
            blob = new Blob([await openThumb(keys.aes, item.id, sealed)], {
              type: 'image/jpeg'
            });
          } catch (err) {
            if (!(err instanceof ApiError && err.code === 'not_found')) {
              activity.warn('download', `Thumbnail for ${item.id} failed: ${String(err)}`);
            }
          }
        }
        if (!blob && item.chunk_count === 0 && item.size <= INLINE_PREVIEW_MAX) {
          blob = await content.pngBlob(item);
        }
        if (!blob) return null;
        void idbPut('thumbs', item.id, blob);
      }
      const url = URL.createObjectURL(blob);
      urls.set(item.id, url);
      return url;
    } catch {
      return null;
    } finally {
      release();
      inflight.delete(item.id);
    }
  })();
  inflight.set(item.id, p);
  return p;
}

export function clearThumbs(): void {
  for (const u of urls.values()) URL.revokeObjectURL(u);
  urls.clear();
}
