<script lang="ts">
  import { flip } from 'svelte/animate';
  import type { CachedItem } from '../history.svelte';
  import { history } from '../history.svelte';
  import { formatClock } from '../format';
  import { iconFor, titleFor } from '../itemview';
  import type { Group } from '../listing';
  import { modKey } from '../platform';
  import { motion } from '../motion';
  import Icon from './Icon.svelte';
  import Thumb from './Thumb.svelte';

  let {
    groups,
    selectedId,
    filtering,
    onselect,
    onactivate,
    oncompose
  }: {
    groups: Group[];
    selectedId: string | null;
    filtering: boolean;
    onselect: (item: CachedItem) => void;
    onactivate: (item: CachedItem) => void;
    oncompose: () => void;
  } = $props();

  let listEl = $state<HTMLElement | null>(null);
  let sentinel = $state<HTMLElement | null>(null);
  const empty = $derived(groups.length === 0);

  function enter(node: Element, params: { fresh: boolean }) {
    if (!params.fresh) return { duration: 0 };
    const h = (node as HTMLElement).offsetHeight;
    return {
      duration: motion(200),
      css: (t: number) => {
        const e = 1 - Math.pow(1 - t, 3);
        return `height:${e * h}px;opacity:${e};transform:translateY(${(1 - e) * -6}px);overflow:hidden`;
      }
    };
  }

  $effect(() => {
    if (!sentinel || !listEl) return;
    const io = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting) && history.hasMore && !history.loadingOlder) {
          history.loadOlder().catch(() => undefined);
        }
      },
      { root: listEl, rootMargin: '400px 0px' }
    );
    io.observe(sentinel);
    return () => io.disconnect();
  });

  $effect(() => {
    const id = selectedId;
    if (!id || !listEl) return;
    const row = listEl.querySelector<HTMLElement>(`[data-id="${id}"]`);
    row?.scrollIntoView({ block: 'nearest' });
  });
</script>

