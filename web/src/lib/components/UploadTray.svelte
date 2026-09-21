<script lang="ts">
  import { fly } from 'svelte/transition';
  import { formatBytes } from '../format';
  import { motion } from '../motion';
  import { uploads } from '../uploads.svelte';
  import Icon from './Icon.svelte';

  const kindIcon = { text: 'text', image: 'image', files: 'file' } as const;
</script>

{#if uploads.list.length}
  <div class="tray" aria-label="Uploads" role="region">
    {#each uploads.list as t (t.key)}
      <div class="up" class:err={t.phase === 'error'} class:done={t.phase === 'done'} transition:fly={{ x: 16, duration: motion(180) }}>
        <span class="ic">
          {#if t.phase === 'done'}
            <Icon name="check" size={14} stroke={2.2} />
          {:else if t.phase === 'error'}
            <Icon name="alert" size={14} />
          {:else}
            <Icon name={kindIcon[t.kind]} size={14} />
          {/if}
        </span>
        <div class="main">
          <div class="top">
            <span class="label" title={t.label}>{t.label}</span>
            <span class="meta num">
              {#if t.phase === 'preparing'}
                Encrypting
              {:else if t.phase === 'committing'}
                Finishing
              {:else if t.phase === 'done'}
                Sent
              {:else if t.phase === 'error'}
                Failed
              {:else}
                {Math.round(t.progress * 100)}%
              {/if}
            </span>
          </div>
          {#if t.phase === 'error'}
            <div class="error">{t.error}</div>
          {:else}
            <div class="bar" role="progressbar" aria-label="Upload progress" aria-valuemin="0" aria-valuemax="100" aria-valuenow={Math.round(t.progress * 100)}>
              <div class="fill" class:indeterminate={t.phase === 'preparing'} style="transform: scaleX({t.phase === 'preparing' ? 1 : t.progress})"></div>
            </div>
            {#if t.total > 0 && t.phase === 'uploading'}
              <div class="sub num">{formatBytes(t.sent)} of {formatBytes(t.total)}</div>
            {/if}
          {/if}
        </div>
        <div class="btns">
          {#if t.phase === 'error' && t.retryable}
            <button class="icon-btn" aria-label="Retry upload" title="Retry" onclick={() => uploads.retry(t)}><Icon name="refresh" size={14} /></button>
          {/if}
          {#if t.phase === 'error' || t.phase === 'done'}
            <button class="icon-btn" aria-label="Dismiss" title="Dismiss" onclick={() => uploads.dismiss(t)}><Icon name="x" size={14} /></button>
          {:else}
            <button class="icon-btn" aria-label="Cancel upload" title="Cancel" onclick={() => uploads.cancel(t)}><Icon name="x" size={14} /></button>
          {/if}
        </div>
      </div>
    {/each}
  </div>
{/if}

<style>
  .tray {
    position: fixed;
    right: 16px;
    bottom: 16px;
    z-index: 60;
    display: flex;
    flex-direction: column;
    gap: 8px;
    width: min(340px, calc(100vw - 32px));
  }

  .up {
    display: flex;
    align-items: flex-start;
    gap: 10px;
    padding: 10px 6px 10px 12px;
    border-radius: 10px;
    background: var(--surface);
    border: 1px solid var(--border-strong);
    box-shadow: var(--shadow-lg);
  }

  .ic {
    display: grid;
    place-items: center;
    width: 26px;
    height: 26px;
    border-radius: 7px;
    background: var(--accent-soft);
    color: var(--accent-text);
    flex: none;
  }

  .done .ic {
    background: var(--success-soft);
    color: var(--success);
  }

  .err .ic {
    background: var(--danger-soft);
    color: var(--danger);
  }

  .main {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 5px;
  }

  .top {
    display: flex;
    justify-content: space-between;
    gap: 8px;
    font-size: var(--text-sm);
  }

  .label {
    font-weight: 550;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .meta {
    color: var(--text-3);
    flex: none;
  }

  .sub {
    font-size: var(--text-xs);
    color: var(--text-3);
  }

  .error {
    font-size: var(--text-sm);
    color: var(--danger);
    line-height: 1.4;
  }

  .bar {
    height: 4px;
    border-radius: 999px;
    background: var(--surface-3);
    overflow: hidden;
  }

  .fill {
    height: 100%;
    background: var(--accent);
    transform-origin: left;
    transition: transform var(--dur) var(--ease);
  }

  .done .fill {
    background: var(--success);
  }

  .fill.indeterminate {
    width: 40%;
    animation: slide 1.1s ease-in-out infinite;
  }

  @keyframes slide {
    from {
      transform: translateX(-100%);
    }
    to {
      transform: translateX(250%);
    }
  }

  .btns {
    display: flex;
    gap: 2px;
  }

  .btns .icon-btn {
    width: 26px;
    height: 26px;
  }
</style>
