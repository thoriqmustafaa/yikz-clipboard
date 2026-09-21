import { encodeArchive } from '../src/lib/protocol/archive';
import { vector } from './load';

export function patternArchive(): Uint8Array {
  const c = vector('files_archive.json').cases.find((c: any) => c.name === 'single_large_file_header');
  const f = c.files[0];
  const data = new Uint8Array(f.size);
  for (let i = 0; i < data.length; i++) data[i] = i % 251;
  return encodeArchive([{ name: f.name, data }]);
}
