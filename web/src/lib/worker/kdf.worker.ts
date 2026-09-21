import { deriveKeySet } from '../protocol/crypto';

interface Request {
  password: string;
  salt: Uint8Array;
  iterations: number;
}

const scope = self as unknown as {
  onmessage: ((e: MessageEvent<Request>) => void) | null;
  postMessage(message: unknown): void;
};

scope.onmessage = async (e) => {
  try {
    const keys = await deriveKeySet(e.data.password, e.data.salt, e.data.iterations);
    scope.postMessage({ ok: true, keys });
  } catch (err) {
    scope.postMessage({ ok: false, error: err instanceof Error ? err.message : String(err) });
  }
};
