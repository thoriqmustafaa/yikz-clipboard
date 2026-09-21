import { AbortedError, ApiError, errorMessage, request, retryDelayMs, sleep, uploadWithProgress } from './api';
import { history } from './history.svelte';
import { makeThumbnail, toPng } from './images';
import { activity } from './log.svelte';
import { firstLine, formatBytes, pluralize } from './format';
import { isRecentHash, rememberHash } from './recent';
import { session, WrongPasswordError } from './session.svelte';
import { toasts } from './toast.svelte';
import { archiveSize, encodeArchiveFromBlobs, sanitizeFileName, uniqueFileNames } from './protocol/archive';
import { chunkRange, planChunks, type ChunkPlan } from './protocol/chunking';
import { SEAL_OVERHEAD, THUMB_MAX_SIDE } from './protocol/constants';
import { contentHash, sha256Hex } from './protocol/crypto';
import { type Bytes, utf8 } from './protocol/encoding';
import { sealChunk, sealMeta, sealPayload, sealThumb } from './protocol/item';
import { buildMeta, type ItemKind, type Meta, type MetaFile } from './protocol/meta';
import { buildCommitRequest, buildCreateItemRequest, parseItemHeader } from './protocol/messages';
import { uuidv7 } from './protocol/uuidv7';

export const MAX_WEB_ITEM_BYTES = 2 * 1024 * 1024 * 1024 - 1;
const CHUNK_PARALLEL = 3;
const CHUNK_ATTEMPTS = 6;

export type UploadPhase = 'preparing' | 'uploading' | 'committing' | 'done' | 'error' | 'canceled';

interface Prepared {
  kind: ItemKind;
  content: Bytes;
  hash: string;
  sha: string;
  meta: Meta;
  thumb: Bytes | null;
}

export class UploadTask {
  readonly key: number;
  label = $state('');
  kind = $state<ItemKind>('text');
  phase = $state<UploadPhase>('preparing');
  total = $state(0);
  sent = $state(0);
  error = $state<string | null>(null);
  retryable = $state(false);
  itemId = '';
  prepared: Prepared | null = null;
  controller = new AbortController();
  private chunkSent = new Map<number, number>();
  private doneChunks = new Set<number>();

  constructor(key: number, label: string, kind: ItemKind) {
    this.key = key;
    this.label = label;
    this.kind = kind;
  }

  get progress(): number {
    if (this.phase === 'done') return 1;
    return this.total > 0 ? Math.min(1, this.sent / this.total) : 0;
  }

  setChunkProgress(index: number, loaded: number): void {
    this.chunkSent.set(index, loaded);
    let s = 0;
    for (const v of this.chunkSent.values()) s += v;
    this.sent = s;
  }

  markChunkDone(index: number): void {
    this.doneChunks.add(index);
  }

  isChunkDone(index: number): boolean {
    return this.doneChunks.has(index);
  }

  resetChunks(): void {
    this.doneChunks.clear();
    this.chunkSent.clear();
    this.sent = 0;
  }
}

function isDuplicate(hash: string, altHash: string | null): boolean {
  if (isRecentHash(hash) || (altHash !== null && isRecentHash(altHash))) return true;
  const newest = history.newest();
  return !!newest && newest.content_hash === hash;
}

class Uploads {
  list = $state<UploadTask[]>([]);
  private nextKey = 1;

  get active(): UploadTask[] {
    return this.list.filter((t) => t.phase !== 'done' && t.phase !== 'canceled');
  }

  private add(task: UploadTask): void {
    this.list = [...this.list, task];
  }

  dismiss(task: UploadTask): void {
    task.prepared = null;
    this.list = this.list.filter((t) => t !== task);
  }

  private scheduleDismiss(task: UploadTask, ms = 2500): void {
    setTimeout(() => this.dismiss(task), ms);
  }

  async sendText(text: string, force = false): Promise<void> {
    if (!text || text.length === 0) return;
    const keys = session.keys;
    if (!keys) return;
    const content = utf8(text);
    const hash = await contentHash(keys.contentHash, content);
    const alt = text.includes('\r\n') ? await contentHash(keys.contentHash, utf8(text.replace(/\r\n/g, '\n'))) : null;
    if (!force && isDuplicate(hash, alt)) {
      this.duplicateNotice(() => void this.sendText(text, true));
      return;
    }
    const task = new UploadTask(this.nextKey++, firstLine(text, 80) || 'Text', 'text');
    const sha = await sha256Hex(content);
    task.prepared = { kind: 'text', content, hash, sha, meta: buildMeta({ kind: 'text', sha256: sha, text }), thumb: null };
    this.add(task);
    await this.run(task);
  }

