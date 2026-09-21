import { DEFAULT_AUTO_DOWNLOAD_BYTES } from './protocol/constants';

export type Theme = 'system' | 'light' | 'dark';

interface Stored {
  theme: Theme;
  autoDownloadBytes: number;
  autoCopy: boolean;
  monospace: boolean;
}

const KEY = 'yc.settings';

function load(): Stored {
  const defaults: Stored = { theme: 'system', autoDownloadBytes: DEFAULT_AUTO_DOWNLOAD_BYTES, autoCopy: true, monospace: false };
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return defaults;
    const v = JSON.parse(raw) as Partial<Stored>;
    return {
      theme: v.theme === 'light' || v.theme === 'dark' ? v.theme : 'system',
      autoDownloadBytes:
        typeof v.autoDownloadBytes === 'number' && v.autoDownloadBytes >= 0 ? v.autoDownloadBytes : defaults.autoDownloadBytes,
      autoCopy: typeof v.autoCopy === 'boolean' ? v.autoCopy : defaults.autoCopy,
      monospace: typeof v.monospace === 'boolean' ? v.monospace : defaults.monospace
    };
  } catch {
    return defaults;
  }
}

class Settings {
  theme = $state<Theme>('system');
  autoDownloadBytes = $state(DEFAULT_AUTO_DOWNLOAD_BYTES);
  autoCopy = $state(true);
  monospace = $state(false);

  constructor() {
    const s = load();
    this.theme = s.theme;
    this.autoDownloadBytes = s.autoDownloadBytes;
    this.autoCopy = s.autoCopy;
    this.monospace = s.monospace;
  }

  save(): void {
    try {
      localStorage.setItem(
        KEY,
        JSON.stringify({
          theme: this.theme,
          autoDownloadBytes: this.autoDownloadBytes,
          autoCopy: this.autoCopy,
          monospace: this.monospace
        })
      );
    } catch {
      return;
    }
  }

  applyTheme(): void {
    const root = document.documentElement;
    if (this.theme === 'system') root.removeAttribute('data-theme');
    else root.setAttribute('data-theme', this.theme);
  }

  reset(): void {
    try {
      localStorage.removeItem(KEY);
    } catch {
      return;
    }
  }
}

export const settings = new Settings();
