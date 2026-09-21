import { ApiError, errorMessage, NetworkError, request } from './api';
import { backoffDelayMs, closeAction } from './closecodes';
import { type CachedItem, history } from './history.svelte';
import { idbGet, idbPut } from './idb';
import { activity } from './log.svelte';
import { AUTO_APPLY_MAX_AGE_MS, DEAD_TIMEOUT_MS, FOREGROUND_PROBE_MS, HEARTBEAT_INTERVAL_MS } from './protocol/constants';
import {
  buildHello,
  buildPing,
  buildPong,
  type Device,
  type OnlineDevice,
  parseDevices,
  parseHistoryIndex,
  parseServerMessage,
  parseTimestamp,
  type ServerMessage,
  type StorageWarningMessage,
  type WelcomeMessage
} from './protocol/messages';
import { session } from './session.svelte';
import { APP_VERSION } from './version';

export type ConnStatus = 'idle' | 'connecting' | 'connected' | 'waiting' | 'offline' | 'paused' | 'update' | 'stopped';

interface SyncState {
  serverId: string | null;
  lastSeq: number;
  stateRev: number | null;
  appliedSeq: number;
}

const KV_SYNC = 'sync';
const HANDSHAKE_TIMEOUT_MS = 15000;

type ApplyHandler = (item: CachedItem) => Promise<void>;

class Sync {
  status = $state<ConnStatus>('idle');
  retryAt = $state<number | null>(null);
  onlineDevices = $state.raw<OnlineDevice[]>([]);
  devices = $state.raw<Device[]>([]);
  storageWarning = $state.raw<StorageWarningMessage | null>(null);
  connectedSince = $state<number | null>(null);
  catchingUp = $state(false);
  lastMessageAt = $state(0);
  serverVersion = $state('');
  attempt = $state(0);
  offset = 0;
  private state: SyncState = {
    serverId: null,
    lastSeq: 0,
    stateRev: null,
    appliedSeq: 0
  };
  private ws: WebSocket | null = null;
  private gen = 0;
  private running = false;
  private opened = false;
  private pingTimer: ReturnType<typeof setInterval> | null = null;
  private watchdogTimer: ReturnType<typeof setInterval> | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private probeTimer: ReturnType<typeof setTimeout> | null = null;
  private handshakeTimer: ReturnType<typeof setTimeout> | null = null;
  private saveTimer: ReturnType<typeof setTimeout> | null = null;
  private liveDuringCatchup: CachedItem[] = [];
  private liveMaxSeq = 0;
  private reconcileRunning: Promise<void> | null = null;
  private reconcileAgain = false;
  private queue: Promise<void> = Promise.resolve();
  private highestAppliedSeq = 0;
  private applyHandler: ApplyHandler | null = null;
  private authLost: (() => void) | null = null;
  private catchUpRun = 0;
  private listenersAttached = false;

  setApplyHandler(fn: ApplyHandler): void {
    this.applyHandler = fn;
  }

  setAuthLostHandler(fn: () => void): void {
    this.authLost = fn;
  }

  get serverId(): string | null {
    return this.state.serverId;
  }

  async loadState(): Promise<string | null> {
    const s = await idbGet<SyncState>('kv', KV_SYNC);
    if (s && typeof s.lastSeq === 'number') {
      this.state = {
        serverId: s.serverId ?? null,
        lastSeq: s.lastSeq,
        stateRev: typeof s.stateRev === 'number' ? s.stateRev : null,
        appliedSeq: typeof s.appliedSeq === 'number' ? s.appliedSeq : 0
      };
    } else {
      this.state = {
        serverId: null,
        lastSeq: 0,
        stateRev: null,
        appliedSeq: 0
      };
    }
    return this.state.serverId;
  }

  private saveSoon(): void {
    if (this.saveTimer) return;
    this.saveTimer = setTimeout(() => {
      this.saveTimer = null;
      void idbPut('kv', KV_SYNC, { ...this.state });
    }, 300);
  }

  private async saveNow(): Promise<void> {
    if (this.saveTimer) {
      clearTimeout(this.saveTimer);
      this.saveTimer = null;
    }
    await idbPut('kv', KV_SYNC, { ...this.state });
  }

  start(): void {
    if (this.running) return;
    this.running = true;
    this.attach();
    activity.info('connection', 'Sync started');
    this.connect();
  }

