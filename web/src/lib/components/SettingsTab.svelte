<script lang="ts">
  import { signOut } from '../app';
  import { formatBytes } from '../format';
  import { session } from '../session.svelte';
  import { settings, type Theme } from '../settings.svelte';
  import { sync } from '../sync.svelte';
  import { APP_VERSION } from '../version';
  import ConfirmDialog from './ConfirmDialog.svelte';
  import Icon, { type IconName } from './Icon.svelte';

  const themes: { id: Theme; label: string; icon: IconName }[] = [
    { id: 'system', label: 'System', icon: 'monitor' },
    { id: 'light', label: 'Light', icon: 'sun' },
    { id: 'dark', label: 'Dark', icon: 'moon' }
  ];

  let mb = $state(Math.round(settings.autoDownloadBytes / 1048576));
  let confirmOpen = $state(false);

  function setTheme(t: Theme) {
    settings.theme = t;
    settings.save();
    settings.applyTheme();
  }

  function commitMb() {
    const v = Number.isFinite(mb) ? Math.max(0, Math.min(10240, Math.round(mb))) : 50;
    mb = v;
    settings.autoDownloadBytes = v * 1048576;
    settings.save();
  }

  function toggleAutoCopy() {
    settings.autoCopy = !settings.autoCopy;
    settings.save();
  }
</script>

<div class="section">
  <h3>Appearance</h3>
  <div class="row">
    <div class="label">
      <span>Theme</span>
      <small>Follow the system or pick one.</small>
    </div>
    <div class="seg" role="radiogroup" aria-label="Theme">
      {#each themes as t (t.id)}
        <button role="radio" aria-checked={settings.theme === t.id} class:on={settings.theme === t.id} onclick={() => setTheme(t.id)}>
          <Icon name={t.icon} size={13} />
          {t.label}
        </button>
      {/each}
    </div>
  </div>
</div>

<div class="section">
  <h3>Sync</h3>
  <div class="row">
    <div class="label">
      <span id="autocopy-label">Copy incoming items automatically</span>
      <small>Puts new items from other devices on this clipboard while the page is visible.</small>
    </div>
    <button class="switch" role="switch" aria-checked={settings.autoCopy} aria-labelledby="autocopy-label" onclick={toggleAutoCopy}>
      <span class="knob"></span>
    </button>
  </div>
  <div class="row">
    <div class="label">
      <label for="auto-dl">Auto-download limit</label>
      <small>Larger items only load when you ask. Currently {formatBytes(settings.autoDownloadBytes)}.</small>
    </div>
    <div class="mb">
      <input id="auto-dl" class="input num" type="number" min="0" max="10240" step="1" bind:value={mb} onchange={commitMb} />
      <span>MB</span>
    </div>
  </div>
</div>

<div class="section">
  <h3>Account</h3>
  <dl>
    <div><dt>Username</dt><dd>{session.username ?? 'Unknown'}</dd></div>
    <div><dt>This device</dt><dd>{session.deviceName || 'Unnamed'}</dd></div>
    <div><dt>Server version</dt><dd class="num">{sync.serverVersion || session.me?.server_version || 'Unknown'}</dd></div>
    <div><dt>App version</dt><dd class="num">{APP_VERSION}</dd></div>
  </dl>
  <div class="signout">
    <div class="label">
      <span>Sign out</span>
      <small>Revokes this browser and clears its local cache and key.</small>
    </div>
    <button class="btn danger" onclick={() => (confirmOpen = true)}><Icon name="logout" size={14} /> Sign out</button>
  </div>
</div>

<ConfirmDialog
  bind:open={confirmOpen}
  title="Sign out of this browser?"
  message="The local history cache and the encryption key stored in this browser will be deleted. Your history stays on the server."
  confirmLabel="Sign out"
  onconfirm={() => signOut()}
/>

<style>
  .section + .section {
    margin-top: 22px;
    padding-top: 18px;
    border-top: 1px solid var(--border);
  }

  h3 {
    margin: 0 0 10px;
    font-size: var(--text-xs);
    font-weight: 600;
    color: var(--text-3);
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }

  .row,
  .signout {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
    padding: 8px 0;
  }

  .label {
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }

  .label span,
  .label label {
    font-weight: 550;
  }

  small {
    color: var(--text-3);
    font-size: var(--text-sm);
  }

  .seg {
    display: inline-flex;
    padding: 2px;
    border-radius: var(--radius);
    background: var(--surface-3);
    gap: 2px;
    flex: none;
  }

  .seg button {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    height: 26px;
    padding: 0 10px;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--text-2);
    font-size: var(--text-sm);
    font-weight: 500;
    transition:
      background var(--dur-fast) var(--ease),
      color var(--dur-fast) var(--ease);
  }

  .seg button.on {
    background: var(--surface);
    color: var(--text);
    box-shadow: var(--shadow-sm);
  }

  .switch {
    position: relative;
    width: 36px;
    height: 20px;
    flex: none;
    border-radius: 999px;
    border: none;
    background: var(--surface-3);
    box-shadow: inset 0 0 0 1px var(--border-strong);
    transition: background var(--dur) var(--ease);
  }

  .switch[aria-checked='true'] {
    background: var(--accent);
    box-shadow: none;
  }

  .knob {
    position: absolute;
    top: 2px;
    left: 2px;
    width: 16px;
    height: 16px;
    border-radius: 999px;
    background: #fff;
    box-shadow: 0 1px 2px rgba(0, 0, 0, 0.25);
    transition: transform var(--dur) var(--ease);
  }

  .switch[aria-checked='true'] .knob {
    transform: translateX(16px);
  }

  .mb {
    display: flex;
    align-items: center;
    gap: 6px;
    flex: none;
    color: var(--text-2);
    font-size: var(--text-sm);
  }

  .mb .input {
    width: 84px;
    height: 30px;
    text-align: right;
  }

  dl {
    margin: 0 0 8px;
  }

  dl > div {
    display: flex;
    justify-content: space-between;
    gap: 12px;
    padding: 7px 0;
    font-size: var(--text-md);
  }

  dl > div + div {
    border-top: 1px solid var(--border);
  }

  dt {
    color: var(--text-2);
  }

  dd {
    margin: 0;
    text-align: right;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .signout {
    margin-top: 6px;
    padding: 12px;
    border-radius: var(--radius);
    border: 1px solid var(--border);
    background: var(--surface-2);
  }
</style>
