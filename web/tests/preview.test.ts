import { describe, expect, it } from 'vitest';
import { codePointLength, makePreview } from '../src/lib/protocol/preview';
import { vector } from './load';

const v = vector('preview.json');

describe('preview', () => {
  it('uses 500 code points', () => {
    expect(v.max_code_points).toBe(500);
  });

  for (const c of v.cases) {
    it(`truncates ${c.name}`, () => {
      expect(codePointLength(c.input)).toBe(c.input_code_points);
      expect(c.input.length).toBe(c.input_utf16_units);
      const out = makePreview(c.input);
      expect(out).toBe(c.expected);
      expect(codePointLength(out)).toBe(c.expected_code_points);
    });
  }
});
