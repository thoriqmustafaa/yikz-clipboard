import { describe, expect, it } from 'vitest';
import {
  ArchiveError,
  archiveSize,
  decodeArchive,
  encodeArchive,
  encodeArchiveFromBlobs,
  listArchive,
  uniqueFileNames,
  validateFileName,
  sanitizeFileName
} from '../src/lib/protocol/archive';
import { fromHex, toHex, utf8 } from '../src/lib/protocol/encoding';
import { sha256Hex } from '../src/lib/protocol/crypto';
import { vector } from './load';
import { patternArchive } from './pattern';

const v = vector('files_archive.json');

describe('YCF1 archive', () => {
  it('packs and unpacks three_files', async () => {
    const c = v.cases.find((c: any) => c.name === 'three_files');
    const files = c.files.map((f: any) => {
      expect(toHex(utf8(f.name))).toBe(f.name_utf8_hex);
      return { name: f.name, data: fromHex(f.data_hex) };
    });
    const archive = encodeArchive(files);
    expect(toHex(archive)).toBe(c.archive_hex);
    expect(archive.length).toBe(c.archive_size);
    expect(await sha256Hex(archive)).toBe(c.archive_sha256);
    expect(archiveSize(files.map((f: any) => ({ name: f.name, size: f.data.length })))).toBe(c.archive_size);
    const decoded = decodeArchive(fromHex(c.archive_hex));
    expect(decoded.map((d) => d.name)).toEqual(c.files.map((f: any) => f.name));
    expect(decoded.map((d) => toHex(d.data))).toEqual(c.files.map((f: any) => f.data_hex));
  });

  it('packs the same bytes from blobs', async () => {
    const c = v.cases.find((c: any) => c.name === 'three_files');
    const archive = await encodeArchiveFromBlobs(
      c.files.map((f: any) => ({ name: f.name, blob: new Blob([fromHex(f.data_hex)]) }))
    );
    expect(toHex(archive)).toBe(c.archive_hex);
  });

  it('packs single_large_file_header', async () => {
    const c = v.cases.find((c: any) => c.name === 'single_large_file_header');
    const archive = patternArchive();
    expect(toHex(archive.subarray(0, c.archive_prefix_hex.length / 2))).toBe(c.archive_prefix_hex);
    expect(archive.length).toBe(c.archive_size);
    expect(await sha256Hex(archive)).toBe(c.archive_sha256);
    const entries = listArchive(archive);
    expect(entries).toHaveLength(1);
    expect(entries[0].size).toBe(c.files[0].size);
  });

  it('rejects invalid names', () => {
    for (const n of v.invalid_names) {
      expect(validateFileName(n.name), n.reason).not.toBeNull();
      expect(() => encodeArchive([{ name: n.name, data: new Uint8Array(1) }])).toThrow(ArchiveError);
    }
  });

  it('rejects malformed archives', () => {
    const c = v.cases.find((c: any) => c.name === 'three_files');
    const good = fromHex(c.archive_hex);
    const trailing = new Uint8Array(good.length + 1);
    trailing.set(good);
    expect(() => listArchive(trailing)).toThrow(ArchiveError);
    expect(() => listArchive(good.subarray(0, good.length - 1))).toThrow(ArchiveError);
    const zero = fromHex('5943463100000000');
    expect(() => listArchive(zero)).toThrow(ArchiveError);
    const tooMany = fromHex('59434631000003e9');
    expect(() => listArchive(tooMany)).toThrow(ArchiveError);
    const badMagic = good.slice();
    badMagic[3] = 0x32;
    expect(() => listArchive(badMagic)).toThrow(ArchiveError);
    expect(() => encodeArchive([])).toThrow(ArchiveError);
    expect(() =>
      encodeArchive([
        { name: 'a', data: new Uint8Array(0) },
        { name: 'a', data: new Uint8Array(0) }
      ])
    ).toThrow(ArchiveError);
    const nfd = 'é.txt';
    expect(validateFileName(nfd)).not.toBeNull();
  });

  it('sanitizes and deduplicates names', () => {
    expect(sanitizeFileName('dir/sub/file.txt')).toBe('file.txt');
    expect(sanitizeFileName('..')).toBe('file');
    expect(sanitizeFileName('a\nb')).toBe('ab');
    expect(validateFileName(sanitizeFileName('x'.repeat(400) + '.txt'))).toBeNull();
    expect(uniqueFileNames(['a.txt', 'a.txt', 'a.txt', 'b'])).toEqual(['a.txt', 'a (2).txt', 'a (3).txt', 'b']);
  });
});
