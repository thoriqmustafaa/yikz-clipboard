import { describe, expect, it } from 'vitest';
import { isUuidV7, UUIDV7_RE, uuidv7 } from '../src/lib/protocol/uuidv7';
import { fromHex } from '../src/lib/protocol/encoding';
import { vector } from './load';

const v = vector('uuidv7.json');

describe('uuidv7', () => {
  it('uses the vector regex', () => {
    expect(UUIDV7_RE.source).toBe(v.regex);
  });

  for (const g of v.generate) {
    it(`generates ${g.expected}`, () => {
      expect(uuidv7(g.unix_ms, fromHex(g.random_hex))).toBe(g.expected);
    });
  }

  it('accepts valid ids', () => {
    for (const s of v.valid) expect(isUuidV7(s)).toBe(true);
  });

  it('rejects invalid ids', () => {
    for (const s of v.invalid) expect(isUuidV7(s.value), s.reason).toBe(false);
  });

  it('generates well formed random ids with the current time', () => {
    const before = Date.now();
    const id = uuidv7();
    expect(id).toMatch(UUIDV7_RE);
    const ms = parseInt(id.replace(/-/g, '').slice(0, 12), 16);
    expect(ms).toBeGreaterThanOrEqual(before);
    expect(ms).toBeLessThanOrEqual(Date.now());
    expect(uuidv7()).not.toBe(uuidv7());
  });
});
