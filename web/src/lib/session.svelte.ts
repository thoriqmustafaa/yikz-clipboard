import { ApiError, errorMessage, request } from './api';
import { idbDelete, idbGet, idbPut } from './idb';
import { deriveKeysOffThread } from './kdf';
import { activity } from './log.svelte';
import { defaultDeviceName } from './platform';
import { DEFAULT_LIMITS, KDF_ITERATIONS, type Limits, PROTOCOL_VERSION } from './protocol/constants';
import type { KeySet } from './protocol/crypto';
import { fromBase64 } from './protocol/encoding';
import {
  buildKeyCheckRequest,
  buildLoginRequest,
  kdfSupported,
  type Kdf,
  type MeResponse,
  parseLoginResponse,
  parseMe
} from './protocol/messages';

export type Phase = 'boot' | 'login' | 'unlock' | 'ready' | 'update';
export type UnlockMode = 'unlock' | 'setup';

const LS_TOKEN = 'yc.token';
const LS_DEVICE_ID = 'yc.device_id';
const LS_USERNAME = 'yc.username';
const LS_SALT = 'yc.salt';
const LS_DEVICE_NAME = 'yc.device_name';
const KV_KEYS = 'keys';

interface StoredKeys {
  aes: CryptoKey;
  contentHash: CryptoKey;
  keyCheck: string;
  salt: string;
}

export class WrongPasswordError extends Error {
  constructor() {
    super('Wrong encryption password');
    this.name = 'WrongPasswordError';
  }
}

export class UnsupportedServerError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'UnsupportedServerError';
  }
}

function lsGet(k: string): string | null {
  try {
    return localStorage.getItem(k);
  } catch {
    return null;
  }
}

function lsSet(k: string, v: string | null): void {
  try {
    if (v === null) localStorage.removeItem(k);
    else localStorage.setItem(k, v);
  } catch {
    return;
  }
}

type Listener = () => void | Promise<void>;

class Session {
  phase = $state<Phase>('boot');
  unlockMode = $state<UnlockMode>('unlock');
  notice = $state<string | null>(null);
  deriving = $state(false);
  username = $state<string | null>(null);
  deviceId = $state<string | null>(null);
  deviceName = $state('');
  me = $state.raw<MeResponse | null>(null);
  limits = $state.raw<Limits>(DEFAULT_LIMITS);
  updateReason = $state('');
  token: string | null = null;
  keys: KeySet | null = null;
  salt: string | null = null;
  pendingSetupPassword: string | null = null;
  private readyListeners: Listener[] = [];
  private leaveListeners: Listener[] = [];

  constructor() {
    this.token = lsGet(LS_TOKEN);
    this.deviceId = lsGet(LS_DEVICE_ID);
    this.username = lsGet(LS_USERNAME);
    this.salt = lsGet(LS_SALT);
    this.deviceName = lsGet(LS_DEVICE_NAME) ?? '';
  }

  onReady(fn: Listener): void {
    this.readyListeners.push(fn);
  }

  onLeave(fn: Listener): void {
    this.leaveListeners.push(fn);
  }

  private async enterReady(): Promise<void> {
    this.phase = 'ready';
    this.notice = null;
    for (const fn of this.readyListeners) await fn();
  }

  private async leave(phase: Phase): Promise<void> {
    const wasReady = this.phase === 'ready';
    this.phase = phase;
    if (wasReady) for (const fn of this.leaveListeners) await fn();
  }

  private checkServer(protocolVersion: number, kdf: Kdf): void {
    if (protocolVersion !== PROTOCOL_VERSION) {
      throw new UnsupportedServerError(`The server speaks protocol version ${protocolVersion}. Update this app.`);
    }
    if (!kdfSupported(kdf) || kdf.iterations !== KDF_ITERATIONS) {
      throw new UnsupportedServerError('The server uses unsupported key derivation settings. Update this app.');
    }
  }

  async fetchMe(): Promise<MeResponse> {
    const me = parseMe(await request('GET', '/api/me', { retries: 2 }));
    this.me = me;
    this.limits = me.limits;
    this.username = me.username;
    this.deviceName = me.device.name;
    lsSet(LS_USERNAME, me.username);
    lsSet(LS_DEVICE_NAME, me.device.name);
    return me;
  }

