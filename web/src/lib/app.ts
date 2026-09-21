import { applyIncoming } from './actions';
import { configureApi, request } from './api';
import { content } from './content.svelte';
import { history } from './history.svelte';
import { idbDestroy } from './idb';
import { activity } from './log.svelte';
import { clearRecentHashes } from './recent';
import { session } from './session.svelte';
import { settings } from './settings.svelte';
import { sync } from './sync.svelte';
import { clearThumbs } from './thumbs';
import { toasts } from './toast.svelte';

let unauthorizedHandled = false;

async function teardownLocal(): Promise<void> {
  sync.stop();
  await sync.resetState();
  content.clear();
  clearThumbs();
  clearRecentHashes();
  history.reset();
  await idbDestroy();
}

export async function handleUnauthorized(): Promise<void> {
  if (unauthorizedHandled || !session.token) return;
  unauthorizedHandled = true;
  activity.warn('auth', 'The server rejected this device token');
  await teardownLocal();
  await session.clearCredentials('This device was signed out or revoked. Sign in again.');
  unauthorizedHandled = false;
}

export async function signOut(): Promise<void> {
  activity.info('auth', 'Signing out');
  try {
    if (session.token) await request('POST', '/api/logout');
  } catch {
    activity.warn('auth', 'Logout request failed, clearing local data anyway');
  }
  await teardownLocal();
  await session.clearCredentials(null);
  toasts.info('Signed out');
}

export async function startApp(): Promise<void> {
  configureApi({ token: () => session.token, onUnauthorized: () => void handleUnauthorized() });
  settings.applyTheme();
  sync.setApplyHandler(applyIncoming);
  sync.setAuthLostHandler(() => void handleUnauthorized());
  session.onReady(async () => {
    const serverId = await sync.loadState();
    await history.loadFromCache(serverId);
    sync.start();
    void sync.refreshDevices();
    if (!session.me) void session.fetchMe().catch(() => undefined);
  });
  session.onLeave(() => {
    sync.stop();
  });
  await session.boot();
}
