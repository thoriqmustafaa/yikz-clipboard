<script lang="ts">
  import type { CachedItem } from '../history.svelte';
  import { cachedThumb, canHaveThumb, loadThumb } from '../thumbs';
  import Icon, { type IconName } from './Icon.svelte';

  let { item, size = 22, icon }: { item: CachedItem; size?: number; icon: IconName } = $props();

  let el = $state<HTMLElement | null>(null);
  let url = $state<string | null>(null);
  let loaded = $state(false);

  $effect(() => {
    const it = item;
    const hit = cachedThumb(it.id);
    url = hit;
    loaded = !!hit;
    if (hit || !canHaveThumb(it) || !el) return;
    let cancelled = false;
    const start = () => {
      loadThumb(it).then((u) => {
        if (!cancelled) url = u;
      });
    };
    if (typeof IntersectionObserver === 'undefined') {
      start();
      return () => {
        cancelled = true;
      };
    }
    const io = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          io.disconnect();
          start();
        }
      },
      { rootMargin: '200px 0px' }
    );
    io.observe(el);
    return () => {
      cancelled = true;
      io.disconnect();
    };
  });
</script>

<span class="thumb" class:has={!!url} bind:this={el} style="--s: {size}px">
  {#if url}
    <img src={url} alt="" class:loaded onload={() => (loaded = true)} draggable="false" />
  {:else}
    <Icon name={icon} size={Math.round(size * 0.68)} />
  {/if}
</span>

<style>
  .thumb {
    width: var(--s);
    height: var(--s);
    flex: none;
    display: grid;
    place-items: center;
    border-radius: 5px;
    color: var(--text-2);
    background: var(--surface-3);
    overflow: hidden;
  }

  .thumb.has {
    background: var(--surface-2);
    box-shadow: inset 0 0 0 1px var(--border);
  }

  img {
    width: 100%;
    height: 100%;
    object-fit: cover;
    opacity: 0;
    transition: opacity var(--dur) var(--ease);
  }

  img.loaded {
    opacity: 1;
  }
</style>
