import { parseErrorBody } from './protocol/messages';

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly details?: Record<string, unknown>,
    public readonly retryAfter?: number
  ) {
    super(message || code);
    this.name = 'ApiError';
  }

  get retryable(): boolean {
    return this.status === 0 || this.status === 500 || this.status === 502 || this.status === 503 || this.status === 504;
  }
}

export class NetworkError extends ApiError {
  constructor(message = 'Network error') {
    super(0, 'network', message);
    this.name = 'NetworkError';
  }
}

export class AbortedError extends Error {
  constructor() {
    super('aborted');
    this.name = 'AbortedError';
  }
}

interface AuthHooks {
  token: () => string | null;
  onUnauthorized: () => void;
}

let hooks: AuthHooks = { token: () => null, onUnauthorized: () => {} };

export function configureApi(h: AuthHooks): void {
  hooks = h;
}

export interface RequestOptions {
  body?: unknown;
  raw?: BodyInit;
  contentType?: string;
  auth?: boolean;
  signal?: AbortSignal;
  retries?: number;
  expect?: 'json' | 'bytes' | 'blob' | 'none';
}

export function retryDelayMs(attempt: number): number {
  return Math.random() * Math.min(60000, 1000 * Math.pow(2, attempt));
}

export function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new AbortedError());
      return;
    }
    const t = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(t);
      reject(new AbortedError());
    };
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

async function toApiError(res: Response): Promise<ApiError> {
  let body: unknown = null;
  try {
    body = await res.json();
  } catch {
    body = null;
  }
  const e = parseErrorBody(body);
  const ra = res.headers.get('Retry-After');
  const retryAfter = ra && /^\d+$/.test(ra) ? Number(ra) : undefined;
  return new ApiError(res.status, e?.code ?? `http_${res.status}`, e?.message ?? res.statusText, e?.details, retryAfter);
}

async function once(method: string, path: string, opts: RequestOptions): Promise<Response> {
  const headers: Record<string, string> = {};
  let body: BodyInit | undefined;
  if (opts.raw !== undefined) {
    body = opts.raw;
    headers['Content-Type'] = opts.contentType ?? 'application/octet-stream';
  } else if (opts.body !== undefined) {
    body = JSON.stringify(opts.body);
    headers['Content-Type'] = 'application/json';
  }
  if (opts.auth !== false) {
    const t = hooks.token();
    if (!t) throw new ApiError(401, 'unauthorized', 'Not signed in');
    headers.Authorization = `Bearer ${t}`;
  }
  try {
    return await fetch(path, {
      method,
      headers,
      body,
      signal: opts.signal,
      cache: 'no-store',
      credentials: 'omit'
    });
  } catch (err) {
    if (opts.signal?.aborted) throw new AbortedError();
    throw new NetworkError(err instanceof Error ? err.message : 'Network error');
  }
}

export async function request<T = unknown>(method: string, path: string, opts: RequestOptions = {}): Promise<T> {
  const retries = opts.retries ?? 0;
  for (let attempt = 0; ; attempt++) {
    try {
      const res = await once(method, path, opts);
      if (!res.ok) {
        const err = await toApiError(res);
        if (err.status === 401 && opts.auth !== false) hooks.onUnauthorized();
        throw err;
      }
      const expect = opts.expect ?? (res.status === 204 ? 'none' : 'json');
      if (expect === 'none' || res.status === 204) return undefined as T;
      if (expect === 'bytes') return new Uint8Array(await res.arrayBuffer()) as T;
      if (expect === 'blob') return (await res.blob()) as T;
      try {
        return (await res.json()) as T;
      } catch {
        throw new ApiError(res.status, 'invalid_response', 'Server sent an invalid response');
      }
    } catch (err) {
      if (err instanceof ApiError && err.retryable && attempt < retries) {
        await sleep(retryDelayMs(attempt), opts.signal);
        continue;
      }
      throw err;
    }
  }
}

export function uploadWithProgress(
  method: string,
  path: string,
  body: Uint8Array<ArrayBuffer>,
  onProgress: (loaded: number) => void,
  signal?: AbortSignal
): Promise<void> {
  return new Promise((resolve, reject) => {
    const token = hooks.token();
    if (!token) {
      reject(new ApiError(401, 'unauthorized', 'Not signed in'));
      return;
    }
    const xhr = new XMLHttpRequest();
    xhr.open(method, path);
    xhr.setRequestHeader('Authorization', `Bearer ${token}`);
    xhr.setRequestHeader('Content-Type', 'application/octet-stream');
    xhr.upload.onprogress = (e) => onProgress(e.loaded);
    const onAbort = () => xhr.abort();
    signal?.addEventListener('abort', onAbort, { once: true });
    xhr.onload = () => {
      signal?.removeEventListener('abort', onAbort);
      if (xhr.status >= 200 && xhr.status < 300) {
        onProgress(body.length);
        resolve();
        return;
      }
      let parsed: unknown = null;
      try {
        parsed = JSON.parse(xhr.responseText);
      } catch {
        parsed = null;
      }
      const e = parseErrorBody(parsed);
      const err = new ApiError(xhr.status, e?.code ?? `http_${xhr.status}`, e?.message ?? xhr.statusText, e?.details);
      if (xhr.status === 401) hooks.onUnauthorized();
      reject(err);
    };
    xhr.onerror = () => {
      signal?.removeEventListener('abort', onAbort);
      reject(new NetworkError());
    };
    xhr.onabort = () => reject(new AbortedError());
    xhr.send(body);
  });
}

export function errorMessage(err: unknown): string {
  if (err instanceof NetworkError) return 'Cannot reach the server';
  if (err instanceof ApiError) {
    switch (err.code) {
      case 'disk_low':
        return 'Server disk is low. Uploads over 1 MB are paused until space frees up.';
      case 'item_too_large':
        return 'Too large for the server storage limit.';
      case 'pinned_limit':
        return 'Pinned storage is full. Unpin something first.';
      case 'rate_limited':
        return 'Too many attempts. Try again later.';
      case 'not_found':
        return 'This item no longer exists.';
      case 'key_check_missing':
        return 'The encryption key is not set on the server yet.';
      case 'internal':
        return 'The server hit an internal error.';
      default:
        return err.message || `Request failed (${err.status})`;
    }
  }
  if (err instanceof Error) return err.message;
  return String(err);
}
