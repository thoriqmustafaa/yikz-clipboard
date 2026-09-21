import { request } from './api';
import { idbClear, idbDelete, idbGetAll, idbPutMany } from './idb';
import { activity } from './log.svelte';
import { DecryptError } from './protocol/crypto';
import { linkFromText, openMeta } from './protocol/item';
import { type Meta, MetaError } from './protocol/meta';
import { type HistoryIndex, type ItemHeader, parseHistory, parseTimestamp } from './protocol/messages';
import { session } from './session.svelte';

export type ItemError = 'corrupt' | 'unsupported';

export interface CachedItem {
  id: string;
  seq: number;
  device_id: string;
  kind: ItemHeader['kind'];
  size: number;
  chunk_count: number;
  created_at: string;
  pinned: boolean;
  content_hash: string;
  has_thumb: boolean;
  stored_bytes: number;
  meta: string;
  payload?: string;
  createdMs: number;
  m: Meta | null;
  err: ItemError | null;
  link: string | null;
  search: string;
  sid: string | null;
}

export type TypeFilter = 'all' | 'text' | 'links' | 'images' | 'files';

export function matchesType(item: CachedItem, type: TypeFilter): boolean {
  switch (type) {
    case 'all':
      return true;
    case 'text':
      return item.kind === 'text' && !item.link;
    case 'links':
      return item.kind === 'text' && !!item.link;
    case 'images':
      return item.kind === 'image';
    case 'files':
      return item.kind === 'files';
  }
}

function searchText(m: Meta | null): string {
  if (!m) return '';
  const parts = [m.preview];
  if (m.files) for (const f of m.files) parts.push(f.name);
  if (m.source_app) parts.push(m.source_app);
  return parts.join('\n').toLowerCase();
}

class History {
  items = $state.raw<CachedItem[]>([]);
  hasMore = $state(true);
  loadingOlder = $state(false);
  loadingAll = $state(false);
  cacheLoaded = $state(false);
  syncing = $state(false);
  fresh = $state.raw<ReadonlySet<string>>(new Set());
  serverId: string | null = null;
  private byId = new Map<string, CachedItem>();
  private freshTimers = new Map<string, ReturnType<typeof setTimeout>>();
  private olderPromise: Promise<void> | null = null;

  get(id: string): CachedItem | undefined {
    return this.byId.get(id);
  }

  newest(): CachedItem | undefined {
    return this.items[0];
  }

  oldestSeq(): number | null {
    return this.items.length ? this.items[this.items.length - 1].seq : null;
  }

  private publish(): void {
    this.items = [...this.byId.values()].sort((a, b) => b.seq - a.seq);
  }

  async loadFromCache(serverId: string | null): Promise<void> {
    this.serverId = serverId;
    const all = await idbGetAll<CachedItem>('items');
    const stale: string[] = [];
    this.byId.clear();
    for (const r of all) {
      if (!r || typeof r.id !== 'string') continue;
      if (serverId && r.sid && r.sid !== serverId) {
        stale.push(r.id);
        continue;
      }
      this.byId.set(r.id, r);
    }
    if (stale.length) await idbDelete('items', stale);
    const needsDecrypt = [...this.byId.values()].filter((r) => r.m === null && r.err === null);
    for (const r of needsDecrypt) this.byId.set(r.id, await this.decrypt(r));
    this.publish();
    this.cacheLoaded = true;
    this.hasMore = true;
    activity.debug('sync', `Loaded ${this.byId.size} items from the local cache`);
  }

  private async decrypt(h: ItemHeader | CachedItem): Promise<CachedItem> {
    const createdMs = (() => {
      try {
        return parseTimestamp(h.created_at);
      } catch {
        return Date.now();
      }
    })();
    const base: CachedItem = {
      id: h.id,
      seq: h.seq,
      device_id: h.device_id,
      kind: h.kind,
      size: h.size,
      chunk_count: h.chunk_count,
      created_at: h.created_at,
      pinned: h.pinned,
      content_hash: h.content_hash,
      has_thumb: h.has_thumb,
      stored_bytes: h.stored_bytes,
      meta: h.meta,
      payload: h.payload,
      createdMs,
      m: null,
      err: null,
      link: null,
      search: '',
      sid: this.serverId
    };
    const keys = session.keys;
    if (!keys) return base;
    try {
      const m = await openMeta(keys.aes, h.id, h.meta);
      base.m = m;
      base.link = h.kind === 'text' ? linkFromText(m.preview) : null;
      base.search = searchText(m);
    } catch (err) {
      if (err instanceof MetaError && err.unsupported) {
        base.err = 'unsupported';
        activity.warn('sync', `Item ${h.id} uses an unsupported meta version`);
      } else {
        base.err = 'corrupt';
        activity.error('sync', `Could not decrypt meta of item ${h.id}${err instanceof DecryptError ? '' : `: ${String(err)}`}`);
      }
    }
    return base;
  }

