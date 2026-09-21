import { describe, expect, it } from 'vitest';
import {
  deriveMasterKeyBits,
  deriveSubkeyBytes,
  importKeySet,
  normalizePassword,
  contentHash,
  sha256Hex
} from '../src/lib/protocol/crypto';
import { fromBase64, fromHex, toHex } from '../src/lib/protocol/encoding';
import { KDF_ITERATIONS } from '../src/lib/protocol/constants';
import { vector } from './load';

const kdf = vector('kdf.json');
const keys = vector('keys.json');

describe('kdf', () => {
  it('declares the protocol algorithm', () => {
    expect(kdf.algorithm).toBe('pbkdf2-sha256');
    expect(kdf.key_length).toBe(32);
  });

  for (const c of kdf.cases.filter((c: any) => c.iterations !== KDF_ITERATIONS)) {
    it(`derives ${c.name}`, async () => {
      expect(toHex(normalizePassword(c.password_input))).toBe(c.password_utf8_hex);
      const salt = fromBase64(c.salt_b64);
      expect(toHex(salt)).toBe(c.salt_hex);
      const key = await deriveMasterKeyBits(c.password_input, salt, c.iterations);
      expect(toHex(key)).toBe(c.key_hex);
    });
  }

  it('derives the primary 600000 iteration key', async () => {
    const c = kdf.cases.find((c: any) => c.name === 'primary');
    expect(c.iterations).toBe(KDF_ITERATIONS);
    const key = await deriveMasterKeyBits(c.password_input, fromBase64(c.salt_b64), c.iterations);
    expect(toHex(key)).toBe(c.key_hex);
  });

  it('normalizes NFD input to NFC', () => {
    const c = kdf.cases.find((c: any) => c.name === 'unicode_nfd_input');
    expect(c.password_input).not.toBe(c.password_normalized);
    expect(c.password_input.normalize('NFC')).toBe(c.password_normalized);
  });
});

describe('subkeys', () => {
  for (const k of keys.keys) {
    it(`derives subkeys for ${k.name}`, async () => {
      const master = fromHex(k.key_hex);
      const sub = await deriveSubkeyBytes(master);
      expect(toHex(sub.contentHashKey)).toBe(k.content_hash_key_hex);
      expect(sub.keyCheck).toBe(k.key_check);
      const set = await importKeySet(master);
      expect(set.keyCheck).toBe(k.key_check);
      expect(set.aes.extractable).toBe(false);
      expect(set.contentHash.extractable).toBe(false);
      for (const s of k.content_hash_samples) {
        const content = fromHex(s.content_hex);
        expect(new TextDecoder().decode(content)).toBe(s.content_utf8);
        expect(await contentHash(set.contentHash, content)).toBe(s.content_hash);
        expect(await sha256Hex(content)).toBe(s.sha256);
      }
    });
  }
});
