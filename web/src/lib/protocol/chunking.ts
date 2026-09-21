import { DEFAULT_LIMITS, SEAL_OVERHEAD } from './constants';

export interface ChunkPlan {
  size: number;
  inline: boolean;
  chunkCount: number;
  chunkSize: number;
}

export function planChunks(
  size: number,
  inlineMax: number = DEFAULT_LIMITS.inline_max_bytes,
  chunkSize: number = DEFAULT_LIMITS.chunk_size_bytes
): ChunkPlan {
  if (!Number.isSafeInteger(size) || size < 1) throw new Error('size must be a positive integer');
  if (size <= inlineMax) return { size, inline: true, chunkCount: 0, chunkSize };
  return { size, inline: false, chunkCount: Math.ceil(size / chunkSize), chunkSize };
}

export function chunkRange(plan: ChunkPlan, index: number): { start: number; end: number } {
  if (plan.inline || index < 0 || index >= plan.chunkCount) throw new Error('chunk index out of range');
  const start = index * plan.chunkSize;
  const end = Math.min(start + plan.chunkSize, plan.size);
  return { start, end };
}

export function chunkPlainSize(plan: ChunkPlan, index: number): number {
  const r = chunkRange(plan, index);
  return r.end - r.start;
}

export function chunkSealedSize(plan: ChunkPlan, index: number): number {
  return chunkPlainSize(plan, index) + SEAL_OVERHEAD;
}

export function totalSealedChunkBytes(plan: ChunkPlan): number {
  let total = 0;
  for (let i = 0; i < plan.chunkCount; i++) total += chunkSealedSize(plan, i);
  return total;
}
