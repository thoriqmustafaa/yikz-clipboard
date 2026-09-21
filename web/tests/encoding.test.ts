import { describe, expect, it } from 'vitest';
import { fromBase64, toBase64 } from '../src/lib/protocol/encoding';

describe('base64', () => {
  it('round trips all lengths', () => {
    for (let n = 0; n < 40; n++) {
      const b = new Uint8Array(n).map((_, i) => (i * 37 + n) & 255);
      const s = toBase64(b);
      expect(s).toBe(Buffer.from(b).toString('base64'));
      expect(Array.from(fromBase64(s))).toEqual(Array.from(b));
    }
  });

  it('rejects invalid input', () => {
    for (const bad of ['a', 'abc', 'ab=c', 'a===', '====', 'ab-_', 'YQ', 'YR==', 'YWJ=x', 'Y Q==']) {
      expect(() => fromBase64(bad)).toThrow();
    }
    expect(Array.from(fromBase64(''))).toEqual([]);
  });
});