  async sendImage(blob: Blob, force = false): Promise<void> {
    const keys = session.keys;
    if (!keys) return;
    const task = new UploadTask(this.nextKey++, 'Image', 'image');
    this.add(task);
    try {
      const img = await toPng(blob);
      if (img.png.length > MAX_WEB_ITEM_BYTES) throw new Error('This image is too large for the browser to send.');
      task.label = `Image ${img.width} x ${img.height}`;
      const hash = await contentHash(keys.contentHash, img.png);
      if (!force && isDuplicate(hash, null)) {
        this.dismiss(task);
        this.duplicateNotice(() => void this.sendImage(blob, true));
        return;
      }
      const sha = await sha256Hex(img.png);
      const thumb = await makeThumbnail(img.png, THUMB_MAX_SIDE, session.limits.thumb_max_bytes, SEAL_OVERHEAD);
      task.prepared = {
        kind: 'image',
        content: img.png,
        hash,
        sha,
        meta: buildMeta({ kind: 'image', sha256: sha, image: { width: img.width, height: img.height } }),
        thumb
      };
    } catch (err) {
      this.fail(task, err, false);
      return;
    }
    await this.run(task);
  }

  async sendFiles(files: File[], force = false): Promise<void> {
    const keys = session.keys;
    if (!keys || files.length === 0) return;
    const limit = session.limits.max_files_per_item;
    if (files.length > limit) {
      toasts.error(`Too many files`, `Send at most ${limit} files at once.`);
      return;
    }
    const names = uniqueFileNames(files.map((f) => sanitizeFileName(f.name)));
    const label = files.length === 1 ? names[0] : pluralize(files.length, 'file');
    const task = new UploadTask(this.nextKey++, label, 'files');
    this.add(task);
    try {
      const size = archiveSize(names.map((n, i) => ({ name: n, size: files[i].size })));
      if (size > MAX_WEB_ITEM_BYTES) {
        throw new Error(`${formatBytes(size)} is more than a browser can send at once (2 GB).`);
      }
      const entries = names.map((n, i) => ({ name: n, blob: files[i] as Blob }));
      const content = await encodeArchiveFromBlobs(entries);
      const hash = await contentHash(keys.contentHash, content);
      if (!force && isDuplicate(hash, null)) {
        this.dismiss(task);
        this.duplicateNotice(() => void this.sendFiles(files, true));
        return;
      }
      const sha = await sha256Hex(content);
      const metaFiles: MetaFile[] = names.map((n, i) => ({ name: n, size: files[i].size }));
      task.prepared = {
        kind: 'files',
        content,
        hash,
        sha,
        meta: buildMeta({ kind: 'files', sha256: sha, files: metaFiles }),
        thumb: null
      };
    } catch (err) {
      this.fail(task, err, false);
      return;
    }
    await this.run(task);
  }

  private duplicateNotice(sendAnyway: () => void): void {
    toasts.show('info', 'Already in sync', {
      detail: 'This content was sent or received recently.',
      action: { label: 'Send anyway', run: sendAnyway }
    });
  }

  retry(task: UploadTask): void {
    if (!task.prepared) return;
    task.controller = new AbortController();
    task.error = null;
    void this.run(task);
  }

  async cancel(task: UploadTask): Promise<void> {
    task.controller.abort();
    const wasChunked = task.prepared && task.prepared.content.length > session.limits.inline_max_bytes;
    task.phase = 'canceled';
    task.prepared = null;
    this.dismiss(task);
    if (wasChunked && task.itemId) {
      try {
        await request('DELETE', `/api/items/${task.itemId}`);
      } catch {
        return;
      }
    }
  }

  private fail(task: UploadTask, err: unknown, retryable: boolean): void {
    if (err instanceof AbortedError) return;
    task.phase = 'error';
    task.error = errorMessage(err);
    task.retryable = retryable && !!task.prepared;
    activity.error('upload', `Upload of ${task.label} failed: ${task.error}`);
  }

