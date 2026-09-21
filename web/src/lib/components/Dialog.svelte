<script lang="ts">
  import type { Snippet } from 'svelte';

  let {
    open = $bindable(false),
    label,
    width = 440,
    sheet = false,
    onclose,
    children
  }: {
    open?: boolean;
    label: string;
    width?: number;
    sheet?: boolean;
    onclose?: () => void;
    children: Snippet;
  } = $props();

  let el = $state<HTMLDialogElement | null>(null);

  $effect(() => {
    if (!el) return;
    if (open && !el.open) el.showModal();
    else if (!open && el.open) el.close();
  });

  function handleClose() {
    if (open) {
      open = false;
      onclose?.();
    }
  }

  $effect(() => {
    const d = el;
    if (!d) return;
    const onDown = (e: MouseEvent) => {
      if (e.target === d) handleClose();
    };
    d.addEventListener('click', onDown);
    return () => d.removeEventListener('click', onDown);
  });
</script>

<dialog
  bind:this={el}
  class:sheet
  aria-label={label}
  style="--w: {width}px"
  onclose={handleClose}
  oncancel={(e) => {
    e.preventDefault();
    handleClose();
  }}
>
  {#if open}
    <div class="inner">
      {@render children()}
    </div>
  {/if}
</dialog>

<style>
  dialog {
    padding: 0;
    border: 1px solid var(--border-strong);
    border-radius: 14px;
    background: var(--surface);
    color: var(--text);
    box-shadow: var(--shadow-lg);
    width: min(var(--w), calc(100vw - 24px));
    max-height: min(720px, calc(100vh - 48px));
    overflow: hidden;
  }

  dialog[open] {
    animation: pop var(--dur-slow) var(--ease-out);
  }

  dialog::backdrop {
    background: var(--overlay);
    backdrop-filter: blur(2px);
  }

  dialog[open]::backdrop {
    animation: fade var(--dur-slow) var(--ease);
  }

  .inner {
    display: flex;
    flex-direction: column;
    max-height: inherit;
    min-height: 0;
  }

  @keyframes pop {
    from {
      opacity: 0;
      transform: translateY(6px) scale(0.985);
    }
  }

  @keyframes fade {
    from {
      opacity: 0;
    }
  }

  @media (max-width: 759px) {
    dialog.sheet {
      width: 100vw;
      max-width: 100vw;
      height: 100dvh;
      max-height: 100dvh;
      margin: 0;
      border-radius: 0;
      border: none;
    }

    dialog.sheet .inner {
      height: 100%;
    }
  }
</style>
