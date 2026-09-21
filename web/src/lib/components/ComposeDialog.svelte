<script lang="ts">
  import { formatBytes } from '../format';
  import { modKey } from '../platform';
  import { utf8 } from '../protocol/encoding';
  import { uploads } from '../uploads.svelte';
  import Dialog from './Dialog.svelte';
  import Icon from './Icon.svelte';

  let { open = $bindable(false) }: { open?: boolean } = $props();

  let text = $state('');
  let area = $state<HTMLTextAreaElement | null>(null);
  let fileInput = $state<HTMLInputElement | null>(null);
  const bytes = $derived(utf8(text).length);

  $effect(() => {
    if (open && area) area.focus();
  });

  function send() {
    if (!text) return;
    const t = text;
    text = '';
    open = false;
    void uploads.sendText(t);
  }

  function onKey(e: KeyboardEvent) {
    if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) {
      e.preventDefault();
      send();
    }
  }

  function pickFiles(e: Event) {
    const input = e.currentTarget as HTMLInputElement;
    const files = input.files ? Array.from(input.files) : [];
    input.value = '';
    if (!files.length) return;
    open = false;
    void uploads.sendFiles(files);
  }
</script>

<Dialog bind:open label="New item" width={560}>
  <div class="head">
    <h2>New item</h2>
    <button class="icon-btn" aria-label="Close" onclick={() => (open = false)}><Icon name="x" size={16} /></button>
  </div>
  <div class="body">
    <label class="sr-only" for="compose-text">Text to send</label>
    <textarea
      id="compose-text"
      bind:this={area}
      bind:value={text}
      onkeydown={onKey}
      placeholder="Type or paste text to send to your devices"
      spellcheck="true"
    ></textarea>
  </div>
  <div class="foot">
    <button class="btn ghost" onclick={() => fileInput?.click()}><Icon name="upload" size={14} /> Send files</button>
    <input bind:this={fileInput} type="file" multiple class="sr-only" tabindex="-1" onchange={pickFiles} aria-hidden="true" />
    <span class="count num">{text ? formatBytes(bytes) : ''}</span>
    <button class="btn primary" onclick={send} disabled={!text}>
      <Icon name="send" size={14} /> Send <kbd class="on-primary">{modKey()}</kbd><kbd class="on-primary">↵</kbd>
    </button>
  </div>
</Dialog>

<style>
  .head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 12px 8px 18px;
  }

  h2 {
    margin: 0;
    font-size: var(--text-lg);
    font-weight: 600;
  }

  .body {
    padding: 0 18px;
  }

  textarea {
    width: 100%;
    min-height: 200px;
    max-height: 50vh;
    resize: vertical;
    padding: 12px;
    border-radius: var(--radius);
    border: 1px solid var(--border-strong);
    background: var(--surface-2);
    font-size: 13.5px;
    line-height: 1.55;
    transition:
      border-color var(--dur-fast) var(--ease),
      box-shadow var(--dur-fast) var(--ease);
  }

  textarea:focus {
    border-color: var(--accent);
    box-shadow: 0 0 0 3px var(--accent-soft-2);
    background: var(--surface);
  }

  .foot {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 12px 18px 16px;
  }

  .count {
    flex: 1;
    text-align: right;
    font-size: var(--text-sm);
    color: var(--text-3);
  }

  .on-primary {
    background: rgba(255, 255, 255, 0.18);
    border-color: transparent;
    color: #fff;
  }
</style>
