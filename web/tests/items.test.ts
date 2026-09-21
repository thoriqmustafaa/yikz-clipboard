import { describe, expect, it } from 'vitest';
import { contentHash, importKeySet, sha256Hex } from '../src/lib/protocol/crypto';
import { fromBase64, fromHex, toHex, utf8 } from '../src/lib/protocol/encoding';
import { buildMeta, parseMeta, serializeMeta, type ItemKind } from '../src/lib/protocol/meta';
import {
  openChunk,
  openMeta,
  openPayload,
  openThumb,
  sealChunk,
  sealMeta,
  sealPayload,
  sealThumb,
  verifyContent
} from '../src/lib/protocol/item';
import { chunkRange, planChunks } from '../src/lib/protocol/chunking';
import { decodeArchive } from '../src/lib/protocol/archive';
import { buildCommitRequest, buildCreateItemRequest, parseItemHeader } from '../src/lib/protocol/messages';
import { patternArchive } from './pattern';
import { vector } from './load';

const items = vector('items.json').items;
const http = vector('http.json').examples;

function example(name: string): any {
  const e = http.find((x: any) => x.name === name);
  if (!e) throw new Error(`missing example ${name}`);
  return e;
}

function contentOf(item: any): Uint8Array {
  return item.content_hex !== undefined ? fromHex(item.content_hex) : patternArchive();
}

describe('items', () => {
  for (const item of items) {
    it(`reproduces the ${item.name} item`, async () => {
      const keys = await importKeySet(fromHex(item.key_hex));
      const content = contentOf(item);
      expect(content.length).toBe(item.size);
      expect(await sha256Hex(content)).toBe(item.content_sha256);
      expect(await contentHash(keys.contentHash, content)).toBe(item.content_hash);

      const kind = item.kind as ItemKind;
      let meta;
      if (kind === 'text') {
        meta = buildMeta({
          kind,
          sha256: item.content_sha256,
          text: new TextDecoder().decode(content),
          sourceApp: item.meta.source_app
        });
      } else if (kind === 'image') {
        meta = buildMeta({
          kind,
          sha256: item.content_sha256,
          image: item.meta.image,
          sourceApp: item.meta.source_app
        });
      } else {
        const files =
          item.content_hex !== undefined
            ? decodeArchive(fromHex(item.content_hex)).map((f) => ({
                name: f.name,
                size: f.data.length
              }))
            : item.meta.files;
        meta = buildMeta({
          kind,
          sha256: item.content_sha256,
          files,
          sourceApp: item.meta.source_app
        });
      }
      expect(new TextDecoder().decode(serializeMeta(meta))).toBe(item.meta_plaintext_utf8);
      expect(parseMeta(utf8(item.meta_plaintext_utf8))).toEqual(item.meta);
      expect(item.meta_aad_utf8).toBe(`yc1|meta|${item.id}`);

      const metaB64 = await sealMeta(keys.aes, item.id, meta, fromHex(item.meta_nonce_hex));
      expect(metaB64).toBe(item.meta_sealed_b64);
      expect(await openMeta(keys.aes, item.id, item.meta_sealed_b64)).toEqual(item.meta);

      const header = parseItemHeader(item.header);
      expect(header.meta).toBe(item.meta_sealed_b64);
      expect(header.id).toBe(item.id);
      expect(header.size).toBe(item.size);
      expect(header.chunk_count).toBe(item.chunk_count);
      expect(header.content_hash).toBe(item.content_hash);
      expect(header.stored_bytes).toBe(item.stored_bytes);

      let stored = fromBase64(item.meta_sealed_b64).length;
      if (item.chunk_count === 0) {
        expect(item.payload_aad_utf8).toBe(`yc1|payload|${item.id}`);
        const payload = await sealPayload(keys.aes, item.id, content, fromHex(item.payload_nonce_hex));
        expect(payload).toBe(item.payload_sealed_b64);
        expect(header.payload).toBe(item.payload_sealed_b64);
        const opened = await openPayload(keys.aes, item.id, item.payload_sealed_b64);
        expect(toHex(opened)).toBe(toHex(content));
        stored += fromBase64(payload).length;
      } else {
        const plan = planChunks(item.size);
        expect(plan.chunkCount).toBe(item.chunk_count);
        const whole = new Uint8Array(item.size);
        for (const c of item.chunks) {
          const r = chunkRange(plan, c.index);
          const plain = content.subarray(r.start, r.end);
          expect(plain.length).toBe(c.plaintext_size);
          expect(await sha256Hex(plain)).toBe(c.plaintext_sha256);
          expect(c.aad_utf8).toBe(`yc1|chunk|${item.id}|${c.index}|${item.chunk_count}`);
          const sealed = await sealChunk(keys.aes, item.id, c.index, item.chunk_count, plain, fromHex(c.nonce_hex));
          expect(sealed.length).toBe(c.sealed_size);
          expect(await sha256Hex(sealed)).toBe(c.sealed_sha256);
          expect(toHex(sealed.subarray(0, 44))).toBe(c.sealed_prefix_hex);
          expect(toHex(sealed.subarray(sealed.length - 32))).toBe(c.sealed_suffix_hex);
          const opened = await openChunk(keys.aes, item.id, c.index, item.chunk_count, sealed);
          whole.set(opened, r.start);
          stored += sealed.length;
        }
        await verifyContent(keys, header, meta, whole);
      }

      if (item.thumb_plaintext_hex) {
        expect(item.thumb_aad_utf8).toBe(`yc1|thumb|${item.id}`);
        const sealed = await sealThumb(keys.aes, item.id, fromHex(item.thumb_plaintext_hex), fromHex(item.thumb_nonce_hex));
        expect(toHex(sealed)).toBe(toHex(fromBase64(item.thumb_sealed_b64)));
        expect(toHex(await openThumb(keys.aes, item.id, sealed))).toBe(item.thumb_plaintext_hex);
        stored += sealed.length;
      }
      expect(stored).toBe(item.stored_bytes);
      await verifyContent(keys, header, meta, content);
    });
  }

  it('detects tampered content', async () => {
    const item = items[0];
    const keys = await importKeySet(fromHex(item.key_hex));
    const content = fromHex(item.content_hex);
    content[0] ^= 1;
    await expect(verifyContent(keys, parseItemHeader(item.header), item.meta, content)).rejects.toThrow();
  });

  it('builds create requests matching http.json', () => {
    for (const name of [
      'item_create_inline',
      'item_create_inline_with_thumb_uploaded_first',
      'item_create_repeat_is_idempotent'
    ]) {
      const e = example(name);
      const item = items.find((i: any) => i.id === e.request_body.id);
      const req = buildCreateItemRequest({
        id: item.id,
        kind: item.kind,
        size: item.size,
        contentHash: item.content_hash,
        meta: item.meta_sealed_b64,
        payload: item.payload_sealed_b64
      });
      expect(req).toEqual(e.request_body);
    }
  });

  it('builds the commit request matching http.json', () => {
    const e = example('commit');
    const item = items.find((i: any) => i.name === 'large');
    expect(e.path).toBe(`/api/items/${item.id}/commit`);
    const req = buildCommitRequest({
      kind: item.kind,
      size: item.size,
      chunkCount: item.chunk_count,
      contentHash: item.content_hash,
      meta: item.meta_sealed_b64
    });
    expect(req).toEqual(e.request_body);
  });
});
