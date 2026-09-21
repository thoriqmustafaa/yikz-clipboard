<script lang="ts">
  import { onMount } from 'svelte';
  import { ApiError, errorMessage, request } from '../api';
  import { handleUnauthorized } from '../app';
  import { relativeTime } from '../format';
  import { platformIcon, platformLabel } from '../itemview';
  import { buildRenameRequest, type Device, parseDevice, validateDeviceName } from '../protocol/messages';
  import { session } from '../session.svelte';
  import { sync } from '../sync.svelte';
  import { toasts } from '../toast.svelte';
  import ConfirmDialog from './ConfirmDialog.svelte';
  import Icon from './Icon.svelte';

  let loading = $state(false);
  let editing = $state<string | null>(null);
  let draft = $state('');
  let draftError = $state<string | null>(null);
  let saving = $state(false);
  let confirmOpen = $state(false);
  let target = $state<Device | null>(null);
  let now = $state(Date.now());

  onMount(() => {
    void refresh();
    const t = setInterval(() => (now = Date.now()), 30000);
    return () => clearInterval(t);
  });

  async function refresh() {
    loading = true;
    await sync.refreshDevices();
    loading = false;
  }

  const onlineIds = $derived(new Set(sync.onlineDevices.map((d) => d.device_id)));
  const list = $derived(
    [...sync.devices].sort((a, b) => Number(b.current) - Number(a.current) || Number(a.revoked) - Number(b.revoked) || a.created_at.localeCompare(b.created_at))
  );

  function startEdit(d: Device) {
    editing = d.id;
    draft = d.name;
    draftError = null;
  }

  async function saveEdit(d: Device) {
    draftError = validateDeviceName(draft);
    if (draftError) return;
    if (draft.trim() === d.name) {
      editing = null;
      return;
    }
    saving = true;
    try {
      const updated = parseDevice(await request('PATCH', `/api/devices/${d.id}`, { body: buildRenameRequest(draft) }));
      sync.devices = sync.devices.map((x) => (x.id === d.id ? updated : x));
      if (d.current) await session.rename(updated.name);
      editing = null;
      toasts.success('Device renamed');
    } catch (err) {
      draftError = errorMessage(err);
    } finally {
      saving = false;
    }
  }

  function askRevoke(d: Device) {
    target = d;
    confirmOpen = true;
  }

  async function revoke() {
    const d = target;
    if (!d) return;
    try {
      await request('DELETE', `/api/devices/${d.id}`);
    } catch (err) {
      if (!(err instanceof ApiError && err.code === 'not_found')) {
        toasts.error(d.revoked ? 'Could not remove device' : 'Could not revoke device', errorMessage(err));
        return;
      }
    }
    if (d.current) {
      await handleUnauthorized();
      return;
    }
    toasts.success(d.revoked ? 'Device removed' : 'Device revoked');
    await sync.refreshDevices();
  }

  function editKey(e: KeyboardEvent, d: Device) {
    if (e.key === 'Enter') {
      e.preventDefault();
      void saveEdit(d);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      editing = null;
    }
  }
</script>

<div class="tab-head">
  <div>
    <h3>Devices</h3>
    <p>Every device signed in to this account. Revoking signs a device out immediately.</p>
  </div>
  <button class="icon-btn" onclick={refresh} aria-label="Refresh devices" title="Refresh"><Icon name="refresh" size={15} class={loading ? 'spin' : ''} /></button>
</div>

