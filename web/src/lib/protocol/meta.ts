import { MIME_FILES, MIME_IMAGE, MIME_TEXT } from './constants';
import { fromUtf8, utf8, type Bytes } from './encoding';
import { makePreview } from './preview';

export type ItemKind = 'text' | 'image' | 'files';

export interface MetaFile {
  name: string;
  size: number;
}

export interface Meta {
  v: 1;
  mime: string;
  preview: string;
  sha256: string;
  image?: { width: number; height: number };
  files?: MetaFile[];
  source_app?: string;
}

export class MetaError extends Error {
  constructor(
    message: string,
    public readonly unsupported = false
  ) {
    super(message);
    this.name = 'MetaError';
  }
}

export function mimeForKind(kind: ItemKind): string {
  if (kind === 'text') return MIME_TEXT;
  if (kind === 'image') return MIME_IMAGE;
  return MIME_FILES;
}

export interface BuildMetaInput {
  kind: ItemKind;
  sha256: string;
  text?: string;
  image?: { width: number; height: number };
  files?: MetaFile[];
  sourceApp?: string;
}

export function buildMeta(input: BuildMetaInput): Meta {
  const meta: Meta = {
    v: 1,
    mime: mimeForKind(input.kind),
    preview: '',
    sha256: input.sha256
  };
  if (input.kind === 'text') {
    meta.preview = makePreview(input.text ?? '');
  } else if (input.kind === 'image') {
    if (!input.image) throw new Error('image meta needs dimensions');
    meta.image = { width: input.image.width, height: input.image.height };
  } else {
    const files = input.files ?? [];
    meta.preview = makePreview(files.map((f) => f.name).join('\n'));
    meta.files = files.map((f) => ({ name: f.name, size: f.size }));
  }
  if (input.sourceApp) meta.source_app = input.sourceApp;
  return meta;
}

export function serializeMeta(meta: Meta): Bytes {
  const ordered: Record<string, unknown> = {
    v: meta.v,
    mime: meta.mime,
    preview: meta.preview,
    sha256: meta.sha256
  };
  if (meta.image) ordered.image = { width: meta.image.width, height: meta.image.height };
  if (meta.files) ordered.files = meta.files.map((f) => ({ name: f.name, size: f.size }));
  if (meta.source_app !== undefined) ordered.source_app = meta.source_app;
  return utf8(JSON.stringify(ordered));
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

export function parseMeta(bytes: Uint8Array): Meta {
  let raw: unknown;
  try {
    raw = JSON.parse(fromUtf8(bytes));
  } catch {
    throw new MetaError('meta is not valid JSON');
  }
  if (!isRecord(raw)) throw new MetaError('meta is not an object');
  if (raw.v !== 1) throw new MetaError('unsupported meta version', true);
  if (typeof raw.mime !== 'string' || typeof raw.preview !== 'string' || typeof raw.sha256 !== 'string') {
    throw new MetaError('meta is missing required fields');
  }
  const meta: Meta = {
    v: 1,
    mime: raw.mime,
    preview: raw.preview,
    sha256: raw.sha256
  };
  if (isRecord(raw.image) && typeof raw.image.width === 'number' && typeof raw.image.height === 'number') {
    meta.image = { width: raw.image.width, height: raw.image.height };
  }
  if (Array.isArray(raw.files)) {
    meta.files = raw.files
      .filter((f): f is Record<string, unknown> => isRecord(f))
      .map((f) => ({
        name: String(f.name ?? ''),
        size: typeof f.size === 'number' ? f.size : 0
      }));
  }
  if (typeof raw.source_app === 'string') meta.source_app = raw.source_app;
  return meta;
}
