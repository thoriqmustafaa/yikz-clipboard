import { randomBytes, toHex } from './encoding';

export const UUIDV7_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

export function isUuidV7(s: unknown): s is string {
  return typeof s === 'string' && UUIDV7_RE.test(s);
}

export function uuidv7(unixMs: number = Date.now(), random: Uint8Array = randomBytes(10)): string {
  if (random.length !== 10) throw new Error('uuidv7 needs 10 random bytes');
  const b = new Uint8Array(16);
  let t = Math.floor(unixMs);
  for (let i = 5; i >= 0; i--) {
    b[i] = t % 256;
    t = Math.floor(t / 256);
  }
  b.set(random, 6);
  b[6] = 0x70 | (b[6] & 0x0f);
  b[8] = 0x80 | (b[8] & 0x3f);
  const h = toHex(b);
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}