{#if list.length === 0}
  <div class="list">
    {#each Array(3) as _, i (i)}
      <div class="dev"><span class="skeleton sk-ic"></span><span class="skeleton sk-line"></span></div>
    {/each}
  </div>
{:else}
  <ul class="list">
    {#each list as d (d.id)}
      {@const isOnline = !d.revoked && (d.online || onlineIds.has(d.id))}
      <li class="dev" class:revoked={d.revoked}>
        <span class="ic"><Icon name={platformIcon(d.platform)} size={16} /></span>
        <div class="main">
          {#if editing === d.id}
            <input
              class="input sm"
              bind:value={draft}
              onkeydown={(e) => editKey(e, d)}
              maxlength="64"
              aria-label="Device name"
              aria-invalid={draftError ? 'true' : undefined}
              disabled={saving}
            />
            {#if draftError}<small class="err">{draftError}</small>{/if}
          {:else}
            <div class="name">
              <span class="nm">{d.name}</span>
              {#if d.current}<span class="badge accent">This browser</span>{/if}
              {#if d.revoked}<span class="badge danger">Revoked</span>{/if}
            </div>
            <div class="sub num">
              {platformLabel(d.platform)}
              <span class="sep">·</span>
              {#if isOnline}
                <span class="online"><span class="dot"></span>Online</span>
              {:else}
                Last seen {relativeTime(Date.parse(d.last_seen_at), now)}
              {/if}
            </div>
          {/if}
        </div>
        <div class="btns">
          {#if editing === d.id}
            <button class="btn sm" onclick={() => (editing = null)} disabled={saving}>Cancel</button>
            <button class="btn sm primary" onclick={() => saveEdit(d)} disabled={saving}>Save</button>
          {:else}
            {#if !d.revoked}
              <button class="icon-btn" onclick={() => startEdit(d)} aria-label="Rename {d.name}" title="Rename"><Icon name="pencil" size={14} /></button>
            {/if}
            <button class="btn sm ghost danger-text" onclick={() => askRevoke(d)}>{d.revoked ? 'Remove' : 'Revoke'}</button>
          {/if}
        </div>
      </li>
    {/each}
  </ul>
{/if}

<ConfirmDialog
  bind:open={confirmOpen}
  title={target?.revoked ? 'Remove device?' : target?.current ? 'Revoke this browser?' : 'Revoke device?'}
  message={target?.revoked
    ? `${target?.name} will be removed from the list. Its items stay in history.`
    : target?.current
      ? 'This browser will be signed out and its local cache cleared.'
      : `${target?.name ?? 'This device'} will be signed out right away and must sign in again to sync.`}
  confirmLabel={target?.revoked ? 'Remove' : 'Revoke'}
  onconfirm={revoke}
/>

<style>
  .tab-head {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 14px;
  }

  h3 {
    margin: 0 0 3px;
    font-size: var(--text-lg);
    font-weight: 600;
  }

  p {
    margin: 0;
    color: var(--text-2);
    font-size: var(--text-sm);
  }

  .list {
    list-style: none;
    margin: 0;
    padding: 0;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    overflow: hidden;
  }

  .dev {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 10px 10px 10px 12px;
    min-height: 56px;
  }

  .dev + .dev {
    border-top: 1px solid var(--border);
  }

  .dev.revoked {
    opacity: 0.7;
  }

  .ic {
    display: grid;
    place-items: center;
    width: 32px;
    height: 32px;
    border-radius: 8px;
    background: var(--surface-3);
    color: var(--text-2);
    flex: none;
  }

  .main {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .name {
    display: flex;
    align-items: center;
    gap: 6px;
    min-width: 0;
  }

  .nm {
    font-weight: 550;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .sub {
    display: flex;
    align-items: center;
    gap: 5px;
    font-size: var(--text-sm);
    color: var(--text-3);
  }

  .online {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    color: var(--success);
  }

  .dot {
    width: 6px;
    height: 6px;
    border-radius: 999px;
    background: currentColor;
  }

  .btns {
    display: flex;
    align-items: center;
    gap: 4px;
    flex: none;
  }

  .input.sm {
    height: 28px;
  }

  .err {
    color: var(--danger);
    font-size: var(--text-xs);
  }

  .danger-text:hover:not(:disabled) {
    color: var(--danger);
    background: var(--danger-soft);
  }

  .sk-ic {
    width: 32px;
    height: 32px;
    border-radius: 8px;
  }

  .sk-line {
    height: 10px;
    width: 40%;
  }
</style>
