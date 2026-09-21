import { describe, expect, it } from 'vitest';
import {
  buildHello,
  buildKeyCheckRequest,
  buildLoginRequest,
  buildPinRequest,
  buildPing,
  buildPong,
  buildRenameRequest,
  kdfSupported,
  parseDevice,
  parseDevices,
  parseErrorBody,
  parseHealth,
  parseHistory,
  parseHistoryIndex,
  parseItemHeader,
  parseLoginResponse,
  parseMe,
  parseServerMessage,
  parseStorage,
  validateDeviceName
} from '../src/lib/protocol/messages';
import { importKeySet } from '../src/lib/protocol/crypto';
import { fromBase64, fromHex } from '../src/lib/protocol/encoding';
import { openMeta, openPayload, openThumb } from '../src/lib/protocol/item';
import { DEFAULT_LIMITS } from '../src/lib/protocol/constants';
import { backoffCapMs, backoffDelayMs, closeAction } from '../src/lib/closecodes';
import { vector } from './load';

const ws = vector('ws.json');
const http = vector('http.json');
const kdf = vector('kdf.json');

function strip(o: unknown): unknown {
  return JSON.parse(JSON.stringify(o));
}

describe('websocket messages', () => {
  for (const m of ws.messages.filter((m: any) => m.direction === 'client_to_server')) {
    it(`builds ${m.name}`, () => {
      const msg = m.message;
      let built;
      if (msg.type === 'hello') {
        built = buildHello({
          deviceId: msg.device_id,
          lastSeq: msg.last_seq,
          appVersion: msg.app_version,
          platform: msg.platform
        });
      } else if (msg.type === 'ping') {
        built = buildPing(msg.ts);
      } else {
        built = buildPong(msg.ts);
      }
      expect(built).toEqual(msg);
      expect(strip(built)).toEqual(msg);
    });
  }

  for (const m of ws.messages.filter((m: any) => m.direction === 'server_to_client')) {
    it(`parses ${m.name}`, () => {
      const parsed = parseServerMessage(JSON.stringify(m.message));
      expect(parsed).not.toBeNull();
      expect(strip(parsed)).toEqual(m.message);
    });
  }

  it('ignores unknown types and unknown fields', () => {
    expect(parseServerMessage('{"type":"future_thing","x":1}')).toBeNull();
    expect(parseServerMessage('{"type":"ping","ts":5,"extra":true}')).toEqual({
      type: 'ping',
      ts: 5
    });
    expect(() => parseServerMessage('not json')).toThrow();
    expect(() => parseServerMessage('{"no":"type"}')).toThrow();
  });

  it('decrypts clip examples', async () => {
    const keys = await importKeySet(fromHex(kdf.cases[0].key_hex));
    for (const m of ws.messages.filter((m: any) => m.message.type === 'clip')) {
      const parsed = parseServerMessage(m.message);
      if (!parsed || parsed.type !== 'clip') throw new Error('expected clip');
      const meta = await openMeta(keys.aes, parsed.item.id, parsed.item.meta);
      expect(meta.v).toBe(1);
      if (parsed.item.payload) {
        const p = await openPayload(keys.aes, parsed.item.id, parsed.item.payload);
        expect(p.length).toBe(parsed.item.size);
      }
    }
  });

  it('maps every close code to the documented action', () => {
    for (const c of ws.close_codes) {
      const a = closeAction(c.code);
      if (c.code === 4001) expect(a).toBe('login');
      else if (c.code === 4003) expect(a).toBe('update');
      else if (c.code === 4002) expect(a).toBe('paused');
      else expect(a).toBe('reconnect');
    }
    expect(closeAction(1006)).toBe('reconnect');
  });

  it('computes full jitter backoff from 0.5 s to 30 s', () => {
    expect(backoffCapMs(0)).toBe(500);
    expect(backoffCapMs(1)).toBe(1000);
    expect(backoffCapMs(5)).toBe(16000);
    expect(backoffCapMs(6)).toBe(30000);
    expect(backoffCapMs(40)).toBe(30000);
    expect(backoffDelayMs(3, () => 0.5)).toBe(2000);
    expect(backoffDelayMs(3, () => 0)).toBe(0);
  });
});

