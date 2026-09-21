<script lang="ts">
  import Dialog from './Dialog.svelte';
  import Icon from './Icon.svelte';

  let {
    open = $bindable(false),
    title,
    message,
    confirmLabel = 'Delete',
    danger = true,
    onconfirm
  }: {
    open?: boolean;
    title: string;
    message: string;
    confirmLabel?: string;
    danger?: boolean;
    onconfirm: () => void | Promise<void>;
  } = $props();

  let busy = $state(false);
  let confirmBtn = $state<HTMLButtonElement | null>(null);

  $effect(() => {
    if (open && confirmBtn) confirmBtn.focus();
  });

  async function confirm() {
    busy = true;
    try {
      await onconfirm();
      open = false;
    } finally {
      busy = false;
    }
  }
</script>

<Dialog bind:open label={title} width={380}>
  <div class="box">
    <div class="ic" class:danger><Icon name={danger ? 'trash' : 'info'} size={17} /></div>
    <h2>{title}</h2>
    <p>{message}</p>
    <div class="row">
      <button class="btn" onclick={() => (open = false)} disabled={busy}>Cancel</button>
      <button class="btn {danger ? 'danger' : 'primary'}" bind:this={confirmBtn} onclick={confirm} disabled={busy}>
        {#if busy}<Icon name="loader" size={14} class="spin" />{/if}
        {confirmLabel}
      </button>
    </div>
  </div>
</Dialog>

<style>
  .box {
    padding: 20px;
  }

  .ic {
    width: 34px;
    height: 34px;
    border-radius: 10px;
    display: grid;
    place-items: center;
    background: var(--accent-soft);
    color: var(--accent-text);
    margin-bottom: 12px;
  }

  .ic.danger {
    background: var(--danger-soft);
    color: var(--danger);
  }

  h2 {
    margin: 0 0 6px;
    font-size: var(--text-lg);
    font-weight: 600;
  }

  p {
    margin: 0 0 18px;
    color: var(--text-2);
    font-size: var(--text-md);
    line-height: 1.5;
  }

  .row {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
  }
</style>
