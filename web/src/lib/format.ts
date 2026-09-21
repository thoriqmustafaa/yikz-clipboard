const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];

export function formatBytes(n: number, digits = 1): string {
  if (!Number.isFinite(n) || n < 0) return '0 B';
  if (n < 1024) return `${n} B`;
  let v = n;
  let i = 0;
  while (v >= 1024 && i < UNITS.length - 1) {
    v /= 1024;
    i++;
  }
  const d = v >= 100 ? 0 : digits;
  return `${v.toFixed(d).replace(/\.0+$/, '')} ${UNITS[i]}`;
}

function startOfDay(ms: number): number {
  const d = new Date(ms);
  d.setHours(0, 0, 0, 0);
  return d.getTime();
}

export function dayKey(ms: number): number {
  return startOfDay(ms);
}

export function dayLabel(ms: number, now: number = Date.now()): string {
  const today = startOfDay(now);
  const day = startOfDay(ms);
  const diffDays = Math.round((today - day) / 86400000);
  if (diffDays <= 0) return 'Today';
  if (diffDays === 1) return 'Yesterday';
  const d = new Date(ms);
  if (diffDays < 7) return d.toLocaleDateString(undefined, { weekday: 'long' });
  const sameYear = d.getFullYear() === new Date(now).getFullYear();
  return d.toLocaleDateString(
    undefined,
    sameYear ? { weekday: 'short', day: 'numeric', month: 'short' } : { day: 'numeric', month: 'short', year: 'numeric' }
  );
}

export function formatClock(ms: number): string {
  return new Date(ms).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
}

export function formatDateTime(ms: number, now: number = Date.now()): string {
  const label = dayLabel(ms, now);
  const time = formatClock(ms);
  if (label === 'Today' || label === 'Yesterday') return `${label} at ${time}`;
  return `${new Date(ms).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })} at ${time}`;
}

export function formatFull(ms: number): string {
  return new Date(ms).toLocaleString(undefined, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  });
}

export function relativeTime(ms: number, now: number = Date.now()): string {
  const s = Math.round((now - ms) / 1000);
  if (s < 10) return 'just now';
  if (s < 60) return `${s}s ago`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m} min ago`;
  const h = Math.round(m / 60);
  if (h < 24) return `${h} h ago`;
  const d = Math.round(h / 24);
  if (d < 30) return `${d} d ago`;
  return new Date(ms).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
}

export function pluralize(n: number, one: string, many: string = `${one}s`): string {
  return `${n} ${n === 1 ? one : many}`;
}

export function firstLine(s: string, max = 200): string {
  const trimmed = s.replace(/^\s+/, '');
  const nl = trimmed.search(/[\r\n]/);
  const line = nl >= 0 ? trimmed.slice(0, nl) : trimmed;
  return line.length > max ? line.slice(0, max) : line;
}
