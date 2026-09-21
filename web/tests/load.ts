import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

export function vector<T = any>(name: string): T {
  const path = fileURLToPath(new URL(`../../protocol/vectors/${name}`, import.meta.url));
  return JSON.parse(readFileSync(path, 'utf8')) as T;
}
