import { SvelteMap } from 'svelte/reactivity';
import { ApiError, errorMessage, request } from './api';
import { type CachedItem, history } from './history.svelte';
import { activity } from './log.svelte';
import { listArchive } from './protocol/archive';
import { planChunks, chunkRange } from './protocol/chunking';
import { DecryptError } from './protocol/crypto';
import { type Bytes, fromUtf8 } from './protocol/encoding';
import { IntegrityError, openChunk, openPayload, verifyContent } from './protocol/item';
import { parseItemHeader } from './protocol/messages';
import { session } from './session.svelte';

export type ContentStatus = 'idle' | 'loading' | 'ready' | 'error';

export interface ContentState {
  status: ContentStatus;
  loaded: number;
  total: number;
  error?: string;
}

interface Entry {
  bytes: Bytes;
  text?: string;
  url?: string;
  at: number;
}

const MEMORY_BUDGET = 256 * 1024 * 1024;
const PARALLEL = 3;

export function describeContentError(err: unknown): string {
  if (err instanceof DecryptError) return 'This item could not be decrypted. It may be corrupted.';
  if (err instanceof IntegrityError) return 'Integrity check failed. The content was discarded.';
  return errorMessage(err);
}

class ContentStore {
  states = new SvelteMap<string, ContentState>();
  private entries = new Map<string, Entry>();
  private inflight = new Map<string, Promise<Bytes>>();

  state(id: string): ContentState {
    return (
      this.states.get(id) ?? {
        status: this.entries.has(id) ? 'ready' : 'idle',
        loaded: 0,
        total: 0
      }
    );
  }

  has(id: string): boolean {
    return this.entries.has(id);
  }

  load(item: CachedItem): Promise<Bytes> {
    const cached = this.entries.get(item.id);
    if (cached) {
      cached.at = Date.now();
      return Promise.resolve(cached.bytes);
    }
    const running = this.inflight.get(item.id);
    if (running) return running;
    const p = this.fetch(item).finally(() => this.inflight.delete(item.id));
    this.inflight.set(item.id, p);
    return p;
  }

  private async fetch(item: CachedItem): Promise<Bytes> {
    const keys = session.keys;
    if (!keys) throw new Error('Locked');
    if (!item.m)
      throw new Error(
        item.err === 'unsupported' ? 'This item was created by a newer app version.' : 'This item could not be decrypted.'
      );
    const total = item.size;
    this.states.set(item.id, { status: 'loading', loaded: 0, total });
    try {
      let bytes: Bytes;
      if (item.chunk_count === 0) {
        let payload = item.payload;
        if (!payload) {
          const h = parseItemHeader(await request('GET', `/api/items/${item.id}`, { retries: 3 }));
          payload = h.payload;
          if (!payload) throw new Error('The server returned no payload');
          void history.setPayload(item.id, payload);
        }
        bytes = await openPayload(keys.aes, item.id, payload);
      } else {
        const plan = planChunks(item.size, 0, session.limits.chunk_size_bytes);
        if (plan.chunkCount !== item.chunk_count) throw new IntegrityError('Chunk count does not match the size');
        bytes = new Uint8Array(item.size);
        let loaded = 0;
        let next = 0;
        const worker = async () => {
          while (next < plan.chunkCount) {
            const index = next++;
            const sealed = await request<Bytes>('GET', `/api/items/${item.id}/chunks/${index}`, { expect: 'bytes', retries: 4 });
            const plain = await openChunk(keys.aes, item.id, index, item.chunk_count, sealed);
            const r = chunkRange(plan, index);
            if (plain.length !== r.end - r.start) throw new IntegrityError(`Chunk ${index} has the wrong size`);
            bytes.set(plain, r.start);
            loaded += plain.length;
            this.states.set(item.id, { status: 'loading', loaded, total });
          }
        };
        await Promise.all(Array.from({ length: Math.min(PARALLEL, plan.chunkCount) }, worker));
      }
      await verifyContent(keys, item, item.m, bytes);
      this.remember(item.id, bytes);
      this.states.set(item.id, { status: 'ready', loaded: total, total });
      activity.debug('download', `Loaded ${item.kind} item ${item.id} (${item.size} bytes)`);
      return bytes;
    } catch (err) {
      const msg = describeContentError(err);
      this.states.set(item.id, {
        status: 'error',
        loaded: 0,
        total,
        error: msg
      });
      if (err instanceof ApiError && err.code === 'not_found') {
        void history.remove([item.id]);
      }
      activity.error('download', `Loading item ${item.id} failed: ${msg}`);
      throw err;
    }
  }

  private remember(id: string, bytes: Bytes): void {
    this.entries.set(id, { bytes, at: Date.now() });
    let total = 0;
    for (const e of this.entries.values()) total += e.bytes.length;
    if (total <= MEMORY_BUDGET) return;
    const sorted = [...this.entries.entries()].sort((a, b) => a[1].at - b[1].at);
    for (const [k, e] of sorted) {
      if (total <= MEMORY_BUDGET || k === id) continue;
      total -= e.bytes.length;
      if (e.url) URL.revokeObjectURL(e.url);
      this.entries.delete(k);
      this.states.delete(k);
    }
  }

  peekText(id: string): string | null {
    const e = this.entries.get(id);
    if (!e) return null;
    if (e.text === undefined) {
      try {
        e.text = fromUtf8(e.bytes);
      } catch {
        return null;
      }
    }
    return e.text;
  }

  async text(item: CachedItem): Promise<string> {
    const bytes = await this.load(item);
    const e = this.entries.get(item.id);
    if (e?.text !== undefined) return e.text;
    const t = fromUtf8(bytes);
    if (e) e.text = t;
    return t;
  }

  async imageUrl(item: CachedItem): Promise<string> {
    await this.load(item);
    const e = this.entries.get(item.id);
    if (!e) throw new Error('Image not loaded');
    if (!e.url) e.url = URL.createObjectURL(new Blob([e.bytes], { type: 'image/png' }));
    return e.url;
  }

  async pngBlob(item: CachedItem): Promise<Blob> {
    const bytes = await this.load(item);
    return new Blob([bytes], { type: 'image/png' });
  }

  async files(item: CachedItem): Promise<{ name: string; blob: Blob }[]> {
    const bytes = await this.load(item);
    return listArchive(bytes).map((e) => ({
      name: e.name,
      blob: new Blob([bytes.subarray(e.offset, e.offset + e.size)])
    }));
  }

  forget(id: string): void {
    const e = this.entries.get(id);
    if (e?.url) URL.revokeObjectURL(e.url);
    this.entries.delete(id);
    this.states.delete(id);
  }

  clear(): void {
    for (const e of this.entries.values()) if (e.url) URL.revokeObjectURL(e.url);
    this.entries.clear();
    this.states.clear();
  }
}

export const content = new ContentStore();
