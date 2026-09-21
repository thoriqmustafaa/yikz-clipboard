<script lang="ts">
  import { onMount } from 'svelte';
  import { startApp } from './lib/app';
  import { session } from './lib/session.svelte';
  import { settings } from './lib/settings.svelte';
  import Login from './lib/components/Login.svelte';
  import Main from './lib/components/Main.svelte';
  import Toasts from './lib/components/Toasts.svelte';
  import Icon from './lib/components/Icon.svelte';

  let bootError = $state<string | null>(null);

  onMount(() => {
    startApp().catch((err) => {
      bootError = err instanceof Error ? err.message : String(err);
    });
  });

  $effect(() => {
    void settings.theme;
    settings.applyTheme();
  });
</script>

{#if bootError}
  <div class="center">
    <div class="boot-error" role="alert">
      <Icon name="alert" size={20} />
      <div>
        <strong>Something went wrong while starting</strong>
        <p>{bootError}</p>
        <button class="btn" onclick={() => location.reload()}>Reload</button>
      </div>
    </div>
  </div>
{:else if session.phase === 'boot'}
  <div class="center" aria-busy="true" aria-label="Loading">
    <div class="boot-mark">
      <Icon name="clipboard" size={22} />
    </div>
  </div>
{:else if session.phase === 'ready'}
  <Main />
{:else if session.phase === 'update'}
  <div class="center">
    <div class="boot-error" role="alert">
      <Icon name="alert" size={20} />
      <div>
        <strong>Update required</strong>
        <p>{session.updateReason || 'This app is not compatible with the server.'}</p>
        <button class="btn" onclick={() => location.reload()}>Reload</button>
      </div>
    </div>
  </div>
{:else}
  <Login />
{/if}

<Toasts />

<style>
  .center {
    height: 100%;
    display: grid;
    place-items: center;
    padding: 24px;
  }

  .boot-mark {
    width: 44px;
    height: 44px;
    border-radius: 12px;
    display: grid;
    place-items: center;
    background: var(--accent);
    color: #fff;
    box-shadow: var(--shadow-md);
    animation: pulse 1.4s ease-in-out infinite;
  }

  @keyframes pulse {
    50% {
      opacity: 0.6;
      transform: scale(0.96);
    }
  }

  .boot-error {
    display: flex;
    gap: 12px;
    max-width: 420px;
    padding: 18px;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius-lg);
    box-shadow: var(--shadow-md);
    color: var(--text);
  }

  .boot-error :global(.icon) {
    color: var(--danger);
    margin-top: 1px;
  }

  .boot-error p {
    margin: 4px 0 12px;
    color: var(--text-2);
  }
</style>