  stop(): void {
    this.running = false;
    this.detach();
    this.teardown();
    this.clearRetry();
    this.status = 'stopped';
    this.onlineDevices = [];
    this.connectedSince = null;
  }

  private attach(): void {
    if (this.listenersAttached) return;
    this.listenersAttached = true;
    window.addEventListener('online', this.onOnline);
    window.addEventListener('offline', this.onOffline);
    document.addEventListener('visibilitychange', this.onVisibility);
    window.addEventListener('pageshow', this.onPageShow);
  }

  private detach(): void {
    if (!this.listenersAttached) return;
    this.listenersAttached = false;
    window.removeEventListener('online', this.onOnline);
    window.removeEventListener('offline', this.onOffline);
    document.removeEventListener('visibilitychange', this.onVisibility);
    window.removeEventListener('pageshow', this.onPageShow);
  }

  private onOnline = () => {
    activity.info('connection', 'Network available, reconnecting now');
    this.reconnectNow(true);
  };

  private onOffline = () => {
    activity.warn('connection', 'Browser reports offline');
    this.teardown();
    this.clearRetry();
    this.status = 'offline';
  };

  private onPageShow = (e: PageTransitionEvent) => {
    if (e.persisted) {
      activity.info('connection', 'Page restored from cache, reconnecting');
      this.reconnectNow(true);
    }
  };

  private onVisibility = () => {
    if (document.visibilityState !== 'visible' || !this.running) return;
    if (this.status === 'connected' && this.ws) {
      const since = Date.now();
      this.send(buildPing(since));
      if (this.probeTimer) clearTimeout(this.probeTimer);
      this.probeTimer = setTimeout(() => {
        this.probeTimer = null;
        if (this.lastMessageAt < since) {
          activity.warn('connection', 'No reply after returning to foreground, reconnecting');
          this.reconnectNow(true);
        }
      }, FOREGROUND_PROBE_MS);
      return;
    }
    if (this.status !== 'connecting' && this.status !== 'update') {
      activity.info('connection', 'Returned to foreground, reconnecting now');
      this.reconnectNow(false);
    }
  };

  reconnectNow(forceClose: boolean): void {
    if (!this.running || this.status === 'update') return;
    this.attempt = 0;
    this.clearRetry();
    if (!forceClose && this.ws && (this.status === 'connected' || this.status === 'connecting')) return;
    this.teardown();
    this.connect();
  }

  private clearRetry(): void {
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retryTimer = null;
    this.retryAt = null;
  }

  private teardown(code = 1000): void {
    this.gen++;
    if (this.pingTimer) clearInterval(this.pingTimer);
    if (this.watchdogTimer) clearInterval(this.watchdogTimer);
    if (this.probeTimer) clearTimeout(this.probeTimer);
    if (this.handshakeTimer) clearTimeout(this.handshakeTimer);
    this.handshakeTimer = null;
    this.pingTimer = null;
    this.watchdogTimer = null;
    this.probeTimer = null;
    const ws = this.ws;
    this.ws = null;
    if (ws) {
      ws.onopen = null;
      ws.onmessage = null;
      ws.onerror = null;
      ws.onclose = null;
      try {
        ws.close(code);
      } catch {
        ws.close();
      }
    }
    this.connectedSince = null;
  }

