import { type Bytes, fromUtf8, utf8 } from './encoding';

export const YCF1_MAGIC = [0x59, 0x43, 0x46, 0x31];
export const MAX_FILES = 1000;

export interface ArchiveFile {
  name: string;
  data: Uint8Array;
}

export interface ArchiveEntry {
  name: string;
  offset: number;
  size: number;
}

export class ArchiveError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ArchiveError';
  }
}

export function validateFileName(name: string): string | null {
  if (typeof name !== 'string' || name.length === 0) return 'empty';
  if (name !== name.normalize('NFC')) return 'not NFC';
  if (name === '.' || name === '..') return 'dot';
  for (let i = 0; i < name.length; i++) {
    const c = name.charCodeAt(i);
    if (c === 0x2f) return 'contains slash';
    if (c === 0x5c) return 'contains backslash';
    if (c <= 0x1f || c === 0x7f) return 'contains control character';
  }
  const len = utf8(name).length;
  if (len < 1 || len > 255) return 'longer than 255 UTF-8 bytes';
  return null;
}

export function isValidFileName(name: string): boolean {
  return validateFileName(name) === null;
}

export function sanitizeFileName(raw: string): string {
  let name = (raw || '').normalize('NFC');
  name = name.split(/[/\\]/).pop() ?? '';
  name = name.replace(/[\u0000-\u001f\u007f]/g, '');
  if (name === '.' || name === '..' || name.length === 0) name = 'file';
  while (utf8(name).length > 255) {
    const cps = Array.from(name);
    const dot = name.lastIndexOf('.');
    const ext = dot > 0 && name.length - dot <= 16 ? name.slice(dot) : '';
    const base = Array.from(ext ? name.slice(0, dot) : name);
    if (base.length <= 1) {
      name = cps.slice(0, cps.length - 1).join('');
    } else {
      name = base.slice(0, base.length - 1).join('') + ext;
    }
  }
  return name;
}

export function uniqueFileNames(names: string[]): string[] {
  const seen = new Set<string>();
  return names.map((n) => {
    let candidate = n;
    if (seen.has(candidate)) {
      const dot = n.lastIndexOf('.');
      const base = dot > 0 ? n.slice(0, dot) : n;
      const ext = dot > 0 ? n.slice(dot) : '';
      let i = 2;
      do {
        candidate = sanitizeFileName(`${base} (${i})${ext}`);
        i++;
      } while (seen.has(candidate));
    }
    seen.add(candidate);
    return candidate;
  });
}

export function archiveSize(files: { name: string; size: number }[]): number {
  let total = 8;
  for (const f of files) total += 4 + utf8(f.name).length + 8 + f.size;
  return total;
}

function checkNames(names: string[]): void {
  if (names.length === 0) throw new ArchiveError('archive must contain at least one file');
  if (names.length > MAX_FILES) throw new ArchiveError('archive has too many files');
  const seen = new Set<string>();
  for (const n of names) {
    const err = validateFileName(n);
    if (err) throw new ArchiveError(`invalid file name: ${err}`);
    if (seen.has(n)) throw new ArchiveError('duplicate file name');
    seen.add(n);
  }
}

export function writeArchiveHeader(view: DataView, offset: number, count: number): number {
  view.setUint8(offset, YCF1_MAGIC[0]);
  view.setUint8(offset + 1, YCF1_MAGIC[1]);
  view.setUint8(offset + 2, YCF1_MAGIC[2]);
  view.setUint8(offset + 3, YCF1_MAGIC[3]);
  view.setUint32(offset + 4, count, false);
  return offset + 8;
}

function setU64(view: DataView, offset: number, value: number): void {
  view.setUint32(offset, Math.floor(value / 0x100000000), false);
  view.setUint32(offset + 4, value >>> 0, false);
}

function getU64(view: DataView, offset: number): number {
  const hi = view.getUint32(offset, false);
  const lo = view.getUint32(offset + 4, false);
  const v = hi * 0x100000000 + lo;
  if (!Number.isSafeInteger(v)) throw new ArchiveError('file too large');
  return v;
}

export function writeEntryHeader(target: Uint8Array, offset: number, name: string, size: number): number {
  const nameBytes = utf8(name);
  const view = new DataView(target.buffer, target.byteOffset, target.byteLength);
  view.setUint32(offset, nameBytes.length, false);
  target.set(nameBytes, offset + 4);
  setU64(view, offset + 4 + nameBytes.length, size);
  return offset + 4 + nameBytes.length + 8;
}

export function encodeArchive(files: ArchiveFile[]): Bytes {
  checkNames(files.map((f) => f.name));
  const total = archiveSize(files.map((f) => ({ name: f.name, size: f.data.length })));
  const out = new Uint8Array(total);
  const view = new DataView(out.buffer);
  let off = writeArchiveHeader(view, 0, files.length);
  for (const f of files) {
    off = writeEntryHeader(out, off, f.name, f.data.length);
    out.set(f.data, off);
    off += f.data.length;
  }
  return out;
}

export async function encodeArchiveFromBlobs(
  files: { name: string; blob: Blob }[],
  onProgress?: (done: number, total: number) => void
): Promise<Bytes> {
  checkNames(files.map((f) => f.name));
  const total = archiveSize(files.map((f) => ({ name: f.name, size: f.blob.size })));
  const out = new Uint8Array(total);
  const view = new DataView(out.buffer);
  let off = writeArchiveHeader(view, 0, files.length);
  for (const f of files) {
    off = writeEntryHeader(out, off, f.name, f.blob.size);
    const data = new Uint8Array(await f.blob.arrayBuffer());
    if (data.length !== f.blob.size) throw new ArchiveError('file changed while reading');
    out.set(data, off);
    off += data.length;
    onProgress?.(off, total);
  }
  return out;
}

export function listArchive(archive: Uint8Array): ArchiveEntry[] {
  const view = new DataView(archive.buffer, archive.byteOffset, archive.byteLength);
  if (archive.length < 8) throw new ArchiveError('archive truncated');
  for (let i = 0; i < 4; i++) if (archive[i] !== YCF1_MAGIC[i]) throw new ArchiveError('bad magic');
  const count = view.getUint32(4, false);
  if (count === 0 || count > MAX_FILES) throw new ArchiveError('invalid file count');
  let off = 8;
  const entries: ArchiveEntry[] = [];
  const seen = new Set<string>();
  for (let i = 0; i < count; i++) {
    if (off + 4 > archive.length) throw new ArchiveError('archive truncated');
    const nameLen = view.getUint32(off, false);
    off += 4;
    if (nameLen < 1 || nameLen > 255 || off + nameLen > archive.length) throw new ArchiveError('invalid name length');
    let name: string;
    try {
      name = fromUtf8(archive.subarray(off, off + nameLen));
    } catch {
      throw new ArchiveError('name is not UTF-8');
    }
    off += nameLen;
    const err = validateFileName(name);
    if (err) throw new ArchiveError(`invalid file name: ${err}`);
    if (seen.has(name)) throw new ArchiveError('duplicate file name');
    seen.add(name);
    if (off + 8 > archive.length) throw new ArchiveError('archive truncated');
    const size = getU64(view, off);
    off += 8;
    if (off + size > archive.length) throw new ArchiveError('archive truncated');
    entries.push({ name, offset: off, size });
    off += size;
  }
  if (off !== archive.length) throw new ArchiveError('trailing bytes');
  return entries;
}

export function decodeArchive(archive: Bytes): { name: string; data: Bytes }[] {
  return listArchive(archive).map((e) => ({
    name: e.name,
    data: archive.subarray(e.offset, e.offset + e.size)
  }));
}
