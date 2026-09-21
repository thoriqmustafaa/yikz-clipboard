import { describe, expect, it } from 'vitest';
import { aadChunk, aadMeta, aadPayload, aadThumb, DecryptError, importAesKeyRaw, open, seal } from '../src/lib/protocol/crypto';
import { fromBase64, fromHex, toBase64, toHex, utf8 } from '../src/lib/protocol/encoding';
import { vector } from './load';

const aead = vector('aead.json');

function aadFor(purpose: string, aadUtf8: string): Uint8Array {
  const parts = aadUtf8.split('|');
  const id = parts[2];
  if (purpose === 'meta') return aadMeta(id);
  if (purpose === 'payload') return aadPayload(id);
  if (purpose === 'thumb') return aadThumb(id);
  return aadChunk(id, Number(parts[3]), Number(parts[4]));
}

describe('aead', () => {
  it('has the documented AAD templates', () => {
    expect(aead.aad_formats).toEqual({
      meta: 'yc1|meta|<item_id>',
      payload: 'yc1|payload|<item_id>',
      thumb: 'yc1|thumb|<item_id>',
      chunk: 'yc1|chunk|<item_id>|<index>|<chunk_count>'
    });
  });

  const purposes = new Set<string>();
  for (const p of aead.positive) {
    purposes.add(p.purpose);
    it(`seals and opens ${p.name}`, async () => {
      const aad = aadFor(p.purpose, p.aad_utf8);
      expect(toHex(aad)).toBe(p.aad_hex);
      expect(toHex(utf8(p.aad_utf8))).toBe(p.aad_hex);
      const key = await importAesKeyRaw(fromHex(p.key_hex));
      const sealed = await seal(key, aad, fromHex(p.plaintext_hex), fromHex(p.nonce_hex));
      expect(toHex(sealed)).toBe(p.sealed_hex);
      expect(toBase64(sealed)).toBe(p.sealed_b64);
      expect(sealed.length).toBe(p.sealed_length);
      const opened = await open(key, aad, fromBase64(p.sealed_b64));
      expect(toHex(opened)).toBe(p.plaintext_hex);
      if (p.plaintext_utf8 !== undefined) expect(new TextDecoder().decode(opened)).toBe(p.plaintext_utf8);
    });
  }

  it('covers every object type', () => {
    expect([...purposes].sort()).toEqual(['chunk', 'meta', 'payload', 'thumb']);
  });

  for (const n of aead.negative) {
    it(`rejects ${n.name}`, async () => {
      expect(n.expect).toBe('decrypt_error');
      expect(toHex(utf8(n.aad_utf8))).toBe(n.aad_hex);
      const key = await importAesKeyRaw(fromHex(n.key_hex));
      await expect(open(key, fromHex(n.aad_hex), fromBase64(n.sealed_b64))).rejects.toBeInstanceOf(DecryptError);
    });
  }

  it('uses random nonces when none is given', async () => {
    const key = await importAesKeyRaw(new Uint8Array(32));
    const a = await seal(key, utf8('x'), utf8('same'));
    const b = await seal(key, utf8('x'), utf8('same'));
    expect(toHex(a.slice(0, 12))).not.toBe(toHex(b.slice(0, 12)));
    expect(a.length).toBe(4 + 28);
  });
});