  private connect(): void {
    if (!this.running) return;
    if (typeof navigator !== 'undefined' && navigator.onLine === false) {
      this.status = 'offline';
      return;
    }
    const token = session.token;
    if (!token) return;
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = `${proto}//${location.host}/ws?token=${encodeURIComponent(token)}`;
    const gen = ++this.gen;
    this.opened = false;
    this.status = 'connecting';
    let ws: WebSocket;
    try {
      ws = new WebSocket(url);
    } catch (err) {
      activity.error('connection', `Could not open WebSocket: ${String(err)}`);
      this.scheduleReconnect();
      return;
    }
    this.ws = ws;
    this.handshakeTimer = setTimeout(() => {
      this.handshakeTimer = null;
      if (gen !== this.gen || this.status === 'connected') return;
      activity.warn('connection', 'No welcome within 15 s, retrying');
      const wasOpen = this.opened;
      this.teardown(4004);
      void this.handleClose(4004, 'handshake timeout', wasOpen);
    }, HANDSHAKE_TIMEOUT_MS);
    ws.onopen = () => {
      if (gen !== this.gen) return;
      this.opened = true;
      this.lastMessageAt = Date.now();
      activity.debug('connection', 'Socket open, sending hello');
      this.send(
        buildHello({
          deviceId: session.deviceId ?? '',
          lastSeq: this.state.lastSeq,
          appVersion: APP_VERSION,
          platform: 'web'
        })
      );
      this.pingTimer = setInterval(() => this.send(buildPing()), HEARTBEAT_INTERVAL_MS);
      this.watchdogTimer = setInterval(() => this.checkDead(), 1000);
    };
    ws.onmessage = (e) => {
      if (gen !== this.gen) return;
      this.lastMessageAt = Date.now();
      if (typeof e.data !== 'string') return;
      let msg: ServerMessage | null;
      try {
        msg = parseServerMessage(e.data);
      } catch (err) {
        activity.warn('connection', `Ignored malformed message: ${String(err)}`);
        return;
      }
      if (!msg) return;
      this.dispatch(msg, gen);
    };
    ws.onclose = (e) => {
      if (gen !== this.gen) return;
      const wasOpen = this.opened;
      this.teardown();
      void this.handleClose(e.code, e.reason, wasOpen);
    };
    ws.onerror = () => {
      if (gen !== this.gen) return;
      activity.debug('connection', 'WebSocket error');
    };
  }

  private send(msg: object): void {
    const ws = this.ws;
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    try {
      ws.send(JSON.stringify(msg));
    } catch {
      return;
    }
  }

  private checkDead(): void {
    if (!this.ws) return;
    if (Date.now() - this.lastMessageAt >= DEAD_TIMEOUT_MS) {
      activity.warn('connection', 'No traffic for 45 s, connection considered dead');
      this.teardown(4005);
      void this.handleClose(4005, 'heartbeat timeout', true);
    }
  }

