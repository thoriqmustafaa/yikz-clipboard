<script lang="ts">
  import { untrack } from 'svelte';
  import { canCopy, kindLabel, saveOneFile } from '../actions';
  import { content, describeContentError } from '../content.svelte';
  import { formatBytes, formatDateTime, formatFull, pluralize } from '../format';
  import type { CachedItem } from '../history.svelte';
  import { iconFor, platformIcon, titleFor } from '../itemview';
  import { modKey } from '../platform';
  import { session } from '../session.svelte';
  import { settings } from '../settings.svelte';
  import { sync } from '../sync.svelte';
  import { cachedThumb, loadThumb } from '../thumbs';
  import Icon from './Icon.svelte';

  let {
    item,
    onback,
    oncopy,
    onsave,
    onpin,
    ondelete,
    showBack = false
  }: {
    item: CachedItem | null;
    onback: () => void;
    oncopy: () => void;
    onsave: () => void;
    onpin: () => void;
    ondelete: () => void;
    showBack?: boolean;
  } = $props();

  let text = $state<string | null>(null);
  let imageUrl = $state<string | null>(null);
  let thumbUrl = $state<string | null>(null);
  let requested = $state(false);

  const cstate = $derived(item ? content.state(item.id) : null);
  const tooLarge = $derived(!!item && item.size > settings.autoDownloadBytes);
  const mod = modKey();

  const itemKey = $derived(item ? `${item.id}:${item.m ? 'm' : 'x'}` : '');

  $effect(() => {
    void itemKey;
    const it = untrack(() => item);
    text = null;
    imageUrl = null;
    thumbUrl = null;
    requested = false;
    if (!it || it.err || !it.m) return;
    if (it.kind === 'image') {
      thumbUrl = cachedThumb(it.id);
      if (!thumbUrl) loadThumb(it).then((u) => {
        if (item?.id === it.id) thumbUrl = u;
      });
    }
    if (it.kind === 'files') return;
    if (it.size <= untrack(() => settings.autoDownloadBytes) || content.has(it.id)) void fetchContent(it);
  });

  async function fetchContent(it: CachedItem) {
    requested = true;
    try {
      if (it.kind === 'text') {
        const t = await content.text(it);
        if (item?.id === it.id) text = t;
      } else if (it.kind === 'image') {
        const u = await content.imageUrl(it);
        if (item?.id === it.id) imageUrl = u;
      }
    } catch {
      return;
    }
  }

  function segments(s: string): { text: string; href?: string }[] {
    const out: { text: string; href?: string }[] = [];
    const re = /\bhttps?:\/\/[^\s<>"'`]+[^\s<>"'`.,;:!?)\]}]/gi;
    let last = 0;
    let m: RegExpExecArray | null;
    while ((m = re.exec(s))) {
      if (m.index > last) out.push({ text: s.slice(last, m.index) });
      out.push({ text: m[0], href: m[0] });
      last = m.index + m[0].length;
    }
    if (last < s.length) out.push({ text: s.slice(last) });
    return out;
  }

  const shownText = $derived(item?.kind === 'text' ? (text ?? item.m?.preview ?? '') : '');
  const truncated = $derived(
    !!item && item.kind === 'text' && text === null && (tooLarge || (cstate?.status === 'loading' && item.chunk_count > 0))
  );
  const shownSegments = $derived(segments(shownText.length > 200000 ? shownText.slice(0, 200000) : shownText));
  const clipped = $derived(shownText.length > 200000);
  const sourceDevice = $derived(item ? sync.device(item.device_id) : undefined);
  const lineCount = $derived(text ? text.split('\n').length : null);
</script>

{#if !item}
  <div class="none">
    <div class="none-ic"><Icon name="clipboard" size={22} /></div>
    <p>Select an item to preview it</p>
    <div class="hints">
      <span><kbd>↑</kbd><kbd>↓</kbd> Navigate</span>
      <span><kbd>↵</kbd> Copy</span>
      <span><kbd>{mod}</kbd><kbd>F</kbd> Search</span>
    </div>
  </div>
{:else}
  <div class="pane">
    <header class="head">
      {#if showBack}
        <button class="icon-btn" onclick={onback} aria-label="Back to list"><Icon name="chevron-left" size={18} /></button>
      {/if}
      <span class="kind-ic"><Icon name={iconFor(item)} size={15} /></span>
      <h2 class="head-title" title={titleFor(item)}>{titleFor(item)}</h2>
      {#if item.kind === 'text' && !item.err}
        <button
          class="icon-btn"
          class:active={settings.monospace}
          aria-pressed={settings.monospace}
          aria-label="Monospace font"
          title="Monospace font"
          onclick={() => {
            settings.monospace = !settings.monospace;
            settings.save();
          }}><Icon name="code" size={15} /></button
        >
      {/if}
    </header>

    <div class="body scroll">
      {#if item.err}
        <div class="notice">
          <Icon name="alert" size={16} />
          <div>
            <strong>{item.err === 'unsupported' ? 'Unsupported item' : 'This item cannot be decrypted'}</strong>
            <p>
              {item.err === 'unsupported'
                ? 'It was created by a newer version of the app. Update this app to open it.'
                : 'It may be corrupted, or it was encrypted with a different key.'}
            </p>
          </div>
        </div>
      {:else if item.kind === 'text'}
        {#if cstate?.status === 'error'}
          <div class="inline-err"><Icon name="alert" size={14} /> {cstate.error}</div>
        {/if}
        <div class="text" class:mono={settings.monospace} class:link-only={!!item.link}>
          {#each shownSegments as seg, i (i)}{#if seg.href}<a href={seg.href} target="_blank" rel="noopener noreferrer nofollow">{seg.text}</a>{:else}{seg.text}{/if}{/each}{#if clipped}<span class="clip-note">
              ...</span
            >{/if}
        </div>
        {#if truncated}
          <div class="load-more">
            {#if cstate?.status === 'loading'}
              {@render progress(cstate.loaded, cstate.total)}
            {:else}
              <span>Showing a preview. The full text is {formatBytes(item.size)}.</span>
              <button class="btn sm" onclick={() => item && fetchContent(item)}>Load full text</button>
            {/if}
          </div>
        {/if}
      {:else if item.kind === 'image'}
        <div class="image-wrap">
          {#if imageUrl}
            <img class="image" src={imageUrl} alt={titleFor(item)} />
          {:else if thumbUrl}
            <img class="image soft" src={thumbUrl} alt={titleFor(item)} />
          {:else}
            <div class="image-ph skeleton" style="aspect-ratio: {item.m?.image ? `${item.m.image.width} / ${item.m.image.height}` : '4 / 3'}"></div>
          {/if}
          {#if !imageUrl}
            <div class="image-status">
              {#if cstate?.status === 'loading'}
                {@render progress(cstate.loaded, cstate.total)}
              {:else if cstate?.status === 'error'}
                <span class="inline-err"><Icon name="alert" size={14} /> {cstate.error}</span>
                <button class="btn sm" onclick={() => item && fetchContent(item)}>Retry</button>
              {:else if tooLarge && !requested}
                <span>Full image is {formatBytes(item.size)}, above your auto-download limit.</span>
                <button class="btn sm" onclick={() => item && fetchContent(item)}>Load full image</button>
              {/if}
            </div>
          {/if}
        </div>
      {:else}
        <ul class="files" aria-label="Files">
          {#each item.m?.files ?? [] as f, i (i)}
            <li>
              <span class="file-ic"><Icon name="file" size={15} /></span>
              <span class="file-name" title={f.name}>{f.name}</span>
              <span class="file-size num">{formatBytes(f.size)}</span>
              <button class="icon-btn sm" aria-label="Download {f.name}" title="Download" onclick={() => item && saveOneFile(item, i)}>
                <Icon name="download" size={14} />
              </button>
            </li>
          {/each}
        </ul>
        {#if cstate?.status === 'loading'}
          <div class="load-more">{@render progress(cstate.loaded, cstate.total)}</div>
        {:else if cstate?.status === 'error'}
          <div class="inline-err"><Icon name="alert" size={14} /> {cstate.error ?? describeContentError(null)}</div>
        {/if}
      {/if}

      <section class="info" aria-label="Information">
        <h3>Information</h3>
        <dl>
          <div>
            <dt>Source</dt>
            <dd>
              <span class="src">
                <Icon name={platformIcon(sourceDevice?.platform)} size={13} />
                {sync.deviceName(item.device_id)}{#if item.device_id === session.deviceId}<span class="badge">This browser</span>{/if}
              </span>
            </dd>
          </div>
          {#if item.m?.source_app}
            <div><dt>Application</dt><dd>{item.m.source_app}</dd></div>
          {/if}
          <div><dt>Type</dt><dd>{kindLabel(item)}</dd></div>
          <div><dt>Size</dt><dd class="num">{formatBytes(item.size)}</dd></div>
          {#if item.kind === 'image' && item.m?.image}
            <div><dt>Dimensions</dt><dd class="num">{item.m.image.width} × {item.m.image.height}</dd></div>
          {/if}
          {#if item.kind === 'text' && text !== null}
            <div><dt>Characters</dt><dd class="num">{Array.from(text).length.toLocaleString()}</dd></div>
            {#if lineCount && lineCount > 1}<div><dt>Lines</dt><dd class="num">{lineCount.toLocaleString()}</dd></div>{/if}
          {/if}
          {#if item.kind === 'files'}
            <div><dt>Files</dt><dd class="num">{pluralize(item.m?.files?.length ?? 0, 'file')}</dd></div>
          {/if}
          <div><dt>Created</dt><dd class="num" title={formatFull(item.createdMs)}>{formatDateTime(item.createdMs)}</dd></div>
          <div><dt>Pinned</dt><dd>{item.pinned ? 'Yes' : 'No'}</dd></div>
        </dl>
      </section>
    </div>

    <footer class="actions">
      <button class="btn primary" onclick={item.kind === 'files' ? onsave : oncopy} disabled={!!item.err || (item.kind !== 'files' && !canCopy(item))}>
        <Icon name={item.kind === 'files' ? 'download' : 'copy'} size={14} />
        {item.kind === 'files' ? 'Download' : 'Copy'}
        <kbd class="on-primary">↵</kbd>
      </button>
      <div class="spacer"></div>
      {#if item.kind !== 'files'}
        <button class="btn ghost" onclick={onsave} disabled={!!item.err} title="Save as file ({mod} S)">
          <Icon name="download" size={14} /><span class="lbl">Save</span>
        </button>
      {/if}
      <button class="btn ghost" onclick={onpin} title="{item.pinned ? 'Unpin' : 'Pin'} ({mod} P)" aria-pressed={item.pinned}>
        <Icon name={item.pinned ? 'pin-off' : 'pin'} size={14} /><span class="lbl">{item.pinned ? 'Unpin' : 'Pin'}</span>
      </button>
      <button class="btn ghost danger-text" onclick={ondelete} title="Delete (Delete key)">
        <Icon name="trash" size={14} /><span class="lbl">Delete</span>
      </button>
    </footer>
  </div>
{/if}

{#snippet progress(loaded: number, total: number)}
  <div class="progress" role="progressbar" aria-valuemin="0" aria-valuemax={total} aria-valuenow={loaded}>
    <div class="progress-top">
      <span>Downloading and decrypting</span>
      <span class="num">{formatBytes(loaded)} / {formatBytes(total)}</span>
    </div>
    <div class="bar"><div class="fill" style="transform: scaleX({total ? loaded / total : 0})"></div></div>
  </div>
{/snippet}

<style>
  .pane {
    height: 100%;
    display: flex;
    flex-direction: column;
    min-height: 0;
  }

  .head {
    display: flex;
    align-items: center;
    gap: 8px;
    height: 44px;
    padding: 0 10px 0 16px;
    border-bottom: 1px solid var(--border);
    flex: none;
  }

  .kind-ic {
    display: grid;
    place-items: center;
    width: 24px;
    height: 24px;
    border-radius: 6px;
    background: var(--surface-3);
    color: var(--text-2);
    flex: none;
  }

  .head-title {
    flex: 1;
    min-width: 0;
    margin: 0;
    font-size: var(--text-md);
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .body {
    flex: 1;
    min-height: 0;
    padding: 16px 20px 20px;
  }

  .text {
    white-space: pre-wrap;
    word-break: break-word;
    overflow-wrap: anywhere;
    font-size: 13.5px;
    line-height: 1.6;
    color: var(--text);
    user-select: text;
    tab-size: 4;
  }

  .text.mono {
    font-family: var(--font-mono);
    font-size: 12.5px;
    line-height: 1.65;
  }

  .text.link-only {
    font-size: var(--text-lg);
  }

  .clip-note {
    color: var(--text-3);
  }

  .load-more {
    display: flex;
    align-items: center;
    gap: 10px;
    flex-wrap: wrap;
    margin-top: 14px;
    padding: 10px 12px;
    border-radius: var(--radius);
    background: var(--surface-2);
    border: 1px solid var(--border);
    font-size: var(--text-sm);
    color: var(--text-2);
  }

  .load-more > span {
    flex: 1;
    min-width: 160px;
  }

  .inline-err {
    display: flex;
    align-items: center;
    gap: 6px;
    color: var(--danger);
    font-size: var(--text-sm);
    margin-bottom: 10px;
  }

  .notice {
    display: flex;
    gap: 10px;
    padding: 12px 14px;
    border-radius: var(--radius);
    background: var(--danger-soft);
    color: var(--danger);
  }

  .notice p {
    margin: 4px 0 0;
    color: var(--text-2);
    font-size: var(--text-sm);
  }

  .image-wrap {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 12px;
  }

  .image {
    max-width: 100%;
    max-height: min(58vh, 560px);
    object-fit: contain;
    border-radius: var(--radius);
    background:
      repeating-conic-gradient(var(--surface-3) 0% 25%, var(--surface-2) 0% 50%) 50% / 16px 16px;
    box-shadow: 0 0 0 1px var(--border);
  }

  .image.soft {
    filter: blur(0.5px);
    opacity: 0.85;
  }

  .image-ph {
    width: min(100%, 420px);
    border-radius: var(--radius);
  }

  .image-status {
    display: flex;
    align-items: center;
    gap: 10px;
    flex-wrap: wrap;
    justify-content: center;
    font-size: var(--text-sm);
    color: var(--text-2);
    width: 100%;
  }

  .files {
    list-style: none;
    margin: 0;
    padding: 0;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    overflow: hidden;
  }

  .files li {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 6px 6px 6px 12px;
    min-height: 40px;
  }

  .files li + li {
    border-top: 1px solid var(--border);
  }

  .file-ic {
    color: var(--text-3);
    display: flex;
  }

  .file-name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .file-size {
    color: var(--text-3);
    font-size: var(--text-sm);
  }

  .icon-btn.sm {
    width: 28px;
    height: 28px;
  }

  .info {
    margin-top: 22px;
    padding-top: 14px;
    border-top: 1px solid var(--border);
  }

  .info h3 {
    margin: 0 0 8px;
    font-size: var(--text-xs);
    font-weight: 600;
    color: var(--text-3);
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }

  dl {
    margin: 0;
  }

  dl > div {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 16px;
    padding: 7px 0;
    font-size: var(--text-sm);
  }

  dl > div + div {
    border-top: 1px solid var(--border);
  }

  dt {
    color: var(--text-2);
  }

  dd {
    margin: 0;
    color: var(--text);
    text-align: right;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .src {
    display: inline-flex;
    align-items: center;
    gap: 6px;
  }

  .src :global(.icon) {
    color: var(--text-3);
  }

  .actions {
    flex: none;
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 8px 10px;
    border-top: 1px solid var(--border);
    background: var(--surface);
  }

  .spacer {
    flex: 1;
  }

  .on-primary {
    background: rgba(255, 255, 255, 0.18);
    border-color: transparent;
    color: #fff;
    margin-left: 2px;
  }

  .danger-text:hover:not(:disabled) {
    color: var(--danger);
    background: var(--danger-soft);
  }

  .progress {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 6px;
    width: 100%;
    max-width: 420px;
  }

  .progress-top {
    display: flex;
    justify-content: space-between;
    font-size: var(--text-sm);
    color: var(--text-2);
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

  .none {
    height: 100%;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 10px;
    color: var(--text-3);
    padding: 24px;
  }

  .none p {
    margin: 0;
    font-size: var(--text-md);
  }

  .none-ic {
    width: 44px;
    height: 44px;
    border-radius: 12px;
    display: grid;
    place-items: center;
    background: var(--surface-2);
    border: 1px solid var(--border);
  }

  .hints {
    display: flex;
    gap: 16px;
    flex-wrap: wrap;
    justify-content: center;
    font-size: var(--text-sm);
    margin-top: 6px;
  }

  .hints span {
    display: inline-flex;
    align-items: center;
    gap: 3px;
  }

  @media (max-width: 560px) {
    .lbl {
      display: none;
    }
  }
</style>
