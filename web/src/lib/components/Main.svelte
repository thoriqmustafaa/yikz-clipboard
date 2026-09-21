<script lang="ts">
  import { copyItem, deleteItem, saveItem, togglePin } from '../actions';
  import { type CachedItem, history, type TypeFilter } from '../history.svelte';
  import { filterItems, flatten, groupItems } from '../listing';
  import { modKey } from '../platform';
  import { sync } from '../sync.svelte';
  import { uploads } from '../uploads.svelte';
  import { formatBytes } from '../format';
  import ComposeDialog from './ComposeDialog.svelte';
  import ConfirmDialog from './ConfirmDialog.svelte';
  import Icon from './Icon.svelte';
  import ItemList from './ItemList.svelte';
  import Panel, { type PanelTab } from './Panel.svelte';
  import Preview from './Preview.svelte';
  import StatusPill from './StatusPill.svelte';
  import UploadTray from './UploadTray.svelte';

  const types: { id: TypeFilter; label: string }[] = [
    { id: 'all', label: 'All types' },
    { id: 'text', label: 'Text' },
    { id: 'links', label: 'Links' },
    { id: 'images', label: 'Images' },
    { id: 'files', label: 'Files' }
  ];

  let query = $state('');
  let type = $state<TypeFilter>('all');
  let selectedId = $state<string | null>(null);
  let composeOpen = $state(false);
  let panelTab = $state<PanelTab | null>(null);
  let confirmOpen = $state(false);
  let pendingDelete = $state<CachedItem | null>(null);
  let sheetOpen = $state(false);
  let narrow = $state(false);
  let dragDepth = $state(0);
  let filterEl = $state<HTMLInputElement | null>(null);
  let now = $state(Date.now());
  const mod = modKey();

  const filtering = $derived(query.trim().length > 0 || type !== 'all');
  const filtered = $derived(filterItems(history.items, query, type));
  const groups = $derived(groupItems(filtered, now));
  const ordered = $derived(flatten(groups));
  const selected = $derived(selectedId ? (history.items.find((i) => i.id === selectedId) ?? null) : null);
  const selectedVisible = $derived(!!selected && ordered.some((i) => i.id === selected.id));
  const dragging = $derived(dragDepth > 0);

  $effect(() => {
    const mq = window.matchMedia('(max-width: 759px)');
    narrow = mq.matches;
    const on = (e: MediaQueryListEvent) => (narrow = e.matches);
    mq.addEventListener('change', on);
    const t = setInterval(() => (now = Date.now()), 60000);
    return () => {
      mq.removeEventListener('change', on);
      clearInterval(t);
    };
  });

  $effect(() => {
    if (ordered.length === 0) {
      if (selectedId && !history.get(selectedId)) selectedId = null;
      return;
    }
    if (!selectedId || !ordered.some((i) => i.id === selectedId)) {
      if (!narrow || !sheetOpen) selectedId = ordered[0].id;
    }
  });

  $effect(() => {
    if (filtering && history.hasMore && !history.loadingAll && history.cacheLoaded && !history.syncing) {
      void history.loadAll();
    }
  });

  $effect(() => {
    if (sheetOpen && !selected) sheetOpen = false;
  });

  function select(item: CachedItem, openSheet = false) {
    selectedId = item.id;
    if (narrow && openSheet) sheetOpen = true;
  }

  function move(delta: number) {
    if (ordered.length === 0) return;
    const i = ordered.findIndex((x) => x.id === selectedId);
    const next = i < 0 ? 0 : Math.max(0, Math.min(ordered.length - 1, i + delta));
    selectedId = ordered[next].id;
    if (next >= ordered.length - 5 && history.hasMore) history.loadOlder().catch(() => undefined);
  }

  function activate(item: CachedItem | null) {
    if (!item) return;
    if (narrow && !sheetOpen) {
      select(item, true);
      return;
    }
    void copyItem(item);
  }

  function askDelete(item: CachedItem | null) {
    if (!item) return;
    pendingDelete = item;
    confirmOpen = true;
  }

  async function confirmDelete() {
    const item = pendingDelete;
    if (!item) return;
    const i = ordered.findIndex((x) => x.id === item.id);
    const neighbor = ordered[i + 1] ?? ordered[i - 1] ?? null;
    const ok = await deleteItem(item);
    if (ok && selectedId === item.id) selectedId = neighbor?.id ?? null;
  }

  function isEditable(t: EventTarget | null): boolean {
    const el = t as HTMLElement | null;
    if (!el || !el.tagName) return false;
    const tag = el.tagName;
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable;
  }

  function isButtonLike(t: EventTarget | null): boolean {
    const el = t as HTMLElement | null;
    if (!el || !el.tagName) return false;
    return el.tagName === 'BUTTON' || el.tagName === 'A' || el.tagName === 'SUMMARY';
  }

  function anyDialogOpen(): boolean {
    return !!document.querySelector('dialog[open]');
  }

  function onKeydown(e: KeyboardEvent) {
    const modDown = e.metaKey || e.ctrlKey;
    if (anyDialogOpen()) return;
    const inFilter = e.target === filterEl;
    const editable = isEditable(e.target);
    const key = e.key.toLowerCase();

    if (modDown && key === 'f') {
      e.preventDefault();
      filterEl?.focus();
      filterEl?.select();
      return;
    }
    if (modDown && key === 'p') {
      e.preventDefault();
      if (selected) void togglePin(selected);
      return;
    }
    if (modDown && key === 's') {
      e.preventDefault();
      if (selected) void saveItem(selected);
      return;
    }
    if (modDown && key === 'n' && e.shiftKey) {
      e.preventDefault();
      composeOpen = true;
      return;
    }
    if (editable && !inFilter) return;

    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        move(1);
        return;
      case 'ArrowUp':
        e.preventDefault();
        move(-1);
        return;
      case 'PageDown':
        e.preventDefault();
        move(10);
        return;
      case 'PageUp':
        e.preventDefault();
        move(-10);
        return;
      case 'Home':
        if (inFilter) return;
        e.preventDefault();
        if (ordered[0]) selectedId = ordered[0].id;
        return;
      case 'End':
        if (inFilter) return;
        e.preventDefault();
        if (ordered.length) selectedId = ordered[ordered.length - 1].id;
        return;
      case 'Enter':
        if (e.isComposing || isButtonLike(e.target)) return;
        e.preventDefault();
        activate(selected);
        return;
      case 'Escape':
        if (sheetOpen) {
          sheetOpen = false;
          return;
        }
        if (inFilter) {
          if (query) query = '';
          else filterEl?.blur();
        }
        return;
      case 'Delete':
      case 'Backspace':
        if (inFilter) return;
        e.preventDefault();
        askDelete(selected);
        return;
    }
  }

  function onPaste(e: ClipboardEvent) {
    if (isEditable(e.target) || anyDialogOpen()) return;
    const dt = e.clipboardData;
    if (!dt) return;
    const files = Array.from(dt.files ?? []);
    if (files.length > 0) {
      e.preventDefault();
      if (files.length === 1 && files[0].type.startsWith('image/')) void uploads.sendImage(files[0]);
      else void uploads.sendFiles(files);
      return;
    }
    const text = dt.getData('text/plain');
    if (text) {
      e.preventDefault();
      void uploads.sendText(text);
    }
  }

  function hasFiles(e: DragEvent): boolean {
    return !!e.dataTransfer && Array.from(e.dataTransfer.types).includes('Files');
  }

  function onDragEnter(e: DragEvent) {
    if (!hasFiles(e) || anyDialogOpen()) return;
    e.preventDefault();
    dragDepth++;
  }

  function onDragOver(e: DragEvent) {
    if (!hasFiles(e)) return;
    e.preventDefault();
    if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
  }

  function onDragLeave(e: DragEvent) {
    if (!hasFiles(e)) return;
    dragDepth = Math.max(0, dragDepth - 1);
  }

  function onDrop(e: DragEvent) {
    if (!hasFiles(e)) return;
    e.preventDefault();
    dragDepth = 0;
    const dt = e.dataTransfer;
    if (!dt) return;
    const files: File[] = [];
    const items = Array.from(dt.items ?? []);
    if (items.length) {
      for (const it of items) {
        if (it.kind !== 'file') continue;
        const entry = typeof it.webkitGetAsEntry === 'function' ? it.webkitGetAsEntry() : null;
        if (entry && entry.isDirectory) continue;
        const f = it.getAsFile();
        if (f) files.push(f);
      }
    } else {
      files.push(...Array.from(dt.files));
    }
    if (files.length === 0) return;
    void uploads.sendFiles(files);
  }

  const warning = $derived(sync.storageWarning);