  async upsert(headers: ItemHeader[], opts: { fresh?: boolean } = {}): Promise<CachedItem[]> {
    const out: CachedItem[] = [];
    const changed: CachedItem[] = [];
    for (const h of headers) {
      const existing = this.byId.get(h.id);
      if (existing && existing.seq === h.seq && existing.meta === h.meta && (existing.m || existing.err)) {
        const updated: CachedItem = {
          ...existing,
          pinned: h.pinned,
          has_thumb: h.has_thumb,
          stored_bytes: h.stored_bytes,
          payload: h.payload ?? existing.payload
        };
        this.byId.set(h.id, updated);
        out.push(updated);
        changed.push(updated);
        continue;
      }
      const item = await this.decrypt(h);
      if (existing?.payload && !item.payload) item.payload = existing.payload;
      this.byId.set(h.id, item);
      out.push(item);
      changed.push(item);
      if (opts.fresh && !existing) this.markFresh(h.id);
    }
    if (changed.length) {
      this.publish();
      await idbPutMany(
        'items',
        changed.map((c) => [c.id, $state.snapshot(c)])
      );
    }
    return out;
  }

  private markFresh(id: string): void {
    const next = new Set(this.fresh);
    next.add(id);
    this.fresh = next;
    const t = setTimeout(() => {
      const s = new Set(this.fresh);
      s.delete(id);
      this.fresh = s;
      this.freshTimers.delete(id);
    }, 1600);
    this.freshTimers.set(id, t);
  }

  async setPayload(id: string, payload: string): Promise<void> {
    const it = this.byId.get(id);
    if (!it) return;
    const next = { ...it, payload };
    this.byId.set(id, next);
    this.publish();
    await idbPutMany('items', [[id, $state.snapshot(next)]]);
  }

  async remove(ids: string[]): Promise<void> {
    let any = false;
    for (const id of ids) if (this.byId.delete(id)) any = true;
    if (any) this.publish();
    await idbDelete('items', ids);
    await idbDelete('thumbs', ids);
  }

  async setPinned(id: string, pinned: boolean): Promise<void> {
    const it = this.byId.get(id);
    if (!it || it.pinned === pinned) return;
    const next = { ...it, pinned };
    this.byId.set(id, next);
    this.publish();
    await idbPutMany('items', [[id, $state.snapshot(next)]]);
  }

  async reconcile(index: HistoryIndex): Promise<void> {
    const present = new Map(index.items.map((e) => [e.id, e]));
    const gone: string[] = [];
    const changed: CachedItem[] = [];
    for (const [id, it] of this.byId) {
      const e = present.get(id);
      if (!e) {
        gone.push(id);
        continue;
      }
      if (e.pinned !== it.pinned) {
        const next = { ...it, pinned: e.pinned };
        this.byId.set(id, next);
        changed.push(next);
      }
    }
    for (const id of gone) this.byId.delete(id);
    if (gone.length || changed.length) this.publish();
    if (gone.length) {
      await idbDelete('items', gone);
      await idbDelete('thumbs', gone);
      activity.info('sync', `Reconcile removed ${gone.length} items`);
    }
    if (changed.length)
      await idbPutMany(
        'items',
        changed.map((c) => [c.id, $state.snapshot(c)])
      );
    const oldest = this.oldestSeq();
    this.hasMore = index.items.length > 0 && (oldest === null || index.items[0].seq < oldest);
  }

  async fetchPage(params: {
    before?: number;
    after?: number;
    limit: number;
  }): Promise<{ items: CachedItem[]; hasMore: boolean }> {
    const q = new URLSearchParams();
    if (params.before !== undefined) q.set('before', String(params.before));
    if (params.after !== undefined) q.set('after', String(params.after));
    q.set('limit', String(params.limit));
    const res = parseHistory(await request('GET', `/api/history?${q.toString()}`, { retries: 3 }));
    const items = await this.upsert(res.items);
    return { items, hasMore: res.has_more };
  }

  loadOlder(limit = 100): Promise<void> {
    if (this.olderPromise) return this.olderPromise;
    if (!this.hasMore) return Promise.resolve();
    this.loadingOlder = true;
    this.olderPromise = (async () => {
      try {
        const oldest = this.oldestSeq();
        const page = await this.fetchPage(oldest === null ? { limit } : { before: oldest, limit });
        this.hasMore = page.hasMore && page.items.length > 0;
      } catch (err) {
        activity.warn('sync', `Loading older history failed: ${String(err)}`);
        throw err;
      } finally {
        this.loadingOlder = false;
        this.olderPromise = null;
      }
    })();
    return this.olderPromise;
  }

  async loadAll(): Promise<void> {
    if (this.loadingAll) return;
    this.loadingAll = true;
    try {
      while (this.hasMore) await this.loadOlder(500);
    } catch {
      return;
    } finally {
      this.loadingAll = false;
    }
  }

  async clear(): Promise<void> {
    this.byId.clear();
    this.items = [];
    this.hasMore = true;
    this.fresh = new Set();
    await idbClear(['items', 'thumbs']);
  }

  reset(): void {
    this.byId.clear();
    this.items = [];
    this.hasMore = true;
    this.cacheLoaded = false;
    this.fresh = new Set();
    this.serverId = null;
  }
}

export const history = new History();