  async boot(): Promise<void> {
    if (!this.token) {
      this.phase = 'login';
      return;
    }
    const stored = await idbGet<StoredKeys>('kv', KV_KEYS);
    let me: MeResponse | null = null;
    try {
      me = await this.fetchMe();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) return;
      activity.warn('auth', `Could not reach the server at startup: ${errorMessage(err)}`);
    }
    if (me) {
      try {
        this.checkServer(me.protocol_version, me.kdf);
      } catch (err) {
        this.updateReason = (err as Error).message;
        this.phase = 'update';
        return;
      }
      this.salt = me.salt;
      lsSet(LS_SALT, me.salt);
    }
    if (stored && stored.aes && stored.contentHash) {
      const keys: KeySet = {
        aes: stored.aes,
        contentHash: stored.contentHash,
        keyCheck: stored.keyCheck
      };
      if (!me) {
        this.keys = keys;
        await this.enterReady();
        return;
      }
      if (stored.salt !== me.salt) {
        await this.discardKeys('The account encryption salt changed. Enter your encryption password again.');
        return;
      }
      if (me.key_check === keys.keyCheck) {
        this.keys = keys;
        await this.enterReady();
        return;
      }
      if (me.key_check === null) {
        const ok = await this.acceptKeyCheck(keys, null).catch(() => false);
        if (ok) {
          this.keys = keys;
          await this.enterReady();
          return;
        }
      }
      await this.discardKeys('The encryption password changed on another device. Enter the new one.');
      return;
    }
    this.unlockMode = me && me.key_check === null ? 'setup' : 'unlock';
    this.phase = 'unlock';
  }

  async login(input: { username: string; password: string; encryptionPassword: string; deviceName: string }): Promise<void> {
    const body = buildLoginRequest({
      username: input.username.trim(),
      password: input.password,
      deviceName: input.deviceName || defaultDeviceName(),
      platform: 'web',
      deviceId: this.deviceId
    });
    const res = parseLoginResponse(await request('POST', '/api/login', { body, auth: false }));
    this.checkServer(res.protocol_version, res.kdf);
    fromBase64(res.salt);
    this.token = res.token;
    this.deviceId = res.device_id;
    this.username = res.username;
    this.salt = res.salt;
    this.deviceName = body.device_name;
    lsSet(LS_TOKEN, res.token);
    lsSet(LS_DEVICE_ID, res.device_id);
    lsSet(LS_USERNAME, res.username);
    lsSet(LS_SALT, res.salt);
    lsSet(LS_DEVICE_NAME, body.device_name);
    activity.info('auth', `Signed in as ${res.username} (device ${res.device_id})`);
    if (res.key_check === null) {
      this.pendingSetupPassword = input.encryptionPassword;
      this.unlockMode = 'setup';
      this.notice = null;
      this.phase = 'unlock';
      return;
    }
    try {
      await this.deriveAndAccept(input.encryptionPassword, res.key_check);
    } catch (err) {
      this.unlockMode = 'unlock';
      this.phase = 'unlock';
      throw err;
    }
  }

  async unlock(password: string): Promise<void> {
    const me = await this.fetchMe();
    this.checkServer(me.protocol_version, me.kdf);
    this.salt = me.salt;
    lsSet(LS_SALT, me.salt);
    await this.deriveAndAccept(password, me.key_check);
  }

  private async deriveAndAccept(password: string, serverKeyCheck: string | null): Promise<void> {
    if (!this.salt) throw new Error('Missing account salt');
    this.deriving = true;
    let keys: KeySet;
    const started = performance.now();
    try {
      keys = await deriveKeysOffThread(password, fromBase64(this.salt), KDF_ITERATIONS);
    } finally {
      this.deriving = false;
    }
    activity.debug('auth', `Derived key in ${Math.round(performance.now() - started)} ms`);
    const ok = await this.acceptKeyCheck(keys, serverKeyCheck);
    if (!ok) {
      activity.warn('auth', 'Encryption password rejected by key check');
      throw new WrongPasswordError();
    }
    this.keys = keys;
    this.pendingSetupPassword = null;
    await idbPut('kv', KV_KEYS, {
      aes: keys.aes,
      contentHash: keys.contentHash,
      keyCheck: keys.keyCheck,
      salt: this.salt
    } satisfies StoredKeys);
    await this.enterReady();
  }

  async acceptKeyCheck(keys: KeySet, serverKeyCheck: string | null): Promise<boolean> {
    if (serverKeyCheck !== null) return serverKeyCheck === keys.keyCheck;
    try {
      await request('PUT', '/api/account/key-check', {
        body: buildKeyCheckRequest(keys.keyCheck),
        retries: 2
      });
      activity.info('auth', 'Stored the key check for this account');
      return true;
    } catch (err) {
      if (err instanceof ApiError && err.code === 'key_check_exists') {
        const me = await this.fetchMe();
        return me.key_check === keys.keyCheck;
      }
      throw err;
    }
  }

  async ensureKeyCheck(): Promise<void> {
    if (!this.keys) throw new Error('Locked');
    const ok = await this.acceptKeyCheck(this.keys, null);
    if (!ok) {
      await this.discardKeys('The encryption password changed on another device. Enter the new one.');
      throw new WrongPasswordError();
    }
  }

  async revalidateKeys(): Promise<boolean> {
    const me = await this.fetchMe();
    if (!this.keys) return false;
    if (me.salt !== this.salt) {
      await this.discardKeys('The server was reset with a new encryption salt. Enter your encryption password again.');
      return false;
    }
    const ok = await this.acceptKeyCheck(this.keys, me.key_check).catch(() => false);
    if (!ok) {
      await this.discardKeys('The server was reset. Enter your encryption password again.');
      return false;
    }
    return true;
  }

  async discardKeys(notice: string): Promise<void> {
    this.keys = null;
    await idbDelete('kv', [KV_KEYS]);
    this.notice = notice;
    this.unlockMode = this.me && this.me.key_check === null ? 'setup' : 'unlock';
    await this.leave('unlock');
  }

  async rename(name: string): Promise<void> {
    this.deviceName = name;
    lsSet(LS_DEVICE_NAME, name);
  }

  async clearCredentials(notice: string | null): Promise<void> {
    this.token = null;
    this.keys = null;
    this.me = null;
    this.pendingSetupPassword = null;
    lsSet(LS_TOKEN, null);
    lsSet(LS_SALT, null);
    this.salt = null;
    this.notice = notice;
    await this.leave('login');
  }

  showUpdate(reason: string): void {
    this.updateReason = reason;
    void this.leave('update');
  }
}

export const session = new Session();
