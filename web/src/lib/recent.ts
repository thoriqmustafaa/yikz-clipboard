const MAX = 32;
const hashes: string[] = [];

export function rememberHash(h: string): void {
  const i = hashes.indexOf(h);
  if (i >= 0) hashes.splice(i, 1);
  hashes.push(h);
  while (hashes.length > MAX) hashes.shift();
}

export function isRecentHash(h: string): boolean {
  return hashes.includes(h);
}

export function clearRecentHashes(): void {
  hashes.length = 0;
}
