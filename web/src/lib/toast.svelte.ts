export type ToastTone = 'info' | 'success' | 'error' | 'warning';

export interface Toast {
  id: number;
  tone: ToastTone;
  title: string;
  detail?: string;
  action?: { label: string; run: () => void };
}

class Toasts {
  list = $state<Toast[]>([]);
  private nextId = 1;
  private timers = new Map<number, ReturnType<typeof setTimeout>>();

  show(tone: ToastTone, title: string, opts: { detail?: string; action?: Toast['action']; duration?: number } = {}): number {
    const id = this.nextId++;
    this.list = [...this.list.slice(-3), { id, tone, title, detail: opts.detail, action: opts.action }];
    const duration = opts.duration ?? (tone === 'error' ? 6000 : 3200);
    if (duration > 0)
      this.timers.set(
        id,
        setTimeout(() => this.dismiss(id), duration)
      );
    return id;
  }

  info(title: string, detail?: string): number {
    return this.show('info', title, { detail });
  }

  success(title: string, detail?: string): number {
    return this.show('success', title, { detail });
  }

  error(title: string, detail?: string): number {
    return this.show('error', title, { detail });
  }

  warning(title: string, detail?: string): number {
    return this.show('warning', title, { detail });
  }

  dismiss(id: number): void {
    const t = this.timers.get(id);
    if (t) clearTimeout(t);
    this.timers.delete(id);
    this.list = this.list.filter((x) => x.id !== id);
  }
}

export const toasts = new Toasts();
