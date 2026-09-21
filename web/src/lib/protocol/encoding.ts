export type Bytes = Uint8Array<ArrayBuffer>;

const encoder = new TextEncoder();
const decoder = new TextDecoder('utf-8', { fatal: true });

export function utf8(s: string): Bytes {
  return encoder.encode(s) as Bytes;
}

export function fromUtf8(b: Uint8Array): string {
  return decoder.decode(b);
}

const HEX = '0123456789abcdef';

export function toHex(b: Uint8Array): string {
  let out = '';
  for (let i = 0; i < b.length; i++) {
    out += HEX[b[i] >> 4] + HEX[b[i] & 15];
  }
  return out;
}

export function fromHex(s: string): Bytes {
  if (s.length % 2 !== 0 || !/^[0-9a-fA-F]*$/.test(s)) throw new Error('invalid hex');
  const out = new Uint8Array(s.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(s.substr(i * 2, 2), 16);
  return out;
}

const B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
const B64_LOOKUP = (() => {
  const t = new Int16Array(128).fill(-1);
  for (let i = 0; i < B64.length; i++) t[B64.charCodeAt(i)] = i;
  return t;
})();

export function toBase64(b: Uint8Array): string {
  let out = '';
  const len = b.length;
  let i = 0;
  const parts: string[] = [];
  for (; i + 2 < len; i += 3) {
    const n = (b[i] << 16) | (b[i + 1] << 8) | b[i + 2];
    out += B64[(n >> 18) & 63] + B64[(n >> 12) & 63] + B64[(n >> 6) & 63] + B64[n & 63];
    if (out.length > 8192) {
      parts.push(out);
      out = '';
    }
  }
  const rem = len - i;
  if (rem === 1) {
    const n = b[i] << 16;
    out += B64[(n >> 18) & 63] + B64[(n >> 12) & 63] + '==';
  } else if (rem === 2) {
    const n = (b[i] << 16) | (b[i + 1] << 8);
    out += B64[(n >> 18) & 63] + B64[(n >> 12) & 63] + B64[(n >> 6) & 63] + '=';
  }
  parts.push(out);
  return parts.join('');
}

export function fromBase64(s: string): Bytes {
  if (typeof s !== 'string' || s.length % 4 !== 0) throw new Error('invalid base64');
  let pad = 0;
  if (s.endsWith('==')) pad = 2;
  else if (s.endsWith('=')) pad = 1;
  const outLen = (s.length / 4) * 3 - pad;
  const out = new Uint8Array(outLen);
  let o = 0;
  for (let i = 0; i < s.length; i += 4) {
    const last = i + 4 === s.length;
    const vals = [0, 0, 0, 0];
    for (let j = 0; j < 4; j++) {
      const c = s.charCodeAt(i + j);
      if (c === 61 && last && j >= 4 - pad) {
        vals[j] = 0;
        continue;
      }
      const v = c < 128 ? B64_LOOKUP[c] : -1;
      if (v < 0) throw new Error('invalid base64');
      vals[j] = v;
    }
    const n = (vals[0] << 18) | (vals[1] << 12) | (vals[2] << 6) | vals[3];
    if (last && pad === 2 && (vals[1] & 15) !== 0) throw new Error('invalid base64');
    if (last && pad === 1 && (vals[2] & 3) !== 0) throw new Error('invalid base64');
    out[o++] = (n >> 16) & 255;
    if (o < outLen) out[o++] = (n >> 8) & 255;
    if (o < outLen) out[o++] = n & 255;
  }
  return out;
}

export function concatBytes(parts: Uint8Array[]): Bytes {
  let total = 0;
  for (const p of parts) total += p.length;
  const out = new Uint8Array(total);
  let off = 0;
  for (const p of parts) {
    out.set(p, off);
    off += p.length;
  }
  return out;
}

export function bytesEqual(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
  return diff === 0;
}

export function randomBytes(n: number): Bytes {
  const out = new Uint8Array(n);
  crypto.getRandomValues(out);
  return out;
}
