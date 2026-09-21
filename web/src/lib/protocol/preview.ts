import { PREVIEW_MAX_CODE_POINTS } from './constants';

export function makePreview(s: string, max: number = PREVIEW_MAX_CODE_POINTS): string {
  let count = 0;
  let end = 0;
  for (const cp of s) {
    if (count === max) return s.slice(0, end);
    count++;
    end += cp.length;
  }
  return s;
}

export function codePointLength(s: string): number {
  let n = 0;
  for (const _ of s) n++;
  return n;
}