  private async run(task: UploadTask): Promise<void> {
    const p = task.prepared;
    const keys = session.keys;
    if (!p || !keys) return;
    const limits = session.limits;
    const signal = task.controller.signal;
    const plan = planChunks(p.content.length, limits.inline_max_bytes, limits.chunk_size_bytes);
    if (!task.itemId) task.itemId = uuidv7();
    task.phase = 'uploading';
    task.error = null;
    let idConflictRetried = false;
    let keyCheckRetried = false;
    for (;;) {
      try {
        const id = task.itemId;
        const metaB64 = await sealMeta(keys.aes, id, p.meta);
        if (p.thumb) {
          const sealedThumb = await sealThumb(keys.aes, id, p.thumb);
          await request('PUT', `/api/items/${id}/thumb`, { raw: sealedThumb, retries: 3, signal });
        }
        let header;
        if (plan.inline) {
          task.total = p.content.length + SEAL_OVERHEAD;
          const payload = await sealPayload(keys.aes, id, p.content);
          const body = buildCreateItemRequest({
            id,
            kind: p.kind,
            size: p.content.length,
            contentHash: p.hash,
            meta: metaB64,
            payload
          });
          task.sent = task.total / 2;
          header = parseItemHeader(await request('POST', '/api/items', { body, retries: 3, signal }));
          task.sent = task.total;
        } else {
          task.total = p.content.length + plan.chunkCount * SEAL_OVERHEAD;
          const committed = await this.uploadChunks(task, plan, p, signal);
          if (committed) {
            header = committed;
          } else {
            task.phase = 'committing';
            header = await this.commit(task, plan, p, metaB64, signal);
          }
        }
        rememberHash(p.hash);
        await history.upsert([header], { fresh: true });
        task.phase = 'done';
        task.prepared = null;
        activity.info('upload', `Sent ${p.kind} item ${header.id} (${formatBytes(p.content.length)})`);
        this.scheduleDismiss(task);
        return;
      } catch (err) {
        if (err instanceof AbortedError) return;
        if (err instanceof ApiError) {
          if (err.code === 'id_conflict' && !idConflictRetried) {
            idConflictRetried = true;
            task.itemId = uuidv7();
            task.resetChunks();
            activity.warn('upload', 'Item id conflict, retrying with a new id');
            continue;
          }
          if (err.code === 'key_check_missing' && !keyCheckRetried) {
            keyCheckRetried = true;
            try {
              await session.ensureKeyCheck();
            } catch (e) {
              this.fail(task, e instanceof WrongPasswordError ? new Error('The encryption password changed.') : e, false);
              return;
            }
            continue;
          }
          if (err.code === 'item_too_large') {
            this.fail(task, err, false);
            void request('DELETE', `/api/items/${task.itemId}`).catch(() => undefined);
            task.prepared = null;
            return;
          }
          if (err.code === 'disk_low') {
            this.fail(task, err, true);
            toasts.warning('Server disk is low', 'Large uploads are paused. Retry when space is available.');
            return;
          }
          this.fail(task, err, err.retryable || err.status === 409);
          return;
        }
        this.fail(task, err, true);
        return;
      }
    }
  }

  private async uploadChunks(task: UploadTask, plan: ChunkPlan, p: Prepared, signal: AbortSignal) {
    const keys = session.keys!;
    const indices = Array.from({ length: plan.chunkCount }, (_, i) => i).filter((i) => !task.isChunkDone(i));
    for (let i = 0; i < plan.chunkCount; i++) {
      if (task.isChunkDone(i)) task.setChunkProgress(i, chunkRange(plan, i).end - chunkRange(plan, i).start + SEAL_OVERHEAD);
    }
    let committedHeader: ReturnType<typeof parseItemHeader> | null = null;
    let cursor = 0;
    const worker = async () => {
      while (cursor < indices.length && !committedHeader) {
        const index = indices[cursor++];
        await this.uploadChunk(task, plan, p, index, keys.aes, signal).catch(async (err) => {
          if (err instanceof ApiError && err.code === 'already_committed') {
            committedHeader = parseItemHeader(await request('GET', `/api/items/${task.itemId}`, { retries: 3 }));
            return;
          }
          throw err;
        });
      }
    };
    await Promise.all(Array.from({ length: Math.min(CHUNK_PARALLEL, indices.length) }, worker));
    return committedHeader;
  }

  private async uploadChunk(task: UploadTask, plan: ChunkPlan, p: Prepared, index: number, aes: CryptoKey, signal: AbortSignal) {
    const r = chunkRange(plan, index);
    for (let attempt = 0; ; attempt++) {
      try {
        const sealed = await sealChunk(aes, task.itemId, index, plan.chunkCount, p.content.subarray(r.start, r.end));
        await uploadWithProgress(
          'PUT',
          `/api/items/${task.itemId}/chunks/${index}`,
          sealed,
          (loaded) => task.setChunkProgress(index, loaded),
          signal
        );
        task.setChunkProgress(index, sealed.length);
        task.markChunkDone(index);
        return;
      } catch (err) {
        task.setChunkProgress(index, 0);
        if (err instanceof ApiError && err.retryable && attempt < CHUNK_ATTEMPTS - 1) {
          activity.warn('upload', `Chunk ${index} failed (${errorMessage(err)}), retrying`);
          await sleep(retryDelayMs(attempt), signal);
          continue;
        }
        throw err;
      }
    }
  }

  private async commit(task: UploadTask, plan: ChunkPlan, p: Prepared, metaB64: string, signal: AbortSignal) {
    const body = buildCommitRequest({
      kind: p.kind,
      size: p.content.length,
      chunkCount: plan.chunkCount,
      contentHash: p.hash,
      meta: metaB64
    });
    for (let round = 0; ; round++) {
      try {
        return parseItemHeader(await request('POST', `/api/items/${task.itemId}/commit`, { body, retries: 3, signal }));
      } catch (err) {
        if (err instanceof ApiError && err.code === 'missing_chunks' && round < 3) {
          const missing = Array.isArray(err.details?.missing)
            ? (err.details.missing as unknown[]).filter((x): x is number => typeof x === 'number')
            : [];
          activity.warn('upload', `Server is missing ${missing.length} chunk(s), uploading them again`);
          for (const i of missing.length ? missing : Array.from({ length: plan.chunkCount }, (_, i) => i)) {
            if (i >= 0 && i < plan.chunkCount) {
              await this.uploadChunk(task, plan, p, i, session.keys!.aes, signal);
            }
          }
          continue;
        }
        throw err;
      }
    }
  }
}

export const uploads = new Uploads();
