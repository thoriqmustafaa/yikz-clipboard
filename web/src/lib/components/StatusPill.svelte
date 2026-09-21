<script lang="ts">
  import { fly } from 'svelte/transition';
  import { platformIcon, platformLabel } from '../itemview';
  import { motion } from '../motion';
  import { relativeTime } from '../format';
  import { session } from '../session.svelte';
  import { sync } from '../sync.svelte';
  import Icon from './Icon.svelte';

  let { onactivity }: { onactivity: () => void } = $props();

  let open = $state(false);
  let now = $state(Date.now());
  let root = $state<HTMLElement | null>(null);
  let online = $state(typeof navigator === 'undefined' ? true : navigator.onLine);

  $effect(() => {
    const t = setInterval(() => (now = Date.now()), 1000);
    const up = () => (online = true);
    const down = () => (online = false);
    window.addEventListener('online', up);
    window.addEventListener('offline', down);
    return () => {
      clearInterval(t);
      window.removeEventListener('online', up);
      window.removeEventListener('offline', down);
    };
  });

  $effect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (root && !root.contains(e.target as Node)) open = false;
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') open = false;
    };
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey);
    };
  });

  const view = $derived.by(() => {
    const s = sync.status;
    if (!online || s === 'offline') return { tone: 'off', label: 'Offline', detail: 'Waiting for the network to come back.' };
    if (s === 'connected') {
      if (sync.catchingUp) return { tone: 'busy', label: 'Syncing', detail: 'Catching up on missed items.' };
      return { tone: 'ok', label: 'Connected', detail: sync.connectedSince ? `Connected ${relativeTime(sync.connectedSince, now)}` : 'Live' };
    }
    if (s === 'waiting' && sync.retryAt) {
      const secs = Math.max(0, Math.ceil((sync.retryAt - now) / 1000));
      return { tone: 'warn', label: secs > 0 ? `Reconnecting in ${secs}s` : 'Reconnecting', detail: `Attempt ${sync.attempt}. Retrying with backoff.` };
    }
    if (s === 'connecting') return { tone: 'warn', label: 'Connecting', detail: 'Opening a connection to the server.' };
    if (s === 'paused') return { tone: 'off', label: 'Paused', detail: 'Too many open tabs for this device. Click to reconnect.' };
    if (s === 'update') return { tone: 'err', label: 'Update required', detail: 'The server needs a newer app.' };
    return { tone: 'off', label: 'Not connected', detail: 'Sync is stopped.' };
  });

  const devices = $derived(
    [...sync.onlineDevices].sort((a, b) => (a.device_id === session.deviceId ? -1 : b.device_id === session.deviceId ? 1 : a.name.localeCompare(b.name)))
  );
</script>

