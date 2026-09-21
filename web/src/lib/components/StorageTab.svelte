<script lang="ts">
  import { onMount } from 'svelte';
  import { errorMessage, request } from '../api';
  import { formatBytes, pluralize } from '../format';
  import { parseStorage, type StorageInfo } from '../protocol/messages';
  import { sync } from '../sync.svelte';
  import Icon from './Icon.svelte';

  let info = $state<StorageInfo | null>(null);
  let error = $state<string | null>(null);
  let loading = $state(false);

  onMount(() => {
    void load();
  });

  async function load() {
    loading = true;
    error = null;
    try {
      info = parseStorage(await request('GET', '/api/storage', { retries: 2 }));
    } catch (err) {
      error = errorMessage(err);
    } finally {
      loading = false;
    }
  }

  const usedPct = $derived(info && info.limit_bytes ? Math.min(100, (info.used_bytes / info.limit_bytes) * 100) : 0);
  const pinnedPct = $derived(info && info.limit_bytes ? Math.min(100, (info.pinned_bytes / info.limit_bytes) * 100) : 0);
  const pinnedOfLimit = $derived(info && info.pinned_limit_bytes ? Math.min(100, (info.pinned_bytes / info.pinned_limit_bytes) * 100) : 0);
  const diskLow = $derived(!!info?.disk_low || !!sync.storageWarning);
</script>

<div class="tab-head">
  <div>
    <h3>Storage</h3>
    <p>Encrypted history stored on the server. Sizes include encryption overhead.</p>
  </div>
  <button class="icon-btn" onclick={load} aria-label="Refresh storage" title="Refresh"><Icon name="refresh" size={15} class={loading ? 'spin' : ''} /></button>
</div>

{#if error}
  <div class="err"><Icon name="alert" size={14} /> {error}</div>
{:else if !info}
  <div class="card"><div class="skeleton" style="height: 12px; width: 50%"></div><div class="skeleton" style="height: 8px; margin-top: 14px"></div></div>
{:else}
  {#if diskLow}
    <div class="warn">
      <Icon name="alert" size={15} />
      <div>
        <strong>Server disk is low</strong>
        <span>Uploads over 1 MB are rejected until free space is above {formatBytes(info.min_free_disk_bytes)}.</span>
      </div>
    </div>
  {/if}
  <div class="card">
    <div class="big">
      <span class="num"><strong>{formatBytes(info.used_bytes)}</strong> of {formatBytes(info.limit_bytes)}</span>
      <span class="pct num">{usedPct.toFixed(usedPct < 10 ? 1 : 0)}%</span>
    </div>
    <div class="meter" role="meter" aria-label="Storage used" aria-valuemin="0" aria-valuemax={info.limit_bytes} aria-valuenow={info.used_bytes}>
      <div class="seg used" style="width: {usedPct}%"></div>
      <div class="seg pinned" style="width: {pinnedPct}%"></div>
    </div>
    <div class="legend">
      <span><i class="sw used"></i>History</span>
      <span><i class="sw pinned"></i>Pinned</span>
    </div>
  </div>

  <dl class="stats">
    <div>
      <dt>Items</dt>
      <dd class="num">{pluralize(info.item_count, 'item')}</dd>
    </div>
    <div>
      <dt>Pinned</dt>
      <dd class="num">{formatBytes(info.pinned_bytes)} of {formatBytes(info.pinned_limit_bytes)} <span class="dim">({pinnedOfLimit.toFixed(0)}%)</span></dd>
    </div>
    <div>
      <dt>Retention</dt>
      <dd class="num">{pluralize(info.retention_days, 'day')} for unpinned items</dd>
    </div>
    <div>
      <dt>Free disk on server</dt>
      <dd class="num" class:low={diskLow}>{formatBytes(info.free_disk_bytes)}</dd>
    </div>
  </dl>
{/if}

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

  .card {
    padding: 14px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--surface-2);
  }

  .big {
    display: flex;
    justify-content: space-between;
    align-items: baseline;
    color: var(--text-2);
    margin-bottom: 10px;
  }

  .big strong {
    color: var(--text);
    font-size: var(--text-xl);
    font-weight: 650;
    letter-spacing: -0.01em;
  }

  .pct {
    font-size: var(--text-sm);
  }

  .meter {
    position: relative;
    height: 8px;
    border-radius: 999px;
    background: var(--surface-3);
    overflow: hidden;
  }

  .seg {
    position: absolute;
    left: 0;
    top: 0;
    bottom: 0;
    border-radius: 999px;
    transition: width var(--dur-slow) var(--ease);
  }

  .seg.used {
    background: var(--accent);
  }

  .seg.pinned {
    background: var(--warning);
  }

  .legend {
    display: flex;
    gap: 14px;
    margin-top: 10px;
    font-size: var(--text-xs);
    color: var(--text-2);
  }

  .legend span {
    display: inline-flex;
    align-items: center;
    gap: 5px;
  }

  .sw {
    width: 8px;
    height: 8px;
    border-radius: 2px;
  }

  .sw.used {
    background: var(--accent);
  }

  .sw.pinned {
    background: var(--warning);
  }

  .stats {
    margin: 14px 0 0;
  }

  .stats > div {
    display: flex;
    justify-content: space-between;
    gap: 12px;
    padding: 9px 2px;
    font-size: var(--text-md);
  }

  .stats > div + div {
    border-top: 1px solid var(--border);
  }

  dt {
    color: var(--text-2);
  }

  dd {
    margin: 0;
    text-align: right;
  }

  .dim {
    color: var(--text-3);
  }

  .low {
    color: var(--warning);
    font-weight: 600;
  }

  .warn {
    display: flex;
    gap: 10px;
    padding: 10px 12px;
    margin-bottom: 12px;
    border-radius: var(--radius);
    background: var(--warning-soft);
    color: var(--warning);
    font-size: var(--text-sm);
  }

  .warn div {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .warn span {
    color: var(--text-2);
  }

  .err {
    display: flex;
    align-items: center;
    gap: 6px;
    color: var(--danger);
    font-size: var(--text-sm);
  }
</style>
