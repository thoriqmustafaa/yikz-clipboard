export type LogLevel = 'debug' | 'info' | 'warn' | 'error';
export type LogCategory = 'connection' | 'sync' | 'upload' | 'download' | 'auth' | 'app';

export interface LogEntry {
  id: number;
  ts: number;
  level: LogLevel;
  category: LogCategory;
  message: string;
}

const MAX_ENTRIES = 1000;

class ActivityLog {
  entries = $state.raw<LogEntry[]>([]);
  private nextId = 1;

  add(level: LogLevel, category: LogCategory, message: string): void {
    const entry: LogEntry = { id: this.nextId++, ts: Date.now(), level, category, message };
    const next =
      this.entries.length >= MAX_ENTRIES ? this.entries.slice(this.entries.length - MAX_ENTRIES + 1) : this.entries.slice();
    next.push(entry);
    this.entries = next;
    if (import.meta.env.DEV) {
      const fn = level === 'error' ? console.error : level === 'warn' ? console.warn : console.debug;
      fn(`[${category}] ${message}`);
    }
  }

  debug(category: LogCategory, message: string): void {
    this.add('debug', category, message);
  }

  info(category: LogCategory, message: string): void {
    this.add('info', category, message);
  }

  warn(category: LogCategory, message: string): void {
    this.add('warn', category, message);
  }

  error(category: LogCategory, message: string): void {
    this.add('error', category, message);
  }

  clear(): void {
    this.entries = [];
  }

  exportText(entries: LogEntry[] = this.entries): string {
    return entries
      .map((e) => `${new Date(e.ts).toISOString()} ${e.level.toUpperCase().padEnd(5)} ${e.category.padEnd(10)} ${e.message}`)
      .join('\n');
  }
}

export const activity = new ActivityLog();
