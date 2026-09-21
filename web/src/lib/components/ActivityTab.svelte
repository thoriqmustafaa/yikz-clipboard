<script lang="ts">
  import { activity, type LogLevel } from '../log.svelte';
  import { saveBlob } from '../save';
  import Icon from './Icon.svelte';

  type Filter = 'all' | LogLevel;
  const filters: { id: Filter; label: string }[] = [
    { id: 'all', label: 'All' },
    { id: 'error', label: 'Errors' },
    { id: 'warn', label: 'Warnings' },
    { id: 'info', label: 'Info' },
    { id: 'debug', label: 'Debug' }
  ];

  let level = $state<Filter>('all');
  let query = $state('');
  let listEl = $state<HTMLElement | null>(null);
  let follow = $state(true);

  const shown = $derived.by(() => {
    const q = query.trim().toLowerCase();
    return activity.entries.filter(
      (e) => (level === 'all' || e.level === level) && (!q || e.message.toLowerCase().includes(q) || e.category.includes(q))
    );
  });

  const counts = $derived.by(() => {
    const c: Record<string, number> = { all: activity.entries.length, error: 0, warn: 0, info: 0, debug: 0 };
    for (const e of activity.entries) c[e.level]++;
    return c;
  });

  $effect(() => {
    void shown.length;
    if (follow && listEl) listEl.scrollTop = listEl.scrollHeight;
  });

  function onScroll() {
    if (!listEl) return;
    follow = listEl.scrollHeight - listEl.scrollTop - listEl.clientHeight < 24;
  }

  function exportLog() {
    const header = `yikz clipboard activity log, exported ${new Date().toISOString()}\n${navigator.userAgent}\n\n`;
    const name = `yikz-clipboard-log-${new Date().toISOString().replace(/[:.]/g, '-')}.txt`;
    saveBlob(new Blob([header + activity.exportText(shown)], { type: 'text/plain;charset=utf-8' }), name);
  }

  function time(ts: number): string {
    const d = new Date(ts);
    const p = (n: number, w = 2) => String(n).padStart(w, '0');
    return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`;
  }
</script>

<div class="tab-head">
  <div>
    <h3>Activity</h3>
    <p>Connection and sync events from this session. Kept in memory only.</p>
  </div>
  <div class="head-btns">
    <button class="btn sm" onclick={exportLog} disabled={shown.length === 0}><Icon name="export" size={13} /> Export</button>
    <button class="btn sm ghost" onclick={() => activity.clear()} disabled={activity.entries.length === 0}>Clear</button>
  </div>
</div>

<div class="toolbar">
  <div class="seg" role="radiogroup" aria-label="Level">
    {#each filters as f (f.id)}
      <button role="radio" aria-checked={level === f.id} class:on={level === f.id} onclick={() => (level = f.id)}>
        {f.label}
        {#if counts[f.id]}<span class="n num">{counts[f.id]}</span>{/if}
      </button>
    {/each}
  </div>
  <div class="search">
    <Icon name="search" size={13} />
    <input class="input" placeholder="Filter" bind:value={query} aria-label="Filter log messages" />
  </div>
</div>

<div class="log scroll" bind:this={listEl} onscroll={onScroll} role="log" aria-live="polite">
  {#if shown.length === 0}
    <div class="empty">No events{level !== 'all' || query ? ' match this filter' : ' yet'}</div>
  {:else}
    {#each shown as e (e.id)}
      <div class="entry {e.level}">
        <span class="t num">{time(e.ts)}</span>
        <span class="lv">{e.level}</span>
        <span class="cat">{e.category}</span>
        <span class="msg">{e.message}</span>
      </div>
    {/each}
  {/if}
</div>

<style>
  .tab-head {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 12px;
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

  .head-btns {
    display: flex;
    gap: 4px;
    flex: none;
  }

  .toolbar {
    display: flex;
    gap: 8px;
    align-items: center;
    flex-wrap: wrap;
    margin-bottom: 10px;
  }

  .seg {
    display: inline-flex;
    padding: 2px;
    border-radius: var(--radius);
    background: var(--surface-3);
    gap: 2px;
  }

  .seg button {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    height: 24px;
    padding: 0 9px;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--text-2);
    font-size: var(--text-sm);
    font-weight: 500;
  }

  .seg button.on {
    background: var(--surface);
    color: var(--text);
    box-shadow: var(--shadow-sm);
  }

  .n {
    font-size: var(--text-xs);
    color: var(--text-3);
  }

  .search {
    position: relative;
    flex: 1;
    min-width: 140px;
    color: var(--text-3);
  }

  .search :global(.icon) {
    position: absolute;
    left: 9px;
    top: 9px;
  }

  .search .input {
    height: 28px;
    padding-left: 28px;
    font-size: var(--text-sm);
  }

  .log {
    height: min(420px, 52vh);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--surface-2);
    font-family: var(--font-mono);
    font-size: 11.5px;
    line-height: 1.55;
    padding: 6px 0;
  }

  .entry {
    display: grid;
    grid-template-columns: auto 42px 78px 1fr;
    gap: 8px;
    padding: 2px 10px;
    align-items: baseline;
  }

  .entry:hover {
    background: var(--surface-hover);
  }

  .t {
    color: var(--text-3);
  }

  .lv {
    text-transform: uppercase;
    font-size: 10px;
    font-weight: 600;
    color: var(--text-3);
  }

  .info .lv {
    color: var(--accent-text);
  }

  .warn .lv {
    color: var(--warning);
  }

  .error .lv,
  .error .msg {
    color: var(--danger);
  }

  .cat {
    color: var(--text-2);
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .msg {
    word-break: break-word;
    color: var(--text);
  }

  .debug .msg {
    color: var(--text-2);
  }

  .empty {
    padding: 28px;
    text-align: center;
    color: var(--text-3);
    font-family: var(--font-sans);
    font-size: var(--text-sm);
  }

  @media (max-width: 560px) {
    .entry {
      grid-template-columns: auto 40px 1fr;
    }

    .cat {
      display: none;
    }
  }
</style>