  private async handleClose(code: number, reason: string, wasOpen: boolean): Promise<void> {
    if (!this.running) return;
    this.onlineDevices = [];
    const action = closeAction(code);
    activity.add(
      action === 'reconnect' ? 'warn' : 'error',
      'connection',
      `Connection closed (code ${code}${reason ? `, ${reason}` : ''})`
    );
    if (!wasOpen && action === 'reconnect') {
      try {
        await request('GET', '/api/me');
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) return;
      }
      if (!this.running) return;
    }
    if (action === 'login') {
      this.running = false;
      this.status = 'stopped';
      this.authLost?.();
      return;
    }
    if (action === 'update') {
      this.status = 'update';
      session.showUpdate('The server does not support this app version. Update the web app.');
      return;
    }
    if (action === 'paused') {
      this.status = 'paused';
      activity.warn('connection', 'Too many connections for this device. Paused until you return to this tab.');
      return;
    }
    this.scheduleReconnect();
  }

  private scheduleReconnect(): void {
    if (!this.running) return;
    if (typeof navigator !== 'undefined' && navigator.onLine === false) {
      this.status = 'offline';
      return;
    }
    const delay = backoffDelayMs(this.attempt);
    this.attempt = this.attempt + 1;
    this.retryAt = Date.now() + delay;
    this.status = 'waiting';
    activity.debug('connection', `Reconnecting in ${(delay / 1000).toFixed(1)} s (attempt ${this.attempt})`);
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      this.retryAt = null;
      this.connect();
    }, delay);
  }

  private dispatch(msg: ServerMessage, gen: number): void {
    switch (msg.type) {
      case 'ping':
        this.send(buildPong(msg.ts));
        return;
      case 'pong':
        return;
      case 'welcome':
        this.queue = this.queue.then(() => this.onWelcome(msg, gen)).catch((err) => this.onSyncError(err));
        return;
      default:
        this.queue = this.queue.then(() => this.handle(msg)).catch((err) => this.onSyncError(err));
    }
  }

  private onSyncError(err: unknown): void {
    activity.error('sync', `Sync error: ${errorMessage(err)}`);
  }

  private async onWelcome(w: WelcomeMessage, gen: number): Promise<void> {
    if (gen !== this.gen) return;
    if (this.handshakeTimer) clearTimeout(this.handshakeTimer);
    this.handshakeTimer = null;
    this.attempt = 0;
    this.status = 'connected';
    this.connectedSince = Date.now();
    this.serverVersion = w.server_version;
    this.onlineDevices = w.online_devices;
    try {
      this.offset = parseTimestamp(w.server_time) - Date.now();
    } catch {
      this.offset = 0;
    }
    activity.info('connection', `Connected (seq ${w.current_seq}, rev ${w.state_rev}, ${w.online_devices.length} online)`);
    if (this.state.serverId && this.state.serverId !== w.server_id) {
      activity.warn('sync', 'Server id changed, clearing the local cache');
      await history.clear();
      this.state = {
        serverId: w.server_id,
        lastSeq: 0,
        stateRev: null,
        appliedSeq: 0
      };
      history.serverId = w.server_id;
      await this.saveNow();
      let ok = false;
      try {
        ok = await session.revalidateKeys();
      } catch (err) {
        activity.error('auth', `Could not revalidate the key: ${errorMessage(err)}`);
      }
      if (!ok) return;
    }
    this.state.serverId = w.server_id;
    history.serverId = w.server_id;
    this.saveSoon();
    void this.runCatchUp(w, gen);
  }

  private async runCatchUp(w: WelcomeMessage, gen: number): Promise<void> {
    const run = ++this.catchUpRun;
    this.catchingUp = true;
    history.syncing = true;
    this.liveDuringCatchup = [];
    this.liveMaxSeq = 0;
    const inserted: CachedItem[] = [];
    try {
      if (this.state.lastSeq === 0) {
        const page = await history.fetchPage({
          before: w.current_seq + 1,
          limit: 100
        });
        history.hasMore = page.hasMore;
        if (gen !== this.gen) return;
        const top = Math.max(w.current_seq, this.liveMaxSeq);
        this.state.lastSeq = top;
        this.state.appliedSeq = Math.max(this.state.appliedSeq, top);
        activity.info('sync', `Initial sync loaded ${page.items.length} items`);
      } else if (w.current_seq > this.state.lastSeq) {
        let cursor = this.state.lastSeq;
        for (;;) {
          const page = await history.fetchPage({ after: cursor, limit: 500 });
          if (gen !== this.gen) return;
          inserted.push(...page.items);
          if (page.items.length) cursor = page.items[page.items.length - 1].seq;
          if (!page.hasMore || page.items.length === 0) break;
        }
        this.state.lastSeq = Math.max(cursor, this.liveMaxSeq);
        activity.info('sync', `Caught up ${inserted.length} missed items`);
      }
      if (w.state_rev !== this.state.stateRev) await this.reconcile();
      if (gen !== this.gen) return;
      const burst = [...inserted, ...this.liveDuringCatchup];
      this.catchingUp = false;
      if (burst.length) {
        const eligible = burst.filter((i) => this.eligible(i)).sort((a, b) => b.seq - a.seq);
        const maxSeq = Math.max(...burst.map((i) => i.seq));
        if (eligible[0]) void this.apply(eligible[0]);
        this.state.appliedSeq = Math.max(this.state.appliedSeq, maxSeq);
      }
      await this.saveNow();
    } catch (err) {
      activity.error('sync', `Catch-up failed: ${errorMessage(err)}`);
      if (gen === this.gen && (err instanceof NetworkError || (err instanceof ApiError && err.retryable))) {
        this.reconnectNow(true);
      }
    } finally {
      if (run === this.catchUpRun) {
        this.catchingUp = false;
        history.syncing = false;
      }
    }
  }

  private eligible(item: CachedItem): boolean {
    const age = Date.now() + this.offset - item.createdMs;
    return item.device_id !== session.deviceId && item.seq > this.state.appliedSeq && age <= AUTO_APPLY_MAX_AGE_MS && !item.err;
  }

  private async apply(item: CachedItem): Promise<void> {
    if (!this.applyHandler) return;
    this.highestAppliedSeq = Math.max(this.highestAppliedSeq, item.seq);
    try {
      await this.applyHandler(item);
    } catch (err) {
      activity.warn('sync', `Auto-apply of ${item.id} failed: ${errorMessage(err)}`);
    }
  }

  isSuperseded(seq: number): boolean {
    return this.highestAppliedSeq > seq;
  }

  private async handle(msg: ServerMessage): Promise<void> {
    switch (msg.type) {
      case 'clip': {
        const [item] = await history.upsert([msg.item], { fresh: true });
        if (!item) return;
        activity.info('sync', `Received ${item.kind} item seq ${item.seq} from ${this.deviceName(item.device_id)}`);
        if (this.catchingUp) {
          this.liveDuringCatchup.push(item);
          this.liveMaxSeq = Math.max(this.liveMaxSeq, item.seq);
          return;
        }
        this.state.lastSeq = Math.max(this.state.lastSeq, item.seq);
        if (this.eligible(item)) void this.apply(item);
        this.state.appliedSeq = Math.max(this.state.appliedSeq, item.seq);
        this.saveSoon();
        return;
      }
      case 'clip_deleted':
        await history.remove(msg.ids);
        activity.info('sync', `${msg.ids.length} item(s) deleted (${msg.reason})`);
        this.onStateRev(msg.state_rev);
        return;
      case 'clip_pinned':
        await history.setPinned(msg.id, msg.pinned);
        activity.info('sync', `Item ${msg.pinned ? 'pinned' : 'unpinned'}`);
        this.onStateRev(msg.state_rev);
        return;
      case 'presence': {
        const others = this.onlineDevices.filter((d) => d.device_id !== msg.device_id);
        this.onlineDevices = msg.online
          ? [
              ...others,
              {
                device_id: msg.device_id,
                name: msg.name,
                platform: msg.platform
              }
            ]
          : others;
        activity.info('connection', `${msg.name} is ${msg.online ? 'online' : 'offline'}`);
        this.devices = this.devices.map((d) => (d.id === msg.device_id ? { ...d, online: msg.online } : d));
        return;
      }
      case 'devices_changed':
        await this.refreshDevices();
        return;
      case 'storage_warning':
        this.storageWarning = msg.active ? msg : null;
        activity.add(msg.active ? 'warn' : 'info', 'sync', msg.active ? 'Server disk is low' : 'Server disk space recovered');
        return;
      case 'error':
        activity.warn('connection', `Server error ${msg.code}: ${msg.message}`);
        return;
      default:
        return;
    }
  }

  private onStateRev(rev: number): void {
    const current = this.state.stateRev;
    if (current === null) {
      if (!this.catchingUp) void this.reconcile().catch((err) => this.onSyncError(err));
      return;
    }
    if (rev === current + 1) {
      this.state.stateRev = rev;
      this.saveSoon();
    } else if (rev > current + 1) {
      activity.info('sync', `Missed state changes (rev ${current} to ${rev}), reconciling`);
      void this.reconcile().catch((err) => this.onSyncError(err));
    }
  }

  reconcile(): Promise<void> {
    if (this.reconcileRunning) {
      this.reconcileAgain = true;
      return this.reconcileRunning;
    }
    this.reconcileRunning = (async () => {
      try {
        do {
          this.reconcileAgain = false;
          const idx = parseHistoryIndex(await request('GET', '/api/history/index', { retries: 3 }));
          await history.reconcile(idx);
          this.state.stateRev = idx.state_rev;
          this.saveSoon();
          activity.debug('sync', `Reconciled with state_rev ${idx.state_rev}`);
        } while (this.reconcileAgain);
      } finally {
        this.reconcileRunning = null;
      }
    })();
    return this.reconcileRunning;
  }

  async refreshDevices(): Promise<Device[]> {
    try {
      const list = parseDevices(await request('GET', '/api/devices', { retries: 2 }));
      this.devices = list;
      return list;
    } catch (err) {
      activity.warn('app', `Could not load devices: ${errorMessage(err)}`);
      return this.devices;
    }
  }

  deviceName(id: string): string {
    if (id === session.deviceId) return session.deviceName || 'This browser';
    const d = this.devices.find((x) => x.id === id);
    if (d) return d.name;
    const o = this.onlineDevices.find((x) => x.device_id === id);
    if (o) return o.name;
    return 'Unknown device';
  }

  device(id: string): Device | undefined {
    return this.devices.find((x) => x.id === id);
  }

  async resetState(): Promise<void> {
    this.state = { serverId: null, lastSeq: 0, stateRev: null, appliedSeq: 0 };
    this.devices = [];
    this.storageWarning = null;
    this.highestAppliedSeq = 0;
  }
}

export const sync = new Sync();
