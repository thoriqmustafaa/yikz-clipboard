<script lang="ts">
  import { errorMessage } from '../api';
  import { formatBytes } from '../format';
  import { renderMarkdown } from '../markdown';
  import { downloadAsset, fetchReleases, latestDownloads, type PlatformDownload, type Release, type ReleasePlatform } from '../releases';
  import { toasts } from '../toast.svelte';
  import { ui } from '../ui.svelte';
  import { APP_VERSION } from '../version';
  import Icon, { type IconName } from './Icon.svelte';

  const platformIcons: Record<ReleasePlatform, IconName> = {
    macos: 'laptop',
    android: 'phone',
    'windows-x64': 'monitor',
    'windows-arm64': 'monitor'
  };

  let releases = $state<Release[] | null>(null);
  let error = $state<string | null>(null);
  let loading = $state(false);
  let busy = $state<Record<string, boolean>>({});

  const downloads = $derived(releases ? latestDownloads(releases) : []);

  $effect(() => {
    void ui.releaseRevision;
    const ctrl = new AbortController();
    void load(ctrl.signal);
    return () => ctrl.abort();
  });

  async function load(signal?: AbortSignal) {
    loading = true;
    error = null;
    try {
      releases = await fetchReleases(20, signal);
    } catch (err) {
      if (signal?.aborted) return;
      error = errorMessage(err);
    } finally {
      if (!signal?.aborted) loading = false;
    }
  }

  function formatDate(iso: string): string {
    const t = Date.parse(iso);
    if (Number.isNaN(t)) return '';
    return new Date(t).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
  }

  async function download(d: PlatformDownload) {
    if (busy[d.platform]) return;
    busy = { ...busy, [d.platform]: true };
    try {
      await downloadAsset(d.version, d.asset);
    } catch (err) {
      toasts.error(`Could not download ${d.label} ${d.version}`, errorMessage(err));
    } finally {
      busy = { ...busy, [d.platform]: false };
    }
  }
</script>

<div class="tab-head">
  <div>
    <h3>What's new</h3>
    <p>You are using version <span class="num">{APP_VERSION}</span> of the web app.</p>
  </div>
  <button class="icon-btn" onclick={() => load()} aria-label="Refresh releases" title="Refresh"><Icon name="refresh" size={15} class={loading ? 'spin' : ''} /></button>
</div>

{#if error}
  <div class="err"><Icon name="alert" size={14} /> {error}</div>
{:else if !releases}
  <div class="card"><div class="skeleton" style="height: 12px; width: 40%"></div><div class="skeleton" style="height: 8px; margin-top: 14px"></div></div>
{:else}
  <section aria-labelledby="downloads-title">
    <h4 class="label" id="downloads-title">Downloads</h4>
    {#if downloads.length === 0}
      <p class="empty">No app builds have been published yet.</p>
    {:else}
      <ul class="downloads">
        {#each downloads as d (d.platform)}
          <li class="dl">
            <span class="dl-ic"><Icon name={platformIcons[d.platform]} size={16} /></span>
            <div class="dl-body">
              <strong>{d.label}</strong>
              <span class="num">{d.version} · {formatBytes(d.asset.size)}</span>
            </div>
            <button class="btn" onclick={() => download(d)} disabled={busy[d.platform]} aria-label="Download {d.label} {d.version}">
              <Icon name={busy[d.platform] ? 'loader' : 'download'} size={14} class={busy[d.platform] ? 'spin' : ''} />
              {busy[d.platform] ? 'Downloading' : 'Download'}
            </button>
          </li>
        {/each}
      </ul>
    {/if}
  </section>

  <section aria-labelledby="changelog-title">
    <h4 class="label" id="changelog-title">Changelog</h4>
    {#if releases.length === 0}
      <p class="empty">No releases yet.</p>
    {:else}
      <ol class="releases">
        {#each releases as r (r.version)}
          <li class="release">
            <div class="release-head">
              <h5 class="num">{r.version}</h5>
              {#if r.version === APP_VERSION}<span class="badge">This version</span>{/if}
              {#if r.published_at}<time datetime={r.published_at}>{formatDate(r.published_at)}</time>{/if}
            </div>
            <div class="md">
              {@html renderMarkdown(r.notes_md)}
            </div>
          </li>
        {/each}
      </ol>
    {/if}
  </section>
{/if}

<style>
  .tab-head {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 14px;
  }

  h3 {
    margin: 0 0 3px;
    font-size: var(--text-lg);
    font-weight: 600;
  }

  p {
    margin: 0;
    color: var(--text-2);
    font-size: var(--text-sm);
  }

  section + section {
    margin-top: 22px;
    padding-top: 18px;
    border-top: 1px solid var(--border);
  }

  .label {
    margin: 0 0 10px;
    font-size: var(--text-xs);
    font-weight: 600;
    color: var(--text-3);
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }

  .empty {
    color: var(--text-3);
  }

  .card {
    padding: 14px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--surface-2);
  }

  .downloads {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(230px, 1fr));
    gap: 8px;
  }

  .dl {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 10px 10px 10px 12px;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--surface-2);
  }

  .dl-ic {
    display: grid;
    place-items: center;
    width: 30px;
    height: 30px;
    flex: none;
    border-radius: 8px;
    background: var(--accent-soft);
    color: var(--accent-text);
  }

  .dl-body {
    display: flex;
    flex-direction: column;
    gap: 1px;
    flex: 1;
    min-width: 0;
  }

  .dl-body strong {
    font-weight: 600;
  }

  .dl-body span {
    color: var(--text-3);
    font-size: var(--text-sm);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .dl .btn {
    flex: none;
  }

  .releases {
    list-style: none;
    margin: 0;
    padding: 0;
  }

  .release + .release {
    margin-top: 16px;
    padding-top: 14px;
    border-top: 1px dashed var(--border);
  }

  .release-head {
    display: flex;
    align-items: baseline;
    flex-wrap: wrap;
    gap: 8px;
    margin-bottom: 4px;
  }

  h5 {
    margin: 0;
    font-size: var(--text-md);
    font-weight: 650;
  }

  .badge {
    padding: 1px 7px;
    border-radius: 999px;
    background: var(--accent-soft);
    color: var(--accent-text);
    font-size: var(--text-xs);
    font-weight: 600;
  }

  time {
    margin-left: auto;
    color: var(--text-3);
    font-size: var(--text-sm);
  }

  .md {
    font-size: var(--text-md);
    color: var(--text);
    overflow-wrap: anywhere;
  }

  .md :global(h2),
  .md :global(h3),
  .md :global(h4),
  .md :global(h5),
  .md :global(h6) {
    margin: 12px 0 4px;
    font-size: var(--text-sm);
    font-weight: 600;
    color: var(--text-2);
  }

  .md :global(ul) {
    margin: 0;
    padding-left: 18px;
  }

  .md :global(li) {
    margin: 2px 0;
  }

  .md :global(li::marker) {
    color: var(--text-3);
  }

  .md :global(p) {
    margin: 6px 0;
    color: var(--text);
    font-size: var(--text-md);
  }

  .md :global(code) {
    padding: 1px 4px;
    border-radius: 4px;
    background: var(--surface-3);
    font-family: var(--font-mono);
    font-size: 0.92em;
  }

  .md :global(a) {
    color: var(--accent-text);
    text-decoration: underline;
    text-underline-offset: 2px;
  }

  .err {
    display: flex;
    align-items: center;
    gap: 6px;
    color: var(--danger);
    font-size: var(--text-sm);
  }
</style>
