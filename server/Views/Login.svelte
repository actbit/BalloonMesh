<script lang="ts">
  import type { LoginPage } from '@sveltesharp/models';

  let { model }: { model: LoginPage } = $props();
</script>

<svelte:head>
  <title>{model.title}</title>
</svelte:head>

<div class="login-shell">
  <div class="aurora"></div>
  <main class="login-card">
    <div class="brand">
      <span class="brand-mark"></span>
      <div>
        <h1>Balloon Mesh</h1>
        <p>ESP32-C3 RGB balloon fleet control</p>
      </div>
    </div>

    <h2>{model.title}</h2>
    <p class="lead">
      Sign in to view the live 2D map, positioning state and LED schedule.
      Actions are authorized by role (Viewer / Operator / Admin).
    </p>

    {#if model.error}
      <div class="error">{model.error}</div>
    {/if}

    <div class="providers">
      {#if model.localEnabled}
        <form class="local-login" method="post" action="/auth/login/local">
          <label>
            <span>Username</span>
            <input name="username" autocomplete="username" required />
          </label>
          <label>
            <span>Password</span>
            <input name="password" type="password" autocomplete="current-password" required />
          </label>
          <button type="submit">Sign in</button>
        </form>
      {/if}
      {#if model.oidcEnabled}
        <a class="provider primary" href="/auth/login/oidc">
          <span class="provider-icon">◎</span>
          <span><strong>OIDC</strong><small>Company / Keycloak / Entra ID</small></span>
        </a>
      {/if}
      {#if model.discordEnabled}
        <a class="provider discord" href="/auth/login/discord">
          <span class="provider-icon">◈</span>
          <span><strong>Discord</strong><small>Discord OAuth2</small></span>
        </a>
      {/if}
      {#if model.mockEnabled}
        <a class="provider mock" href="/auth/login/mock">
          <span class="provider-icon">◇</span>
          <span><strong>Mock operator</strong><small>Development only · Admin</small></span>
        </a>
      {/if}
    </div>

    {#if !model.oidcEnabled && !model.discordEnabled && !model.mockEnabled}
      <p class="empty">No login provider is configured. Set OIDC or Discord secrets, or enable the mock provider in Development.</p>
    {/if}

    <p class="footer">Gateway devices authenticate separately with their device token.</p>
  </main>
</div>

<style>
  :global(html), :global(body) {
    margin: 0;
    height: 100%;
    background: #050b14;
    color: #e2e8f0;
    font-family: "Inter", "Segoe UI", system-ui, sans-serif;
  }
  :global(*) { box-sizing: border-box; }

  .login-shell {
    min-height: 100vh;
    display: grid;
    place-items: center;
    position: relative;
    overflow: hidden;
    padding: 24px;
  }

  .aurora {
    position: absolute;
    inset: -20%;
    background:
      radial-gradient(circle at 20% 20%, rgba(56, 189, 248, 0.22), transparent 35%),
      radial-gradient(circle at 80% 30%, rgba(167, 139, 250, 0.18), transparent 32%),
      radial-gradient(circle at 50% 85%, rgba(244, 114, 182, 0.14), transparent 40%);
    filter: blur(20px);
  }

  .login-card {
    position: relative;
    width: min(460px, 100%);
    padding: 32px;
    border-radius: 24px;
    background: rgba(7, 17, 30, 0.88);
    border: 1px solid rgba(148, 163, 184, 0.18);
    box-shadow: 0 30px 70px rgba(0, 0, 0, 0.45);
    backdrop-filter: blur(18px);
  }

  .brand { display: flex; align-items: center; gap: 12px; margin-bottom: 26px; }
  .brand-mark {
    width: 38px; height: 38px; border-radius: 50%;
    background: conic-gradient(from 210deg, #38bdf8, #a78bfa, #f472b6, #38bdf8);
    box-shadow: 0 0 26px rgba(56, 189, 248, 0.55);
  }
  .brand h1 { margin: 0; font-size: 21px; letter-spacing: 0.02em; }
  .brand p { margin: 2px 0 0; font-size: 11px; color: #94a3b8; }

  h2 { margin: 0 0 8px; font-size: 24px; }
  .lead { margin: 0 0 20px; font-size: 13px; line-height: 1.6; color: #94a3b8; }

  .error {
    margin-bottom: 16px; padding: 10px 12px; border-radius: 10px;
    background: rgba(127, 29, 29, 0.65); color: #fecaca; font-size: 12px;
  }

  .providers { display: flex; flex-direction: column; gap: 10px; }
  .local-login {
    display: flex; flex-direction: column; gap: 10px;
    padding: 16px; border-radius: 14px;
    border: 1px solid rgba(56, 189, 248, 0.4);
    background: rgba(15, 23, 42, 0.7);
  }
  .local-login label { display: flex; flex-direction: column; gap: 5px; }
  .local-login label span { font-size: 11px; color: #94a3b8; }
  .local-login input {
    width: 100%; padding: 10px 11px; border-radius: 9px;
    border: 1px solid rgba(148, 163, 184, 0.25);
    background: rgba(2, 6, 23, 0.8); color: #e2e8f0;
  }
  .local-login button {
    margin-top: 4px; padding: 11px; border: 0; border-radius: 9px;
    background: #38bdf8; color: #082f49; font-weight: 700; cursor: pointer;
  }
  .provider {
    display: flex; align-items: center; gap: 14px;
    padding: 14px 16px; border-radius: 14px;
    border: 1px solid rgba(148, 163, 184, 0.22);
    background: rgba(15, 23, 42, 0.7);
    color: #e2e8f0; text-decoration: none;
    transition: border-color 0.15s ease, background 0.15s ease, transform 0.15s ease;
  }
  .provider:hover { border-color: rgba(56, 189, 248, 0.55); background: rgba(56, 189, 248, 0.12); transform: translateY(-1px); }
  .provider.primary { border-color: rgba(56, 189, 248, 0.4); }
  .provider.discord { border-color: rgba(129, 140, 248, 0.35); }
  .provider.mock { border-color: rgba(251, 191, 36, 0.35); }
  .provider-icon {
    width: 34px; height: 34px; border-radius: 10px;
    display: grid; place-items: center; font-size: 18px;
    background: rgba(56, 189, 248, 0.12); color: #7dd3fc;
  }
  .provider.discord .provider-icon { background: rgba(129, 140, 248, 0.14); color: #c7d2fe; }
  .provider.mock .provider-icon { background: rgba(251, 191, 36, 0.14); color: #fde68a; }
  .provider strong { display: block; font-size: 14px; }
  .provider small { display: block; margin-top: 2px; font-size: 11px; color: #94a3b8; }

  .empty { margin-top: 16px; font-size: 12px; color: #fbbf24; }
  .footer { margin: 24px 0 0; font-size: 10px; color: #64748b; }
</style>
