<script lang="ts" module>
  export type { PanelTab } from '../ui.svelte';
</script>

<script lang="ts">
  import ActivityTab from './ActivityTab.svelte';
  import Dialog from './Dialog.svelte';
  import DevicesTab from './DevicesTab.svelte';
  import Icon, { type IconName } from './Icon.svelte';
  import SettingsTab from './SettingsTab.svelte';
  import StorageTab from './StorageTab.svelte';
  import WhatsNewTab from './WhatsNewTab.svelte';
  import type { PanelTab } from '../ui.svelte';
  import { APP_VERSION } from '../version';

  let { tab = $bindable<PanelTab | null>(null) }: { tab?: PanelTab | null } = $props();

  const tabs: { id: PanelTab; label: string; icon: IconName }[] = [
    { id: 'devices', label: 'Devices', icon: 'devices' },
    { id: 'storage', label: 'Storage', icon: 'storage' },
    { id: 'activity', label: 'Activity', icon: 'activity' },
    { id: 'settings', label: 'Settings', icon: 'settings' },
    { id: 'whatsnew', label: "What's new", icon: 'sparkles' }
  ];

  let open = $state(false);
  let current = $state<PanelTab>('devices');

  $effect(() => {
    if (tab) {
      current = tab;
      open = true;
    }
  });

  $effect(() => {
    if (!open) tab = null;
    else tab = current;
  });

  function onTabKey(e: KeyboardEvent) {
    const i = tabs.findIndex((t) => t.id === current);
    let next = -1;
    if (e.key === 'ArrowDown' || e.key === 'ArrowRight') next = (i + 1) % tabs.length;
    else if (e.key === 'ArrowUp' || e.key === 'ArrowLeft') next = (i - 1 + tabs.length) % tabs.length;
    if (next >= 0) {
      e.preventDefault();
      current = tabs[next].id;
      (e.currentTarget as HTMLElement).querySelector<HTMLElement>(`[data-tab="${current}"]`)?.focus();
    }
  }
</script>

<Dialog bind:open label="Settings" width={760} sheet>
  <div class="panel">
    <nav class="nav" aria-label="Sections">
      <div class="nav-title">yikz clipboard</div>
      <div role="tablist" aria-orientation="vertical" tabindex="-1" onkeydown={onTabKey}>
        {#each tabs as t (t.id)}
          <button
            role="tab"
            data-tab={t.id}
            id="tab-{t.id}"
            aria-selected={current === t.id}
            aria-controls="tabpanel"
            tabindex={current === t.id ? 0 : -1}
            class:on={current === t.id}
            onclick={() => (current = t.id)}
          >
            <Icon name={t.icon} size={15} />
            <span>{t.label}</span>
          </button>
        {/each}
      </div>
      <button class="nav-version num" onclick={() => (current = 'whatsnew')} title="What's new">v{APP_VERSION}</button>
    </nav>
    <div class="content scroll" id="tabpanel" role="tabpanel" tabindex="-1" aria-labelledby="tab-{current}">
      <button class="icon-btn close" aria-label="Close" onclick={() => (open = false)}><Icon name="x" size={16} /></button>
      {#if current === 'devices'}
        <DevicesTab />
      {:else if current === 'storage'}
        <StorageTab />
      {:else if current === 'activity'}
        <ActivityTab />
      {:else if current === 'whatsnew'}
        <WhatsNewTab />
      {:else}
        <SettingsTab />
      {/if}
    </div>
  </div>
</Dialog>

<style>
  .panel {
    display: flex;
    height: min(620px, calc(100vh - 48px));
    min-height: 0;
  }

  .nav {
    display: flex;
    flex-direction: column;
    width: 188px;
    flex: none;
    padding: 14px 8px;
    border-right: 1px solid var(--border);
    background: var(--surface-2);
  }

  .nav-title {
    padding: 4px 10px 12px;
    font-size: var(--text-xs);
    font-weight: 600;
    color: var(--text-3);
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }

  [role='tablist'] {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .nav-version {
    align-self: flex-start;
    margin: auto 10px 0;
    padding: 2px 7px;
    border: 1px solid var(--border);
    border-radius: 999px;
    background: var(--surface);
    color: var(--text-3);
    font-size: var(--text-xs);
    font-weight: 500;
    transition:
      color var(--dur-fast) var(--ease),
      border-color var(--dur-fast) var(--ease);
  }

  .nav-version:hover {
    color: var(--text);
    border-color: var(--border-strong);
  }

  [role='tab'] {
    display: flex;
    align-items: center;
    gap: 9px;
    height: 32px;
    padding: 0 10px;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--text-2);
    font-size: var(--text-md);
    font-weight: 500;
    text-align: left;
    transition:
      background var(--dur-fast) var(--ease),
      color var(--dur-fast) var(--ease);
  }

  [role='tab']:hover {
    background: var(--surface-hover);
    color: var(--text);
  }

  [role='tab'].on {
    background: var(--surface);
    color: var(--text);
    box-shadow: var(--shadow-sm), 0 0 0 1px var(--border);
  }

  .content {
    position: relative;
    flex: 1;
    min-width: 0;
    padding: 20px 22px 24px;
  }

  .close {
    position: absolute;
    top: 12px;
    right: 12px;
    z-index: 1;
  }

  .content > :global(:nth-child(2)) {
    padding-right: 32px;
  }

  @media (max-width: 759px) {
    .panel {
      flex-direction: column;
      height: 100%;
    }

    .nav {
      width: auto;
      padding: 10px 52px 0 10px;
      border-right: none;
      border-bottom: 1px solid var(--border);
    }

    .nav-title,
    .nav-version {
      display: none;
    }

    [role='tablist'] {
      flex-direction: row;
      overflow-x: auto;
      padding-bottom: 8px;
    }

    [role='tab'] {
      flex: none;
    }

    .content {
      padding: 16px;
    }

    .close {
      position: fixed;
      top: 10px;
      right: 10px;
    }
  }
</style>
