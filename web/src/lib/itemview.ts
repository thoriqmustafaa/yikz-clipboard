import type { IconName } from './components/Icon.svelte';
import { firstLine, formatBytes, pluralize } from './format';
import type { CachedItem } from './history.svelte';
import type { Platform } from './protocol/messages';

export function iconFor(item: CachedItem): IconName {
  if (item.err) return 'alert';
  if (item.kind === 'image') return 'image';
  if (item.kind === 'files') return (item.m?.files?.length ?? 0) > 1 ? 'files' : 'file';
  return item.link ? 'link' : 'text';
}

export function titleFor(item: CachedItem): string {
  if (item.err === 'unsupported') return 'Unsupported item (created by a newer app)';
  if (item.err === 'corrupt' || !item.m) return 'Unable to decrypt this item';
  if (item.kind === 'image') {
    const d = item.m.image;
    return d ? `Image ${d.width} × ${d.height}` : 'Image';
  }
  if (item.kind === 'files') {
    const files = item.m.files ?? [];
    if (files.length === 1) return files[0].name;
    if (files.length > 1) return `${files[0].name} and ${pluralize(files.length - 1, 'more file')}`;
    return 'Files';
  }
  const line = firstLine(item.m.preview, 240);
  if (line) return line;
  return item.m.preview.trim() ? item.m.preview.trim().slice(0, 240) : 'Whitespace';
}

export function sizeLabel(item: CachedItem): string {
  return formatBytes(item.size);
}

export function platformIcon(p: Platform | undefined): IconName {
  if (p === 'macos') return 'laptop';
  if (p === 'windows') return 'monitor';
  if (p === 'android') return 'phone';
  return 'globe';
}

export function platformLabel(p: Platform | undefined): string {
  if (p === 'macos') return 'macOS';
  if (p === 'windows') return 'Windows';
  if (p === 'android') return 'Android';
  return 'Web';
}
