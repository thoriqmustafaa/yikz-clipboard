<script lang="ts">
  import { ApiError, NetworkError } from '../api';
  import { signOut } from '../app';
  import { defaultDeviceName } from '../platform';
  import { validateDeviceName } from '../protocol/messages';
  import { session, UnsupportedServerError, WrongPasswordError } from '../session.svelte';
  import Icon from './Icon.svelte';

  let username = $state(session.username ?? '');
  let password = $state('');
  let encPassword = $state('');
  let confirmPassword = $state('');
  let deviceName = $state(session.deviceName || defaultDeviceName());
  let showPw = $state(false);
  let showEnc = $state(false);
  let busy = $state(false);
  let formError = $state<string | null>(null);
  let encError = $state<string | null>(null);
  let confirmError = $state<string | null>(null);
  let deviceError = $state<string | null>(null);
  let progress = $state(0);
  let encInput = $state<HTMLInputElement | null>(null);
  let confirmInput = $state<HTMLInputElement | null>(null);

  const mode = $derived(session.phase === 'login' ? 'login' : session.unlockMode);
  const carried = $derived(mode === 'setup' && !!session.pendingSetupPassword);

  $effect(() => {
    if (!session.deriving) {
      progress = 0;
      return;
    }
    const start = performance.now();
    let raf = 0;
    const tick = () => {
      const t = performance.now() - start;
      progress = Math.min(0.95, 1 - Math.exp(-t / 1600));
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  });

  $effect(() => {
    if (mode === 'setup' && carried) confirmInput?.focus();
    else if (mode !== 'login') encInput?.focus();
  });

  function describe(err: unknown): string {
    if (err instanceof UnsupportedServerError) return err.message;
    if (err instanceof NetworkError) return 'Cannot reach the server. Check your connection and try again.';
    if (err instanceof ApiError) {
      if (err.code === 'invalid_credentials') return 'Wrong username or password.';
      if (err.code === 'rate_limited') {
        const mins = err.retryAfter ? Math.max(1, Math.ceil(err.retryAfter / 60)) : null;
        return mins ? `Too many failed attempts. Try again in ${mins} min.` : 'Too many failed attempts. Try again later.';
      }
      if (err.code === 'invalid_request') return err.message || 'The server rejected the request.';
      if (err.status >= 500) return 'The server had a problem. Try again in a moment.';
      return err.message;
    }
    return err instanceof Error ? err.message : String(err);
  }

  async function submitLogin(e: SubmitEvent) {
    e.preventDefault();
    formError = null;
    encError = null;
    deviceError = validateDeviceName(deviceName);
    if (!username.trim() || !password) {
      formError = 'Enter your username and password.';
      return;
    }
    if (!encPassword) {
      encError = 'Enter your encryption password.';
      return;
    }
    if (deviceError) return;
    busy = true;
    try {
      await session.login({ username, password, encryptionPassword: encPassword, deviceName: deviceName.trim() });
      password = '';
    } catch (err) {
      if (err instanceof WrongPasswordError) {
        encError = 'This encryption password does not match the one used by your other devices.';
        encPassword = '';
      } else {
        formError = describe(err);
      }
    } finally {
      busy = false;
    }
  }

  async function submitUnlock(e: SubmitEvent) {
    e.preventDefault();
    formError = null;
    encError = null;
    confirmError = null;
    let pw = encPassword;
    if (mode === 'setup') {
      if (carried) pw = session.pendingSetupPassword ?? '';
      if (!pw) {
        encError = 'Choose an encryption password.';
        return;
      }
      if (pw.length < 8) {
        encError = 'Use at least 8 characters.';
        return;
      }
      if (confirmPassword !== pw) {
        confirmError = 'The passwords do not match.';
        return;
      }
    } else if (!pw) {
      encError = 'Enter your encryption password.';
      return;
    }
    busy = true;
    try {
      await session.unlock(pw);
      encPassword = '';
      confirmPassword = '';
    } catch (err) {
      if (err instanceof WrongPasswordError) {
        encError = 'Wrong encryption password. It must match the one used on your other devices.';
        encPassword = '';
        encInput?.focus();
      } else {
        formError = describe(err);
      }
    } finally {
      busy = false;
    }
  }

  function startOver() {
    session.pendingSetupPassword = null;
    confirmPassword = '';
    encPassword = '';
  }
</script>

<div class="wrap">
  <main class="card" aria-labelledby="auth-title">
    <div class="brand">
      <div class="mark"><Icon name="clipboard" size={20} /></div>
    </div>

    {#if mode === 'login'}
      <h1 id="auth-title">Sign in to yikz clipboard</h1>
      <p class="sub">Your clipboard history, end-to-end encrypted across your devices.</p>
      {#if session.notice}
        <div class="notice" role="status"><Icon name="info" size={15} /><span>{session.notice}</span></div>
      {/if}
      <form onsubmit={submitLogin} novalidate>
        <label class="field">
          <span>Username</span>
          <input class="input" autocomplete="username" autocapitalize="off" spellcheck="false" bind:value={username} disabled={busy} />
        </label>
        <label class="field">
          <span>Password</span>
          <div class="pw">
            <input
              class="input"
              type={showPw ? 'text' : 'password'}
              autocomplete="current-password"
              bind:value={password}
              disabled={busy}
            />
            <button type="button" class="icon-btn reveal" onclick={() => (showPw = !showPw)} aria-label={showPw ? 'Hide password' : 'Show password'}>
              <Icon name={showPw ? 'eye-off' : 'eye'} size={15} />
            </button>
          </div>
        </label>
        <div class="divider"><span>Encryption</span></div>
        <label class="field">
          <span>Encryption password</span>
          <div class="pw">
            <input
              class="input"
              type={showEnc ? 'text' : 'password'}
              autocomplete="off"
              bind:value={encPassword}
              bind:this={encInput}
              disabled={busy}
              aria-invalid={encError ? 'true' : undefined}
              aria-describedby="enc-hint"
            />
            <button type="button" class="icon-btn reveal" onclick={() => (showEnc = !showEnc)} aria-label={showEnc ? 'Hide encryption password' : 'Show encryption password'}>
              <Icon name={showEnc ? 'eye-off' : 'eye'} size={15} />
            </button>
          </div>
          {#if encError}
            <small class="err" id="enc-hint">{encError}</small>
          {:else}
            <small class="hint" id="enc-hint">Never sent to the server. Use the same one on every device.</small>
          {/if}
        </label>
        <label class="field">
          <span>Device name</span>
          <input class="input" bind:value={deviceName} maxlength="64" disabled={busy} aria-invalid={deviceError ? 'true' : undefined} />
          {#if deviceError}<small class="err">{deviceError}</small>{/if}
        </label>
        {#if formError}
          <div class="form-error" role="alert"><Icon name="alert" size={15} /><span>{formError}</span></div>
        {/if}
        {@render derivingBar()}
        <button class="btn primary lg submit" type="submit" disabled={busy}>
          {#if busy}<Icon name="loader" size={15} class="spin" />{/if}
          {session.deriving ? 'Deriving key' : busy ? 'Signing in' : 'Sign in'}
        </button>
      </form>
    {:else}
      {#if mode === 'setup'}
        <h1 id="auth-title">Set your encryption password</h1>
        <p class="sub">
          This is the first device on the account. The encryption password protects everything you sync and cannot be recovered, so
          keep it somewhere safe.
        </p>
      {:else}
        <h1 id="auth-title">Unlock your history</h1>
        <p class="sub">
          Signed in as <strong>{session.username ?? 'you'}</strong>. Enter your encryption password to decrypt your clipboard.
        </p>
      {/if}
      {#if session.notice}
        <div class="notice" role="status"><Icon name="info" size={15} /><span>{session.notice}</span></div>
      {/if}
      <form onsubmit={submitUnlock} novalidate>
        {#if !carried}
          <label class="field">
            <span>Encryption password</span>
            <div class="pw">
              <input
                class="input"
                type={showEnc ? 'text' : 'password'}
                autocomplete="off"
                bind:value={encPassword}
                bind:this={encInput}
                disabled={busy}
                aria-invalid={encError ? 'true' : undefined}
              />
              <button type="button" class="icon-btn reveal" onclick={() => (showEnc = !showEnc)} aria-label={showEnc ? 'Hide encryption password' : 'Show encryption password'}>
                <Icon name={showEnc ? 'eye-off' : 'eye'} size={15} />
              </button>
            </div>
            {#if encError}<small class="err">{encError}</small>{/if}
          </label>
        {/if}
        {#if mode === 'setup'}
          <label class="field">
            <span>Confirm encryption password</span>
            <input
              class="input"
              type={showEnc ? 'text' : 'password'}
              autocomplete="off"
              bind:value={confirmPassword}
              bind:this={confirmInput}
              disabled={busy}
              aria-invalid={confirmError || (carried && encError) ? 'true' : undefined}
            />
            {#if confirmError}
              <small class="err">{confirmError}</small>
            {:else if carried && encError}
              <small class="err">{encError}</small>
            {:else if carried}
              <small class="hint">Type the encryption password you entered on the previous step again.</small>
            {/if}
          </label>
        {/if}
        {#if formError}
          <div class="form-error" role="alert"><Icon name="alert" size={15} /><span>{formError}</span></div>
        {/if}
        {@render derivingBar()}
        <button class="btn primary lg submit" type="submit" disabled={busy}>
          {#if busy}<Icon name="loader" size={15} class="spin" />{/if}
          {session.deriving ? 'Deriving key' : mode === 'setup' ? 'Set password and continue' : 'Unlock'}
        </button>
        <div class="links">
          {#if carried}
            <button type="button" class="link" onclick={startOver} disabled={busy}>Choose a different password</button>
          {/if}
          <button type="button" class="link" onclick={() => signOut()} disabled={busy}>Sign out</button>
        </div>
      </form>
    {/if}
  </main>
  <p class="foot">Encryption keys are derived on this device and never leave it.</p>
</div>

{#snippet derivingBar()}
  {#if session.deriving}
    <div class="derive" role="status" aria-live="polite">
      <div class="derive-top">
        <span>Deriving encryption key</span>
        <span class="num">{Math.round(progress * 100)}%</span>
      </div>
      <div class="bar"><div class="fill" style="transform: scaleX({progress})"></div></div>
      <small>Runs 600,000 PBKDF2 rounds once. This device remembers the key afterwards.</small>
    </div>
  {/if}
{/snippet}

<style>
  .wrap {
    min-height: 100%;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    padding: 32px 16px;
    overflow: auto;
    height: 100%;
    background:
      radial-gradient(1200px 600px at 50% -10%, var(--accent-soft), transparent 60%),
      var(--bg);
  }

  .card {
    width: 100%;
    max-width: 392px;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 14px;
    box-shadow: var(--shadow-lg);
    padding: 28px 28px 24px;
  }

  .brand {
    display: flex;
    margin-bottom: 18px;
  }

  .mark {
    width: 38px;
    height: 38px;
    border-radius: 10px;
    display: grid;
    place-items: center;
    color: #fff;
    background: linear-gradient(160deg, #7474e8, #5151cf);
    box-shadow:
      0 6px 16px -6px rgba(91, 91, 214, 0.7),
      inset 0 1px 0 rgba(255, 255, 255, 0.2);
  }

  h1 {
    font-size: 19px;
    line-height: 1.3;
    font-weight: 650;
    letter-spacing: -0.01em;
    margin: 0 0 6px;
  }

  .sub {
    margin: 0 0 20px;
    color: var(--text-2);
    font-size: var(--text-md);
  }

  form {
    display: flex;
    flex-direction: column;
    gap: 14px;
  }

  .field {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }

  .field > span {
    font-size: var(--text-sm);
    font-weight: 550;
    color: var(--text-2);
  }

  .pw {
    position: relative;
  }

  .pw .input {
    padding-right: 38px;
  }

  .reveal {
    position: absolute;
    right: 3px;
    top: 2px;
    width: 30px;
    height: 30px;
  }

  small {
    font-size: var(--text-sm);
  }

  .hint {
    color: var(--text-3);
  }

  .err {
    color: var(--danger);
  }

  .divider {
    display: flex;
    align-items: center;
    gap: 10px;
    margin: 4px 0 -2px;
    color: var(--text-3);
    font-size: var(--text-xs);
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }

  .divider::before,
  .divider::after {
    content: '';
    flex: 1;
    height: 1px;
    background: var(--border);
  }

  .notice,
  .form-error {
    display: flex;
    gap: 8px;
    align-items: flex-start;
    padding: 9px 11px;
    border-radius: var(--radius);
    font-size: var(--text-sm);
    line-height: 1.45;
  }

  .notice {
    background: var(--accent-soft);
    color: var(--accent-text);
    margin-bottom: 16px;
  }

  .form-error {
    background: var(--danger-soft);
    color: var(--danger);
  }

  .notice :global(.icon),
  .form-error :global(.icon) {
    margin-top: 1px;
  }

  .submit {
    width: 100%;
    margin-top: 4px;
  }

  .derive {
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 10px 12px;
    border-radius: var(--radius);
    background: var(--surface-2);
    border: 1px solid var(--border);
  }

  .derive-top {
    display: flex;
    justify-content: space-between;
    font-size: var(--text-sm);
    font-weight: 550;
  }

  .derive small {
    color: var(--text-3);
    font-size: var(--text-xs);
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
    transition: transform var(--dur) linear;
  }

  .links {
    display: flex;
    justify-content: center;
    gap: 16px;
  }

  .link {
    background: none;
    border: none;
    padding: 2px 4px;
    color: var(--text-2);
    font-size: var(--text-sm);
  }

  .link:hover:not(:disabled) {
    color: var(--text);
    text-decoration: underline;
    text-underline-offset: 2px;
  }

  .foot {
    margin: 18px 0 0;
    color: var(--text-3);
    font-size: var(--text-sm);
    display: flex;
    align-items: center;
    gap: 6px;
  }

  @media (max-width: 440px) {
    .card {
      padding: 22px 18px 18px;
    }
  }
</style>