describe('http examples', () => {
  const examples: any[] = http.examples;

  function ex(name: string): any {
    const e = examples.find((x) => x.name === name);
    if (!e) throw new Error(name);
    return e;
  }

  it('builds login requests', () => {
    const a = ex('login_new_device').request_body;
    expect(
      buildLoginRequest({
        username: a.username,
        password: a.password,
        deviceName: a.device_name,
        platform: a.platform
      })
    ).toEqual(a);
    const b = ex('login_existing_device').request_body;
    expect(
      buildLoginRequest({
        username: b.username,
        password: b.password,
        deviceName: b.device_name,
        platform: b.platform,
        deviceId: b.device_id
      })
    ).toEqual(b);
  });

  it('builds small request bodies', () => {
    expect(buildPinRequest(true)).toEqual(ex('pin').request_body);
    expect(buildPinRequest(false)).toEqual(ex('unpin').request_body);
    expect(buildKeyCheckRequest(ex('key_check_set').request_body.key_check)).toEqual(ex('key_check_set').request_body);
    expect(buildRenameRequest('  Gaming PC ')).toEqual(ex('device_rename').request_body);
  });

  it('parses login and me', () => {
    for (const name of ['login_new_device', 'login_existing_device']) {
      const r = parseLoginResponse(ex(name).response_body);
      expect(strip(r)).toEqual(ex(name).response_body);
      expect(kdfSupported(r.kdf)).toBe(true);
      expect(fromBase64(r.salt).length).toBe(16);
    }
    const body = ex('me').response_body;
    const me = parseMe(body);
    expect(me.device.current).toBe(true);
    expect(me.limits).toEqual({ ...DEFAULT_LIMITS, ...body.limits });
    expect(strip({ ...me, limits: body.limits })).toEqual(body);
  });

  it('parses every response body', async () => {
    const keys = await importKeySet(fromHex(kdf.cases[0].key_hex));
    let parsed = 0;
    for (const e of examples) {
      const body = e.response_body;
      if (e.status === 204) {
        expect(body).toBeUndefined();
        continue;
      }
      if (e.status >= 400) {
        const err = parseErrorBody(body);
        expect(err?.code, e.name).toBeTypeOf('string');
        expect(strip(err)).toEqual(body);
        parsed++;
        continue;
      }
      const path: string = e.path;
      if (path.startsWith('/api/login')) parseLoginResponse(body);
      else if (path === '/api/me') parseMe(body);
      else if (path === '/api/devices') expect(parseDevices(body)).toHaveLength(body.devices.length);
      else if (path.startsWith('/api/devices/')) expect(strip(parseDevice(body))).toEqual(body);
      else if (path === '/api/history/index') expect(strip(parseHistoryIndex(body))).toEqual(body);
      else if (path.startsWith('/api/history')) {
        const h = parseHistory(body);
        expect(strip(h)).toEqual(body);
        for (const it of h.items) {
          expect(it.payload).toBeUndefined();
          await openMeta(keys.aes, it.id, it.meta);
        }
      } else if (path === '/api/storage') expect(strip(parseStorage(body))).toEqual(body);
      else if (path === '/healthz') expect(strip(parseHealth(body))).toEqual(body);
      else if (/\/thumb$/.test(path)) {
        const id = path.split('/')[3];
        const jpeg = await openThumb(keys.aes, id, fromBase64(body));
        expect(jpeg[0]).toBe(0xff);
      } else if (/\/chunks\/\d+$/.test(path)) {
        expect(body).toBeUndefined();
        expect(e.response_body_note).toBeTypeOf('string');
      } else if (/^\/api\/items/.test(path) && body && typeof body === 'object') {
        const h = parseItemHeader(body);
        expect(strip(h)).toEqual(body);
        await openMeta(keys.aes, h.id, h.meta);
        if (h.payload) await openPayload(keys.aes, h.id, h.payload);
      } else {
        throw new Error(`unhandled example ${e.name}`);
      }
      parsed++;
    }
    expect(parsed).toBeGreaterThan(30);
  });

  it('reads error details', () => {
    const e = parseErrorBody(ex('commit_missing_chunks').response_body);
    expect(e?.details?.missing).toEqual([1, 2]);
    expect(ex('login_rate_limited').response_headers['Retry-After']).toBe('600');
  });

  it('validates device names', () => {
    expect(validateDeviceName('Web on Firefox')).toBeNull();
    expect(validateDeviceName('   ')).not.toBeNull();
    expect(validateDeviceName('x'.repeat(65))).not.toBeNull();
    expect(validateDeviceName('a' + String.fromCharCode(7) + 'b')).not.toBeNull();
  });
});
