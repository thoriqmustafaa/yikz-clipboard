import { CONTENT_HASH_LABEL, KEY_CHECK_LABEL, KEY_LENGTH, NONCE_LENGTH, SEAL_OVERHEAD } from './constants';
import { type Bytes, randomBytes, toHex, utf8 } from './encoding';

export class DecryptError extends Error {
  constructor(message = 'decryption failed') {
    super(message);
    this.name = 'DecryptError';
  }
}

export interface KeySet {
  aes: CryptoKey;
  contentHash: CryptoKey;
  keyCheck: string;
}

function subtle(): SubtleCrypto {
  const s = globalThis.crypto?.subtle;
  if (!s) throw new Error('WebCrypto is not available. Use HTTPS or localhost.');
  return s;
}

export function normalizePassword(password: string): Bytes {
  return utf8(password.normalize('NFC'));
}

export async function deriveMasterKeyBits(password: string, salt: Uint8Array, iterations: number): Promise<Bytes> {
  const base = await subtle().importKey('raw', normalizePassword(password), 'PBKDF2', false, ['deriveBits']);
  const bits = await subtle().deriveBits(
    { name: 'PBKDF2', hash: 'SHA-256', salt: new Uint8Array(salt), iterations },
    base,
    KEY_LENGTH * 8
  );
  return new Uint8Array(bits);
}

export async function hmacSha256(keyBytes: Uint8Array, data: Uint8Array): Promise<Bytes> {
  const k = await subtle().importKey('raw', new Uint8Array(keyBytes), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return new Uint8Array(await subtle().sign('HMAC', k, new Uint8Array(data)));
}

export interface SubkeyBytes {
  contentHashKey: Bytes;
  keyCheck: string;
}

export async function deriveSubkeyBytes(master: Uint8Array): Promise<SubkeyBytes> {
  const contentHashKey = await hmacSha256(master, utf8(CONTENT_HASH_LABEL));
  const keyCheck = toHex(await hmacSha256(master, utf8(KEY_CHECK_LABEL)));
  return { contentHashKey, keyCheck };
}

export async function importKeySet(master: Uint8Array, extractable = false): Promise<KeySet> {
  if (master.length !== KEY_LENGTH) throw new Error('key must be 32 bytes');
  const aes = await subtle().importKey('raw', new Uint8Array(master), { name: 'AES-GCM' }, extractable, ['encrypt', 'decrypt']);
  const sub = await deriveSubkeyBytes(master);
  const contentHash = await subtle().importKey('raw', sub.contentHashKey, { name: 'HMAC', hash: 'SHA-256' }, extractable, [
    'sign'
  ]);
  sub.contentHashKey.fill(0);
  return { aes, contentHash, keyCheck: sub.keyCheck };
}

export async function deriveKeySet(password: string, salt: Uint8Array, iterations: number): Promise<KeySet> {
  const master = await deriveMasterKeyBits(password, salt, iterations);
  try {
    return await importKeySet(master, false);
  } finally {
    master.fill(0);
  }
}

export function aadMeta(id: string): Bytes {
  return utf8(`yc1|meta|${id}`);
}

export function aadPayload(id: string): Bytes {
  return utf8(`yc1|payload|${id}`);
}

export function aadThumb(id: string): Bytes {
  return utf8(`yc1|thumb|${id}`);
}

export function aadChunk(id: string, index: number, count: number): Bytes {
  return utf8(`yc1|chunk|${id}|${index}|${count}`);
}

export async function seal(key: CryptoKey, aad: Uint8Array, plaintext: Uint8Array, nonce?: Uint8Array): Promise<Bytes> {
  const iv = nonce ? new Uint8Array(nonce) : randomBytes(NONCE_LENGTH);
  if (iv.length !== NONCE_LENGTH) throw new Error('nonce must be 12 bytes');
  const ct = new Uint8Array(
    await subtle().encrypt(
      {
        name: 'AES-GCM',
        iv,
        additionalData: new Uint8Array(aad),
        tagLength: 128
      },
      key,
      plaintext as Bytes
    )
  );
  const out = new Uint8Array(NONCE_LENGTH + ct.length);
  out.set(iv, 0);
  out.set(ct, NONCE_LENGTH);
  return out;
}

export async function open(key: CryptoKey, aad: Uint8Array, sealed: Uint8Array): Promise<Bytes> {
  if (sealed.length < SEAL_OVERHEAD) throw new DecryptError('sealed box is too short');
  const iv = sealed.slice(0, NONCE_LENGTH);
  const body = sealed.slice(NONCE_LENGTH);
  try {
    const pt = await subtle().decrypt(
      {
        name: 'AES-GCM',
        iv,
        additionalData: new Uint8Array(aad),
        tagLength: 128
      },
      key,
      body
    );
    return new Uint8Array(pt);
  } catch {
    throw new DecryptError();
  }
}

export async function importAesKeyRaw(raw: Uint8Array): Promise<CryptoKey> {
  return subtle().importKey('raw', new Uint8Array(raw), { name: 'AES-GCM' }, false, ['encrypt', 'decrypt']);
}

export async function contentHash(hashKey: CryptoKey, content: Uint8Array): Promise<string> {
  return toHex(new Uint8Array(await subtle().sign('HMAC', hashKey, content as Bytes)));
}

export async function sha256Hex(data: Uint8Array): Promise<string> {
  return toHex(new Uint8Array(await subtle().digest('SHA-256', data as Bytes)));
}