<div class="wrap" bind:this={root}>
  <button
    class="pill {view.tone}"
    aria-haspopup="dialog"
    aria-expanded={open}
    aria-label="Connection: {view.label}"
    onclick={() => (open = !open)}
  >
    <span class="dot" aria-hidden="true"></span>
    <span class="txt num">{view.label}</span>
  </button>
  {#if open}
    <div class="pop" role="dialog" aria-label="Connection details" transition:fly={{ y: -4, duration: motion(150) }}>
      <div class="pop-head">
        <span class="dot big {view.tone}" aria-hidden="true"></span>
        <div>
          <div class="pop-title">{view.label}</div>
          <div class="pop-sub">{view.detail}</div>
        </div>
      </div>
      {#if sync.storageWarning}
        <div class="warn"><Icon name="alert" size={13} /> Server disk is low. Large uploads are paused.</div>
      {/if}
      <div class="sec-label">Online devices <span class="count num">{devices.length}</span></div>
      {#if devices.length === 0}
        <div class="empty">No devices online</div>
      {:else}
        <ul>
          {#each devices as d (d.device_id)}
            <li>
              <span class="dev-ic"><Icon name={platformIcon(d.platform)} size={14} /></span>
              <span class="dev-name">{d.name}</span>
              {#if d.device_id === session.deviceId}<span class="badge accent">You</span>{:else}<span class="plat">{platformLabel(d.platform)}</span>{/if}
            </li>
          {/each}
        </ul>
      {/if}
      <div class="pop-foot">
        <button
          class="btn sm"
          onclick={() => {
            sync.reconnectNow(true);
          }}
          disabled={!online}><Icon name="refresh" size={13} /> Reconnect</button
        >
        <button
          class="btn ghost sm"
          onclick={() => {
            open = false;
            onactivity();
          }}><Icon name="activity" size={13} /> Activity</button
        >
      </div>
    </div>
  {/if}
</div>

<style>
  .wrap {
    position: relative;
  }

  .pill {
    display: inline-flex;
    align-items: center;
    gap: 7px;
    height: 26px;
    padding: 0 10px 0 9px;
    border-radius: 999px;
    border: 1px solid var(--border);
    background: var(--surface);
    font-size: var(--text-sm);
    font-weight: 550;
    color: var(--text-2);
    white-space: nowrap;
    transition:
      background var(--dur-fast) var(--ease),
      border-color var(--dur-fast) var(--ease);
  }

  .pill:hover {
    background: var(--surface-2);
    border-color: var(--border-strong);
  }

  .dot {
    width: 7px;
    height: 7px;
    border-radius: 999px;
    background: var(--text-3);
    flex: none;
  }

  .ok .dot,
  .dot.ok {
    background: var(--success);
    box-shadow: 0 0 0 3px var(--success-soft);
  }

  .warn .dot,
  .dot.warn,
  .busy .dot,
  .dot.busy {
    background: var(--warning);
    box-shadow: 0 0 0 3px var(--warning-soft);
    animation: blink 1.2s ease-in-out infinite;
  }

  .busy .dot,
  .dot.busy {
    background: var(--accent);
    box-shadow: 0 0 0 3px var(--accent-soft);
  }

  .err .dot,
  .dot.err {
    background: var(--danger);
  }

  @keyframes blink {
    50% {
      opacity: 0.35;
    }
  }

  .pop {
    position: absolute;
    right: 0;
    top: calc(100% + 8px);
    width: 280px;
    z-index: 50;
    background: var(--surface);
    border: 1px solid var(--border-strong);
    border-radius: 12px;
    box-shadow: var(--shadow-lg);
    padding: 12px;
  }

  .pop-head {
    display: flex;
    gap: 10px;
    align-items: flex-start;
    padding: 2px 2px 10px;
    border-bottom: 1px solid var(--border);
  }

  .dot.big {
    width: 9px;
    height: 9px;
    margin-top: 5px;
  }

  .pop-title {
    font-weight: 600;
  }

  .pop-sub {
    font-size: var(--text-sm);
    color: var(--text-2);
  }

  .warn {
    display: flex;
    align-items: center;
    gap: 6px;
    margin-top: 10px;
    padding: 7px 9px;
    border-radius: var(--radius-sm);
    background: var(--warning-soft);
    color: var(--warning);
    font-size: var(--text-sm);
  }

  .sec-label {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin: 12px 2px 6px;
    font-size: var(--text-xs);
    font-weight: 600;
    color: var(--text-3);
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }

  .count {
    text-transform: none;
  }

  ul {
    list-style: none;
    margin: 0;
    padding: 0;
    max-height: 220px;
    overflow: auto;
  }

  li {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 4px;
    font-size: var(--text-md);
  }

  .dev-ic {
    display: grid;
    place-items: center;
    width: 24px;
    height: 24px;
    border-radius: 6px;
    background: var(--surface-3);
    color: var(--text-2);
  }

  .dev-name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .plat {
    font-size: var(--text-xs);
    color: var(--text-3);
  }

  .empty {
    font-size: var(--text-sm);
    color: var(--text-3);
    padding: 4px 2px 8px;
  }

  .pop-foot {
    display: flex;
    gap: 6px;
    margin-top: 10px;
    padding-top: 10px;
    border-top: 1px solid var(--border);
  }

  @media (max-width: 560px) {
    .txt {
      max-width: 90px;
      overflow: hidden;
      text-overflow: ellipsis;
    }
  }
</style>
