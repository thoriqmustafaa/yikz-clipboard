import { ApiError, errorMessage, request } from './api';
import { canWriteImages, isNotAllowed, writePng, writeText } from './clipboard';
import { content } from './content.svelte';
import { formatBytes, pluralize } from './format';
import { type CachedItem, history } from './history.svelte';
import { activity } from './log.svelte';
import { rememberHash } from './recent';
import { imageFileName, saveBlob, textFileName } from './save';
import { settings } from './settings.svelte';
import { sync } from './sync.svelte';
import { toasts } from './toast.svelte';
import { buildPinRequest, parseItemHeader } from './protocol/messages';

export function kindLabel(item: CachedItem): string {
  if (item.err === 'unsupported') return 'Unsupported';
  if (item.err === 'corrupt') return 'Unreadable';
  if (item.kind === 'text') return item.link ? 'Link' : 'Text';
  if (item.kind === 'image') return 'Image';
  const n = item.m?.files?.length ?? 0;
  return n === 1 ? 'File' : pluralize(n, 'file');
}

export function canCopy(item: CachedItem): boolean {
  if (item.err || !item.m) return false;
  if (item.kind === 'text') return true;
  if (item.kind === 'image') return canWriteImages();
  return false;
}

export async function copyItem(item: CachedItem): Promise<boolean> {
  if (!canCopy(item)) {
    if (item.kind === 'files') return saveItem(item);
    toasts.error('Cannot copy this item');
    return false;
  }
  try {
    if (item.kind === 'text') await writeText(content.peekText(item.id) ?? content.text(item));
    else await writePng(content.pngBlob(item));
    rememberHash(item.content_hash);
    toasts.success(item.kind === 'image' ? 'Image copied' : item.link ? 'Link copied' : 'Copied to clipboard');
    activity.info('app', `Copied item ${item.id} to the clipboard`);
    return true;
  } catch (err) {
    if (isNotAllowed(err)) toasts.error('Clipboard access was blocked', 'Allow clipboard access for this site and try again.');
    else toasts.error('Copy failed', errorMessage(err));
    return false;
  }
}

export async function saveItem(item: CachedItem): Promise<boolean> {
  if (item.err || !item.m) {
    toasts.error('This item cannot be opened');
    return false;
  }
  try {
    if (item.kind === 'text') {
      const text = await content.text(item);
      saveBlob(new Blob([text], { type: 'text/plain;charset=utf-8' }), textFileName(item.m.preview, item.createdMs));
    } else if (item.kind === 'image') {
      saveBlob(await content.pngBlob(item), imageFileName(item.createdMs));
    } else {
      const files = await content.files(item);
      for (const [i, f] of files.entries()) {
        if (i > 0) await new Promise((r) => setTimeout(r, 250));
        saveBlob(f.blob, f.name);
      }
      toasts.success(files.length === 1 ? `Saved ${files[0].name}` : `Saved ${files.length} files`);
      return true;
    }
    toasts.success('Saved');
    return true;
  } catch (err) {
    toasts.error('Download failed', errorMessage(err));
    return false;
  }
}

export async function saveOneFile(item: CachedItem, index: number): Promise<void> {
  try {
    const files = await content.files(item);
    const f = files[index];
    if (f) saveBlob(f.blob, f.name);
  } catch (err) {
    toasts.error('Download failed', errorMessage(err));
  }
}

export async function togglePin(item: CachedItem): Promise<void> {
  const next = !item.pinned;
  await history.setPinned(item.id, next);
  try {
    const h = parseItemHeader(await request('POST', `/api/items/${item.id}/pin`, { body: buildPinRequest(next), retries: 2 }));
    await history.setPinned(item.id, h.pinned);
    toasts.success(h.pinned ? 'Pinned' : 'Unpinned');
  } catch (err) {
    await history.setPinned(item.id, !next);
    if (err instanceof ApiError && err.code === 'not_found') {
      await history.remove([item.id]);
      toasts.error('This item no longer exists');
      return;
    }
    toasts.error(next ? 'Could not pin' : 'Could not unpin', errorMessage(err));
  }
}

export async function deleteItem(item: CachedItem): Promise<boolean> {
  try {
    await request('DELETE', `/api/items/${item.id}`, { retries: 2 });
  } catch (err) {
    if (!(err instanceof ApiError && err.code === 'not_found')) {
      toasts.error('Could not delete', errorMessage(err));
      return false;
    }
  }
  await history.remove([item.id]);
  content.forget(item.id);
  toasts.success('Deleted');
  return true;
}

async function writeIncoming(item: CachedItem): Promise<void> {
  if (item.kind === 'text') {
    const text = await content.text(item);
    if (sync.isSuperseded(item.seq)) return;
    await writeText(text);
  } else {
    const blob = await content.pngBlob(item);
    if (sync.isSuperseded(item.seq)) return;
    await writePng(blob);
  }
  rememberHash(item.content_hash);
  activity.info('sync', `Put item seq ${item.seq} on the clipboard`);
}

export async function applyIncoming(item: CachedItem): Promise<void> {
  if (!item.m) return;
  const from = sync.deviceName(item.device_id);
  rememberHash(item.content_hash);
  const visible = typeof document !== 'undefined' && document.visibilityState === 'visible';
  if (!visible) {
    activity.debug('sync', `Item seq ${item.seq} kept in history because the page is hidden`);
    return;
  }
  if (item.kind === 'files') {
    const count = item.m.files?.length ?? 0;
    const name = count === 1 ? item.m.files![0].name : pluralize(count, 'file');
    toasts.show('info', `${name} from ${from}`, {
      detail: formatBytes(item.size),
      action: { label: 'Download', run: () => void saveItem(item) },
      duration: 8000
    });
    return;
  }
  if (item.size > settings.autoDownloadBytes) {
    toasts.show('info', `Large ${item.kind} (${formatBytes(item.size)}) from ${from}`, {
      detail: 'Above your auto-download limit.',
      action: { label: 'Download', run: () => void saveItem(item) },
      duration: 10000
    });
    return;
  }
  if (!settings.autoCopy || (item.kind === 'image' && !canWriteImages())) {
    toasts.show('info', `New ${item.link ? 'link' : item.kind} from ${from}`, {
      action: { label: 'Copy', run: () => void copyItem(item) }
    });
    return;
  }
  try {
    await writeIncoming(item);
  } catch (err) {
    activity.debug('sync', `Clipboard write not allowed: ${errorMessage(err)}`);
    toasts.show('info', `New ${item.link ? 'link' : item.kind} from ${from}`, {
      detail: 'Click Copy to put it on your clipboard.',
      action: { label: 'Copy', run: () => void copyItem(item) }
    });
  }
}
