import { dayKey, dayLabel } from './format';
import { type CachedItem, matchesType, type TypeFilter } from './history.svelte';

export interface Group {
  key: string;
  label: string;
  items: CachedItem[];
}

export function filterItems(items: CachedItem[], query: string, type: TypeFilter): CachedItem[] {
  const q = query.trim().toLowerCase();
  const terms = q ? q.split(/\s+/) : [];
  return items.filter((it) => {
    if (!matchesType(it, type)) return false;
    if (terms.length === 0) return true;
    return terms.every((t) => it.search.includes(t));
  });
}

export function groupItems(items: CachedItem[], now: number = Date.now()): Group[] {
  const groups: Group[] = [];
  const pinned = items.filter((i) => i.pinned);
  if (pinned.length) groups.push({ key: 'pinned', label: 'Pinned', items: pinned });
  let current: Group | null = null;
  for (const it of items) {
    if (it.pinned) continue;
    const k = `d${dayKey(it.createdMs)}`;
    if (!current || current.key !== k) {
      current = { key: k, label: dayLabel(it.createdMs, now), items: [] };
      groups.push(current);
    }
    current.items.push(it);
  }
  return groups;
}

export function flatten(groups: Group[]): CachedItem[] {
  const out: CachedItem[] = [];
  for (const g of groups) out.push(...g.items);
  return out;
}
