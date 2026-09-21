import { describe, expect, it } from 'vitest';
import { chunkPlainSize, chunkRange, chunkSealedSize, planChunks, totalSealedChunkBytes } from '../src/lib/protocol/chunking';
import { DEFAULT_LIMITS, SEAL_OVERHEAD } from '../src/lib/protocol/constants';
import { vector } from './load';

const v = vector('chunking.json');

describe('chunking', () => {
  it('matches the constants', () => {
    expect(v.inline_max_bytes).toBe(DEFAULT_LIMITS.inline_max_bytes);
    expect(v.chunk_size_bytes).toBe(DEFAULT_LIMITS.chunk_size_bytes);
    expect(v.seal_overhead_bytes).toBe(SEAL_OVERHEAD);
  });

  for (const c of v.cases) {
    it(`plans size ${c.size}`, () => {
      const p = planChunks(c.size);
      expect(p.inline).toBe(c.inline);
      expect(p.chunkCount).toBe(c.chunk_count);
      if (c.inline) {
        expect(c.size + SEAL_OVERHEAD).toBe(c.payload_sealed_length);
        return;
      }
      expect(chunkPlainSize(p, 0)).toBe(Math.min(c.full_chunk_plaintext_size, c.size));
      if (c.chunk_count > 1) expect(chunkSealedSize(p, 0)).toBe(c.full_chunk_sealed_length);
      expect(c.last_chunk_index).toBe(c.chunk_count - 1);
      expect(chunkPlainSize(p, c.last_chunk_index)).toBe(c.last_chunk_plaintext_size);
      expect(chunkSealedSize(p, c.last_chunk_index)).toBe(c.last_chunk_sealed_length);
      expect(totalSealedChunkBytes(p)).toBe(c.total_chunk_sealed_bytes);
      expect(chunkRange(p, c.last_chunk_index).end).toBe(c.size);
    });
  }

  it('rejects bad input', () => {
    expect(() => planChunks(0)).toThrow();
    expect(() => chunkRange(planChunks(10), 0)).toThrow();
    expect(() => chunkRange(planChunks(300000), 1)).toThrow();
  });
});
