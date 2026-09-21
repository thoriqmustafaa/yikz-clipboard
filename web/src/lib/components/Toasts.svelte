<script lang="ts">
  import { fly } from 'svelte/transition';
  import { toasts } from '../toast.svelte';
  import { motion } from '../motion';
  import Icon from './Icon.svelte';

  const icons = { info: 'info', success: 'check', error: 'alert', warning: 'alert' } as const;
</script>

<div class="toasts" aria-live="polite" aria-atomic="false">
  {#each toasts.list as t (t.id)}
    <div class="toast {t.tone}" role={t.tone === 'error' ? 'alert' : 'status'} transition:fly={{ y: 10, duration: motion(180) }}>
      <span class="ic"><Icon name={icons[t.tone]} size={15} stroke={2} /></span>
      <div class="body">
        <div class="title">{t.title}</div>
        {#if t.detail}<div class="detail">{t.detail}</div>{/if}
      </div>
      {#if t.action}
        <button
          class="action"
          onclick={() => {
            t.action?.run();
            toasts.dismiss(t.id);
          }}>{t.action.label}</button
        >
      {/if}
      <button class="close" aria-label="Dismiss" onclick={() => toasts.dismiss(t.id)}><Icon name="x" size={13} /></button>
    </div>
  {/each}
</div>

<style>
  .toasts {
    position: fixed;
    left: 50%;
    bottom: 20px;
    transform: translateX(-50%);
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 8px;
    z-index: 100;
    pointer-events: none;
    width: min(440px, calc(100vw - 24px));
  }

  .toast {
    pointer-events: auto;
    display: flex;
    align-items: center;
    gap: 10px;
    width: 100%;
    padding: 9px 8px 9px 12px;
    border-radius: 10px;
    background: var(--surface);
    border: 1px solid var(--border-strong);
    box-shadow: var(--shadow-lg);
    color: var(--text);
    font-size: var(--text-md);
  }

  .ic {
    display: grid;
    place-items: center;
    width: 22px;
    height: 22px;
    border-radius: 999px;
    flex: none;
  }

  .info .ic {
    background: var(--accent-soft);
    color: var(--accent-text);
  }

  .success .ic {
    background: var(--success-soft);
    color: var(--success);
  }

  .error .ic {
    background: var(--danger-soft);
    color: var(--danger);
  }

  .warning .ic {
    background: var(--warning-soft);
    color: var(--warning);
  }

  .body {
    flex: 1;
    min-width: 0;
  }

  .title {
    font-weight: 550;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .detail {
    color: var(--text-2);
    font-size: var(--text-sm);
    margin-top: 1px;
  }

  .action {
    flex: none;
    height: 26px;
    padding: 0 10px;
    border-radius: var(--radius-sm);
    border: 1px solid var(--border-strong);
    background: var(--surface-2);
    font-size: var(--text-sm);
    font-weight: 550;
  }

  .action:hover {
    background: var(--surface-3);
  }

  .close {
    flex: none;
    display: grid;
    place-items: center;
    width: 24px;
    height: 24px;
    border: none;
    background: transparent;
    color: var(--text-3);
    border-radius: var(--radius-sm);
  }

  .close:hover {
    background: var(--surface-hover);
    color: var(--text);
  }
</style>
