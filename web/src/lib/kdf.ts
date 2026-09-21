import { deriveKeySet, type KeySet } from './protocol/crypto';

interface WorkerReply {
  ok: boolean;
  keys?: KeySet;
  error?: string;
}

export function deriveKeysOffThread(password: string, salt: Uint8Array, iterations: number): Promise<KeySet> {
  if (typeof Worker === 'undefined') return deriveKeySet(password, salt, iterations);
  return new Promise<KeySet>((resolve, reject) => {
    let worker: Worker;
    try {
      worker = new Worker(new URL('./worker/kdf.worker.ts', import.meta.url), { type: 'module' });
    } catch {
      deriveKeySet(password, salt, iterations).then(resolve, reject);
      return;
    }
    const fallback = () => {
      worker.terminate();
      deriveKeySet(password, salt, iterations).then(resolve, reject);
    };
    worker.onmessage = (e: MessageEvent<WorkerReply>) => {
      worker.terminate();
      if (e.data.ok && e.data.keys) resolve(e.data.keys);
      else reject(new Error(e.data.error || 'key derivation failed'));
    };
    worker.onerror = (e) => {
      e.preventDefault();
      fallback();
    };
    worker.onmessageerror = fallback;
    worker.postMessage({ password, salt: new Uint8Array(salt), iterations });
  });
}
