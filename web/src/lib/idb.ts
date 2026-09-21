const DB_NAME = 'yikz-clipboard';
const DB_VERSION = 1;
const STORES = ['kv', 'items', 'thumbs'] as const;
type StoreName = (typeof STORES)[number];

let dbPromise: Promise<IDBDatabase | null> | null = null;
const memory: Record<StoreName, Map<string, unknown>> = { kv: new Map(), items: new Map(), thumbs: new Map() };

function openDb(): Promise<IDBDatabase | null> {
  if (dbPromise) return dbPromise;
  dbPromise = new Promise((resolve) => {
    if (typeof indexedDB === 'undefined') {
      resolve(null);
      return;
    }
    let req: IDBOpenDBRequest;
    try {
      req = indexedDB.open(DB_NAME, DB_VERSION);
    } catch {
      resolve(null);
      return;
    }
    req.onupgradeneeded = () => {
      const db = req.result;
      for (const s of STORES) if (!db.objectStoreNames.contains(s)) db.createObjectStore(s);
    };
    req.onsuccess = () => {
      const db = req.result;
      db.onversionchange = () => {
        db.close();
        dbPromise = null;
      };
      resolve(db);
    };
    req.onerror = () => resolve(null);
    req.onblocked = () => resolve(null);
  });
  return dbPromise;
}

function wrap<T>(req: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

function done(tx: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error);
  });
}

export async function idbGet<T>(store: StoreName, key: string): Promise<T | undefined> {
  const db = await openDb();
  if (!db) return memory[store].get(key) as T | undefined;
  try {
    return (await wrap(db.transaction(store, 'readonly').objectStore(store).get(key))) as T | undefined;
  } catch {
    return memory[store].get(key) as T | undefined;
  }
}

export async function idbGetAll<T>(store: StoreName): Promise<T[]> {
  const db = await openDb();
  if (!db) return [...memory[store].values()] as T[];
  try {
    return (await wrap(db.transaction(store, 'readonly').objectStore(store).getAll())) as T[];
  } catch {
    return [...memory[store].values()] as T[];
  }
}

export async function idbPut(store: StoreName, key: string, value: unknown): Promise<void> {
  await idbPutMany(store, [[key, value]]);
}

export async function idbPutMany(store: StoreName, entries: [string, unknown][]): Promise<void> {
  if (entries.length === 0) return;
  const db = await openDb();
  if (!db) {
    for (const [k, v] of entries) memory[store].set(k, v);
    return;
  }
  try {
    const tx = db.transaction(store, 'readwrite');
    const os = tx.objectStore(store);
    for (const [k, v] of entries) os.put(v, k);
    await done(tx);
  } catch {
    for (const [k, v] of entries) memory[store].set(k, v);
  }
}

export async function idbDelete(store: StoreName, keys: string[]): Promise<void> {
  for (const k of keys) memory[store].delete(k);
  if (keys.length === 0) return;
  const db = await openDb();
  if (!db) return;
  try {
    const tx = db.transaction(store, 'readwrite');
    const os = tx.objectStore(store);
    for (const k of keys) os.delete(k);
    await done(tx);
  } catch {
    return;
  }
}

export async function idbClear(stores: readonly StoreName[] = STORES): Promise<void> {
  for (const s of stores) memory[s].clear();
  const db = await openDb();
  if (!db) return;
  try {
    const tx = db.transaction([...stores], 'readwrite');
    for (const s of stores) tx.objectStore(s).clear();
    await done(tx);
  } catch {
    return;
  }
}

export async function idbDestroy(): Promise<void> {
  await idbClear();
  const db = await openDb();
  if (db) db.close();
  dbPromise = null;
  if (typeof indexedDB === 'undefined') return;
  await new Promise<void>((resolve) => {
    try {
      const req = indexedDB.deleteDatabase(DB_NAME);
      req.onsuccess = () => resolve();
      req.onerror = () => resolve();
      req.onblocked = () => resolve();
    } catch {
      resolve();
    }
  });
}