</script>

<svelte:window onkeydown={onKeydown} ondragenter={onDragEnter} ondragover={onDragOver} ondragleave={onDragLeave} ondrop={onDrop} />
<svelte:document onpaste={onPaste} />

<div class="app" class:narrow>
  <header class="top">
    <div class="brand" aria-hidden="true"><Icon name="clipboard" size={15} /></div>
    <div class="search">
      <Icon name="search" size={15} />
      <input
        bind:this={filterEl}
        bind:value={query}
        type="search"
        placeholder="Search clipboard history"
        aria-label="Search clipboard history"
        autocomplete="off"
        spellcheck="false"
      />
      {#if query}
        <button class="clear" aria-label="Clear search" onclick={() => (query = '')}><Icon name="x" size={13} /></button>
      {:else}
        <span class="hint" aria-hidden="true"><kbd>{mod}</kbd><kbd>F</kbd></span>
      {/if}
    </div>
    <div class="type">
      <select bind:value={type} aria-label="Filter by type">
        {#each types as t (t.id)}
          <option value={t.id}>{t.label}</option>
        {/each}
      </select>
      <Icon name="chevron-down" size={13} />
    </div>
    <div class="spacer"></div>
    <StatusPill onactivity={() => (panelTab = 'activity')} />
    <div class="tools">
      <button class="icon-btn" onclick={() => (composeOpen = true)} aria-label="New item" title="New item"><Icon name="plus" size={17} /></button>
      <button class="icon-btn hide-narrow" onclick={() => (panelTab = 'devices')} aria-label="Devices" title="Devices"><Icon name="devices" size={16} /></button>
      <button class="icon-btn hide-narrow" onclick={() => (panelTab = 'storage')} aria-label="Storage" title="Storage"><Icon name="storage" size={16} /></button>
      <button class="icon-btn" onclick={() => (panelTab = 'settings')} aria-label="Settings" title="Settings"><Icon name="settings" size={16} /></button>
    </div>
  </header>

  {#if warning}
    <div class="banner" role="status">
      <Icon name="alert" size={14} />
      <span>Server disk is low ({formatBytes(warning.free_disk_bytes)} free). Uploads over 1 MB are paused.</span>
    </div>
  {/if}

  <div class="content">
    <section class="list-pane" aria-label="History">
      <ItemList
        {groups}
        {selectedId}
        {filtering}
        onselect={(it) => select(it, true)}
        onactivate={(it) => void copyItem(it)}
        oncompose={() => (composeOpen = true)}
      />
    </section>
    <section class="preview-pane" class:open={sheetOpen} aria-label="Preview" aria-hidden={narrow && !sheetOpen}>
      <Preview
        item={selected && (selectedVisible || sheetOpen) ? selected : null}
        showBack={narrow}
        onback={() => (sheetOpen = false)}
        oncopy={() => selected && copyItem(selected)}
        onsave={() => selected && saveItem(selected)}
        onpin={() => selected && togglePin(selected)}
        ondelete={() => askDelete(selected)}
      />
    </section>
  </div>

  {#if dragging}
    <div class="drop" aria-hidden="true">
      <div class="drop-card">
        <div class="drop-ic"><Icon name="upload" size={22} /></div>
        <strong>Drop to send</strong>
        <span>Files are encrypted before they leave this browser</span>
      </div>
    </div>
  {/if}
</div>

<UploadTray />
<ComposeDialog bind:open={composeOpen} />
<Panel bind:tab={panelTab} />
<ConfirmDialog
  bind:open={confirmOpen}
  title="Delete this item?"
  message="It will be removed from history on every device. This cannot be undone."
  onconfirm={confirmDelete}
/>

<style>
  .app {
    position: relative;
    height: 100%;
    display: flex;
    flex-direction: column;
    background: var(--surface);
  }

  .top {
    flex: none;
    display: flex;
    align-items: center;
    gap: 8px;
    height: 52px;
    padding: 0 10px 0 12px;
    border-bottom: 1px solid var(--border);
    background: var(--surface);
  }

  .brand {
    width: 26px;
    height: 26px;
    border-radius: 7px;
    display: grid;
    place-items: center;
    color: #fff;
    background: linear-gradient(160deg, #7474e8, #5151cf);
    flex: none;
  }

  .search {
    position: relative;
    display: flex;
    align-items: center;
    flex: 1;
    max-width: 440px;
    min-width: 0;
    height: 32px;
    padding: 0 8px 0 10px;
    gap: 8px;
    border-radius: var(--radius);
    background: var(--surface-2);
    border: 1px solid transparent;
    color: var(--text-3);
    transition:
      border-color var(--dur-fast) var(--ease),
      background var(--dur-fast) var(--ease),
      box-shadow var(--dur-fast) var(--ease);
  }

  .search:focus-within {
    background: var(--surface);
    border-color: var(--accent);
    box-shadow: 0 0 0 3px var(--accent-soft-2);
  }

  .search input {
    flex: 1;
    min-width: 0;
    height: 100%;
    border: none;
    background: transparent;
    font-size: var(--text-md);
    color: var(--text);
  }

  .search input:focus-visible {
    box-shadow: none;
  }

  .search input::-webkit-search-cancel-button {
    display: none;
  }

  .hint {
    display: inline-flex;
    gap: 2px;
  }

  .clear {
    display: grid;
    place-items: center;
    width: 20px;
    height: 20px;
    border: none;
    border-radius: 999px;
    background: var(--surface-3);
    color: var(--text-2);
  }

  .type {
    position: relative;
    display: flex;
    align-items: center;
    color: var(--text-2);
    flex: none;
  }

  .type select {
    appearance: none;
    height: 32px;
    padding: 0 28px 0 10px;
    border-radius: var(--radius);
    border: 1px solid var(--border);
    background: var(--surface);
    font-size: var(--text-md);
    color: var(--text);
    cursor: pointer;
  }

  .type select:hover {
    border-color: var(--border-strong);
  }

  .type select:focus-visible {
    box-shadow: var(--focus-ring);
  }

  .type :global(.icon) {
    position: absolute;
    right: 9px;
    pointer-events: none;
  }

  .spacer {
    flex: 1;
  }

  .tools {
    display: flex;
    align-items: center;
    gap: 2px;
  }

  .banner {
    flex: none;
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 7px 14px;
    font-size: var(--text-sm);
    background: var(--warning-soft);
    color: var(--warning);
    border-bottom: 1px solid var(--border);
  }

  .content {
    flex: 1;
    min-height: 0;
    display: flex;
    position: relative;
  }

  .list-pane {
    width: clamp(300px, 38%, 440px);
    flex: none;
    border-right: 1px solid var(--border);
    min-height: 0;
  }

  .preview-pane {
    flex: 1;
    min-width: 0;
    min-height: 0;
    background: var(--surface);
  }

  .drop {
    position: absolute;
    inset: 8px;
    z-index: 70;
    display: grid;
    place-items: center;
    border-radius: 14px;
    border: 2px dashed var(--accent);
    background: color-mix(in srgb, var(--accent) 8%, transparent);
    backdrop-filter: blur(3px);
    pointer-events: none;
    animation: fade var(--dur) var(--ease);
  }

  .drop-card {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 6px;
    padding: 22px 28px;
    border-radius: 14px;
    background: var(--surface);
    box-shadow: var(--shadow-lg);
    text-align: center;
  }

  .drop-card span {
    color: var(--text-2);
    font-size: var(--text-sm);
  }

  .drop-ic {
    width: 44px;
    height: 44px;
    border-radius: 12px;
    display: grid;
    place-items: center;
    background: var(--accent-soft);
    color: var(--accent-text);
    margin-bottom: 4px;
  }

  @keyframes fade {
    from {
      opacity: 0;
    }
  }

  @media (max-width: 759px) {
    .top {
      gap: 6px;
      padding: 0 8px;
    }

    .brand,
    .hint,
    .hide-narrow {
      display: none;
    }

    .type select {
      width: 44px;
      padding: 0;
      color: transparent;
      text-indent: -999px;
    }

    .type select option {
      color: var(--text);
    }

    .type :global(.icon) {
      right: 15px;
    }

    .list-pane {
      width: 100%;
      border-right: none;
    }

    .preview-pane {
      position: absolute;
      inset: 0;
      z-index: 20;
      transform: translateX(100%);
      visibility: hidden;
      transition:
        transform var(--dur-slow) var(--ease-out),
        visibility 0s linear var(--dur-slow);
      box-shadow: var(--shadow-lg);
    }

    .preview-pane.open {
      transform: none;
      visibility: visible;
      transition:
        transform var(--dur-slow) var(--ease-out),
        visibility 0s;
    }
  }
</style>