<div class="list scroll" bind:this={listEl} role="listbox" aria-label="Clipboard history" tabindex="-1">
  {#if empty && (!history.cacheLoaded || (history.syncing && history.items.length === 0))}
    <div class="skeletons" aria-hidden="true">
      {#each Array(9) as _, i (i)}
        <div class="sk-row">
          <span class="skeleton sk-icon"></span>
          <span class="skeleton sk-line" style="width: {40 + ((i * 37) % 45)}%"></span>
        </div>
      {/each}
    </div>
  {:else if empty && filtering}
    <div class="empty">
      <div class="empty-ic"><Icon name="search" size={20} /></div>
      <strong>No matches</strong>
      {#if history.loadingAll}
        <p>Searching older history<span class="dots"></span></p>
      {:else}
        <p>Try a different search or type filter.</p>
      {/if}
    </div>
  {:else if empty}
    <div class="empty">
      <div class="empty-ic"><Icon name="inbox" size={20} /></div>
      <strong>Nothing here yet</strong>
      <p>
        Copy something on another device, or press <kbd>{modKey()}</kbd><kbd>V</kbd> here to send your clipboard. Drop files
        anywhere to send them.
      </p>
      <button class="btn sm" onclick={oncompose}><Icon name="pencil" size={13} /> Write a note</button>
    </div>
  {:else}
    {#each groups as g (g.key)}
      <div class="group" role="group" aria-label={g.label}>
        <div class="group-label">
          {#if g.key === 'pinned'}<Icon name="pin" size={11} stroke={2} />{/if}
          {g.label}
        </div>
        {#each g.items as it (it.id)}
          <div
            class="row"
            class:selected={it.id === selectedId}
            class:fresh={history.fresh.has(it.id)}
            class:bad={!!it.err}
            data-id={it.id}
            role="option"
            tabindex="-1"
            aria-selected={it.id === selectedId}
            onclick={() => onselect(it)}
            ondblclick={() => onactivate(it)}
            onkeydown={() => {}}
            in:enter={{ fresh: history.fresh.has(it.id) }}
            animate:flip={{ duration: motion(180) }}
          >
            <Thumb item={it} icon={iconFor(it)} />
            <span class="title" class:muted={!!it.err}>{titleFor(it)}</span>
            {#if it.pinned && g.key !== 'pinned'}<span class="pin"><Icon name="pin" size={12} /></span>{/if}
            <span class="time num">{formatClock(it.createdMs)}</span>
          </div>
        {/each}
      </div>
    {/each}
    {#if history.hasMore || history.loadingOlder}
      <div class="more" aria-live="polite">
        {#if history.loadingOlder}
          <Icon name="loader" size={13} class="spin" /> Loading older items
        {:else}
          <button class="btn ghost sm" onclick={() => history.loadOlder().catch(() => undefined)}>Load older items</button>
        {/if}
      </div>
    {:else if history.items.length > 20}
      <div class="more end">You have reached the beginning of your history</div>
    {/if}
  {/if}
  <div class="sentinel" bind:this={sentinel} aria-hidden="true"></div>
</div>

<style>
  .list {
    height: 100%;
    padding: 6px 6px 16px;
    overscroll-behavior: contain;
  }

  .group {
    margin-bottom: 4px;
  }

  .group-label {
    position: sticky;
    top: -6px;
    z-index: 1;
    display: flex;
    align-items: center;
    gap: 5px;
    padding: 10px 10px 5px;
    font-size: var(--text-xs);
    font-weight: 600;
    color: var(--text-3);
    background: linear-gradient(var(--surface) 70%, transparent);
  }

  .row {
    position: relative;
    display: flex;
    align-items: center;
    gap: 10px;
    height: 36px;
    padding: 0 10px 0 8px;
    border-radius: var(--radius);
    cursor: default;
    user-select: none;
    transition: background var(--dur-fast) var(--ease);
  }

  .row:hover {
    background: var(--surface-hover);
  }

  .row.selected {
    background: var(--selection);
  }

  .row.selected::before {
    content: '';
    position: absolute;
    inset: 0;
    border-radius: inherit;
    box-shadow: inset 0 0 0 1px var(--selection-border);
    pointer-events: none;
  }

  .row.fresh {
    animation: glow 1.6s var(--ease-out);
  }

  @keyframes glow {
    0% {
      background: var(--accent-soft-2);
    }
  }

  .title {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: var(--text-md);
  }

  .title.muted {
    color: var(--text-3);
    font-style: italic;
  }

  .row.selected .title {
    color: var(--text);
  }

  .pin {
    color: var(--text-3);
    display: flex;
  }

  .time {
    flex: none;
    font-size: var(--text-xs);
    color: var(--text-3);
  }

  .more {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 6px;
    padding: 12px;
    color: var(--text-3);
    font-size: var(--text-sm);
  }

  .more.end {
    font-size: var(--text-xs);
  }

  .sentinel {
    height: 1px;
  }

  .empty {
    display: flex;
    flex-direction: column;
    align-items: center;
    text-align: center;
    gap: 6px;
    padding: 56px 24px;
    color: var(--text-2);
  }

  .empty strong {
    color: var(--text);
    font-weight: 600;
  }

  .empty p {
    margin: 0 0 8px;
    max-width: 260px;
    font-size: var(--text-sm);
    line-height: 1.6;
  }

  .empty kbd {
    margin: 0 1px;
  }

  .empty-ic {
    width: 40px;
    height: 40px;
    border-radius: 12px;
    display: grid;
    place-items: center;
    background: var(--surface-2);
    border: 1px solid var(--border);
    color: var(--text-3);
    margin-bottom: 6px;
  }

  .dots::after {
    content: '...';
  }

  .skeletons {
    padding: 8px 4px;
  }

  .sk-row {
    display: flex;
    align-items: center;
    gap: 10px;
    height: 36px;
    padding: 0 8px;
  }

  .sk-icon {
    width: 22px;
    height: 22px;
    border-radius: 5px;
  }

  .sk-line {
    height: 10px;
  }
</style>
