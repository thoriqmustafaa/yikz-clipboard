import { aadChunk, aadMeta, aadPayload, aadThumb, contentHash, type KeySet, open, seal, sha256Hex } from './crypto';
import { type Bytes, fromBase64, toBase64 } from './encoding';
import { type Meta, parseMeta, serializeMeta } from './meta';
import type { ItemHeader } from './messages';

export class IntegrityError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'IntegrityError';
  }
}

export async function sealMeta(aes: CryptoKey, id: string, meta: Meta, nonce?: Uint8Array): Promise<string> {
  return toBase64(await seal(aes, aadMeta(id), serializeMeta(meta), nonce));
}

export async function openMeta(aes: CryptoKey, id: string, sealedB64: string): Promise<Meta> {
  return parseMeta(await open(aes, aadMeta(id), fromBase64(sealedB64)));
}

export async function sealPayload(aes: CryptoKey, id: string, content: Uint8Array, nonce?: Uint8Array): Promise<string> {
  return toBase64(await seal(aes, aadPayload(id), content, nonce));
}

export async function openPayload(aes: CryptoKey, id: string, sealedB64: string): Promise<Bytes> {
  return open(aes, aadPayload(id), fromBase64(sealedB64));
}

export async function sealThumb(aes: CryptoKey, id: string, jpeg: Uint8Array, nonce?: Uint8Array): Promise<Bytes> {
  return seal(aes, aadThumb(id), jpeg, nonce);
}

export async function openThumb(aes: CryptoKey, id: string, sealed: Uint8Array): Promise<Bytes> {
  return open(aes, aadThumb(id), sealed);
}

export async function sealChunk(
  aes: CryptoKey,
  id: string,
  index: number,
  count: number,
  plaintext: Uint8Array,
  nonce?: Uint8Array
): Promise<Bytes> {
  return seal(aes, aadChunk(id, index, count), plaintext, nonce);
}

export async function openChunk(aes: CryptoKey, id: string, index: number, count: number, sealed: Uint8Array): Promise<Bytes> {
  return open(aes, aadChunk(id, index, count), sealed);
}

export async function verifyContent(
  keys: KeySet,
  header: Pick<ItemHeader, 'size' | 'content_hash'>,
  meta: Meta,
  content: Uint8Array
): Promise<void> {
  if (content.length !== header.size) throw new IntegrityError('content size does not match the header');
  const digest = await sha256Hex(content);
  if (digest !== meta.sha256) throw new IntegrityError('SHA-256 does not match the item meta');
  const keyed = await contentHash(keys.contentHash, content);
  if (keyed !== header.content_hash) throw new IntegrityError('content hash does not match the item header');
}

const URL_ONLY = /^\s*(https?:\/\/[^\s]+)\s*$/i;

export function linkFromText(text: string): string | null {
  const m = URL_ONLY.exec(text);
  if (!m) return null;
  try {
    const u = new URL(m[1]);
    return u.protocol === 'http:' || u.protocol === 'https:' ? m[1] : null;
  } catch {
    return null;
  }
}
