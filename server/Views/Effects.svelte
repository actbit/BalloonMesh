<script lang="ts">
  import { onMount } from 'svelte';
  import type { HomePage as EffectsPage } from '@sveltesharp/models';
  import {
    applyPreset,
    defaultParams,
    effectPresets,
    formatAddress,
    hexToRgb,
    presetById,
    renderEffectColors,
    rgbCss,
    rgbToHex
  } from './lib/effect-model.svelte.ts';
  import { FleetMap } from './lib/fleet-map.svelte.ts';

  type EffectKind = 'flow' | 'wave' | 'pulse' | 'rainbow' | 'twinkle' | 'alternate' | 'solid';
  type EffectParams = {
    kind: EffectKind;
    origin: number | null;
    speed: number;
    wavelength: number;
    width: number;
    directionDeg: number;
    r: number;
    g: number;
    b: number;
    r2: number;
    g2: number;
    b2: number;
    durationSeconds: number;
    frameIntervalMs: number;
  };
  type BalloonPoint = {
    meshAddress: number;
    name: string;
    lat: number | null;
    lon: number | null;
    online?: boolean;
  };
  type MapBalloon = BalloonPoint & { r: number; g: number; b: number };
  type EffectColor = { meshAddress: number; r: number; g: number; b: number };
  type ShadowNode = EffectColor & { known: boolean; queueCount: number; rejectedCount: number };
  type ShadowSnapshot = { instanceId: string; revision: number; nodes: ShadowNode[] };
  type EffectState = {
    kind: string;
    label: string;
    running: boolean;
    originAddress: string | null;
    originName: string | null;
    speed: number;
    wavelength: number;
    width: number;
    directionDeg: number;
    r: number;
    g: number;
    b: number;
    r2: number;
    g2: number;
    b2: number;
    frameIntervalMs: number;
    durationSeconds: number;
    startedAt: string;
    endsAt: string | null;
    stopAt: string | null;
    nextKind: string | null;
    nextAt: string | null;
    scheduleWarning: string | null;
  };

  type Balloon = MapBalloon & {
    groupName: string;
    confidence: number | null;
    lastMethod: string | null;
    queueCount: number;
    estimatedQueueCount: number;
  };
  type Anchor = {
    anchorId: number;
    name: string;
    lat: number;
    lon: number;
    timeValid: boolean;
  };
  type Gateway = {
    id: string;
    webSocket: boolean;
    provisioned: boolean;
    pendingCommands: number;
  };

  let { model }: { model: EffectsPage } = $props();

  let balloons = $state<Balloon[]>([]);
  let anchors = $state<Anchor[]>([]);
  let gateways = $state<Gateway[]>([]);
  let selectedOrigin = $state<number | null>(null);
  let params = $state<EffectParams>(defaultParams());
  let effect = $state<EffectState | null>(null);
  let frameColors = $state<Record<number, { r: number; g: number; b: number }>>({});
  let shadowNodes = $state<Record<number, ShadowNode>>({});
  let clock = $state(Date.now());
  let statusLine = $state('Connecting…');
  let logLines = $state<string[]>([]);
  let mapError = $state('');
  let busy = $state(false);
  let displayMode = $state('preview');

  let fleetMap: FleetMap | null = null;
  let hub: any = null;
  let stateLoaded = false;
  let previewTimer = 0;
  let clockTimer = 0;
  let previewSeconds = 0;
  let effectStartedMs = 0;
  let shadowInstanceId = '';
  let shadowRevision = -1;
  const oldShadowInstances = new Set<string>();

  const colourPresets = ['#38bdf8', '#f472b6', '#facc15', '#22c55e', '#ef4444', '#a78bfa', '#ffffff'];

  const preset = $derived(presetById(params.kind));
  const originBalloon = $derived(
    balloons.find((balloon) => balloon.meshAddress === selectedOrigin) ?? null
  );
  const primaryHex = $derived(rgbToHex(params.r, params.g, params.b));
  const secondaryHex = $derived(rgbToHex(params.r2, params.g2, params.b2));
  const onlineCount = $derived(balloons.filter((balloon) => balloon.online !== false).length);
  const positionedCount = $derived(
    balloons.filter((balloon) => balloon.lat != null && balloon.lon != null).length
  );
  const remainingLabel = $derived.by(() => {
    if (!effect) {
      return '未実行';
    }
    if (!effect.running) {
      return '停止中';
    }
    if (effect.stopAt) {
      return `あと約${Math.max(0, Math.ceil((Date.parse(effect.stopAt) - clock) / 1000))}秒`;
    }
    if (effect.nextAt) {
      return `変更まで約${Math.max(0, Math.ceil((Date.parse(effect.nextAt) - clock) / 1000))}秒`;
    }
    if (clock < Date.parse(effect.startedAt)) {
      return `開始まで約${Math.ceil((Date.parse(effect.startedAt) - clock) / 1000)}秒`;
    }
    if (effect.durationSeconds <= 0 || !effect.endsAt) {
      return '連続';
    }
    const seconds = Math.max(0, Math.round((Date.parse(effect.endsAt) - clock) / 1000));
    return `${seconds}s`;
  });
  const progress = $derived.by(() => {
    if (!effect?.running) {
      return 0;
    }
    if (clock < Date.parse(effect.startedAt)) {
      return Math.max(0, Math.min(100, (1 - (Date.parse(effect.startedAt) - clock) / 30000) * 100));
    }
    if (!effect || effect.durationSeconds <= 0 || !effect.endsAt) {
      return effect?.running ? 100 : 0;
    }
    const total = Date.parse(effect.endsAt) - Date.parse(effect.startedAt);
    if (total <= 0) {
      return 100;
    }
    return Math.max(0, Math.min(100, ((clock - Date.parse(effect.startedAt)) / total) * 100));
  });

  function addLog(message: string) {
    const now = new Date().toLocaleTimeString();
    logLines = [`${now}  ${message}`, ...logLines].slice(0, 7);
    statusLine = message;
  }

  function eventValue(event: Event): string {
    return (event.currentTarget as HTMLInputElement | HTMLSelectElement).value;
  }

  function eventNumber(event: Event): number {
    return Number(eventValue(event));
  }

  async function api(path: string, options: RequestInit = {}) {
    const response = await fetch(path, {
      ...options,
      headers: { 'Content-Type': 'application/json', ...(options.headers ?? {}) }
    });
    if (response.status === 204) {
      return null;
    }
    const text = await response.text();
    const body = text ? JSON.parse(text) : null;
    if (!response.ok) {
      throw new Error(body?.error ?? response.statusText);
    }
    return body;
  }

  function applyState(state: any) {
    const first = !stateLoaded;
    balloons = state.balloons ?? [];
    anchors = state.anchors ?? [];
    gateways = state.gateways ?? [];
    if (state.ledShadow) applyShadow(state.ledShadow);
    stateLoaded = true;
    if (state.effect) {
      effect = state.effect;
      effectStartedMs = Date.parse(state.effect.startedAt);
    } else {
      effect = null;
    }
    if (first && selectedOrigin == null && balloons.length > 0) {
      selectedOrigin = balloons[0].meshAddress;
    }
    fleetMap?.setBalloons(balloons, selectedOrigin);
    if (first) {
      fleetMap?.fit();
    }
  }

  function upsertBalloon(balloon: Balloon) {
    const index = balloons.findIndex((item) => item.meshAddress === balloon.meshAddress);
    if (index >= 0) {
      const copy = [...balloons];
      copy[index] = balloon;
      balloons = copy;
    } else {
      balloons = [...balloons, balloon];
    }
    fleetMap?.setBalloons(balloons, selectedOrigin);
  }

  async function fetchState() {
    try {
      const state = await api('/api/state');
      applyState(state);
    } catch (error) {
      addLog(`状態取得に失敗: ${(error as Error).message}`);
    }
  }

  function choosePreset(kind: EffectKind) {
    params = applyPreset(params, kind);
    addLog(`${presetById(kind).label} プリセットを選択`);
  }

  function selectOrigin(value: string) {
    const address = Number(value);
    selectedOrigin = Number.isFinite(address) && value !== '' ? address : null;
    fleetMap?.setSelected(selectedOrigin);
  }

  function setPrimary(hex: string) {
    const { r, g, b } = hexToRgb(hex);
    params.r = r;
    params.g = g;
    params.b = b;
  }

  function setSecondary(hex: string) {
    const { r, g, b } = hexToRgb(hex);
    params.r2 = r;
    params.g2 = g;
    params.b2 = b;
  }

  async function startEffect() {
    if (!model.canOperate || busy) {
      return;
    }
    busy = true;
    try {
      const state = await api('/api/effects/start', {
        method: 'POST',
        body: JSON.stringify({
          kind: params.kind,
          origin: originBalloon ? String(originBalloon.meshAddress) : null,
          speed: params.speed,
          wavelength: params.wavelength,
          width: params.width,
          directionDeg: params.directionDeg,
          r: params.r,
          g: params.g,
          b: params.b,
          r2: params.r2,
          g2: params.g2,
          b2: params.b2,
          durationSeconds: params.durationSeconds,
          frameIntervalMs: params.frameIntervalMs,
          clearOnStop: true
        })
      });
      effect = state as EffectState;
      effectStartedMs = Date.parse(effect.startedAt);
      addLog(effect.nextKind ? '予約済みの周が終わってから設定を変更します' : '約30秒後の開始に向けて命令を準備します');
    } catch (error) {
      addLog(`エフェクト開始に失敗: ${(error as Error).message}`);
    } finally {
      busy = false;
    }
  }

  async function stopEffect() {
    if (busy) {
      return;
    }
    busy = true;
    try {
      const state = await api('/api/effects/stop', {
        method: 'POST',
        body: JSON.stringify({ atCycleEnd: true })
      });
      effect = state ?? (effect ? { ...effect, running: false } : null);
      addLog('予約済みの周が終わったら送信を終了します');
    } catch (error) {
      addLog(`停止に失敗: ${(error as Error).message}`);
    } finally {
      busy = false;
    }
  }

  function colorOf(balloon: Balloon): { r: number; g: number; b: number } {
    if (displayMode === 'shadow') {
      const node = shadowNodes[balloon.meshAddress];
      return node?.known ? node : { r: 0, g: 0, b: 0 };
    }
    return frameColors[balloon.meshAddress] ?? balloon;
  }

  function applyShadow(snapshot: ShadowSnapshot) {
    if (!snapshot || oldShadowInstances.has(snapshot.instanceId)) return;
    if (shadowInstanceId !== snapshot.instanceId) {
      if (shadowInstanceId) oldShadowInstances.add(shadowInstanceId);
      shadowInstanceId = snapshot.instanceId;
      shadowRevision = -1;
    }
    if (snapshot.revision < shadowRevision) return;
    shadowRevision = snapshot.revision;
    const next: Record<number, ShadowNode> = {};
    for (const state of snapshot.nodes) next[state.meshAddress] = state;
    shadowNodes = next;
    if (displayMode === 'shadow') paintShadow();
  }

  function paintShadow() {
    fleetMap?.setEffectColors(balloons.map((balloon) => {
      const node = shadowNodes[balloon.meshAddress];
      return node?.known ? node : { meshAddress: balloon.meshAddress, r: 0, g: 0, b: 0 };
    }));
  }

  function selectDisplay(event: Event) {
    displayMode = eventValue(event);
    if (displayMode === 'shadow') paintShadow();
    else previewFrame();
  }

  function nodeColorCss(balloon: Balloon): string {
    const color = colorOf(balloon);
    return Math.max(color.r, color.g, color.b) >= 8 ? rgbCss(color) : '#64748b';
  }

  function previewFrame() {
    previewSeconds += 0.09;
    const elapsed = effect?.running
      ? Math.max(0, (Date.now() - effectStartedMs) / 1000)
      : previewSeconds;
    const colors: EffectColor[] = renderEffectColors(
      balloons as BalloonPoint[],
      params,
      elapsed,
      originBalloon as BalloonPoint | null
    );
    const next: Record<number, { r: number; g: number; b: number }> = {};
    for (const color of colors) {
      next[color.meshAddress] = { r: color.r, g: color.g, b: color.b };
    }
    frameColors = next;
    if (displayMode === 'preview') {
      fleetMap?.setEffectColors(colors);
    }
  }

  async function loadScript(src: string): Promise<void> {
    if (document.querySelector(`script[src="${src}"]`)) {
      return;
    }
    await new Promise<void>((resolve, reject) => {
      const script = document.createElement('script');
      script.src = src;
      script.async = true;
      script.onload = () => resolve();
      script.onerror = () => reject(new Error(`Failed to load ${src}`));
      document.head.appendChild(script);
    });
  }

  async function initSignalR() {
    await loadScript('https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.7/dist/browser/signalr.min.js');
    const signalR = (window as any).signalR;
    if (!signalR) {
      throw new Error('SignalR did not initialise.');
    }

    hub = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/monitor')
      .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
      .build();
    hub.on('state', applyState);
    hub.on('balloonUpdated', upsertBalloon);
    hub.on('gatewaysUpdated', (value: Gateway[]) => {
      gateways = value;
    });
    hub.on('ledShadowChanged', applyShadow);
    hub.on('effectChanged', (value: EffectState | null) => {
      effect = value;
      if (value?.running) {
        effectStartedMs = Date.parse(value.startedAt);
        addLog(value.scheduleWarning ?? (value.stopAt ? '予約済みの周が終わったら送信を終了します' : value.nextKind ? '予約済みの周が終わったら設定を変更します' : `${value.label} の予約を開始`));
      } else if (value) {
        previewSeconds = 0;
        addLog(`${value.label} を停止`);
      } else {
        previewSeconds = 0;
      }
    });
    hub.on('commandSent', (result: any) => {
      if (result?.message) {
        addLog(result.message);
      }
    });
    hub.onreconnecting(() => {
      statusLine = 'Reconnecting…';
    });
    hub.onreconnected(() => {
      statusLine = 'Live';
    });
    hub.onclose(() => {
      statusLine = 'Polling mode';
    });

    try {
      await hub.start();
      statusLine = 'Live';
    } catch (error) {
      statusLine = 'Polling mode';
      addLog(`SignalR 利用不可: ${(error as Error).message}`);
    }
  }

  onMount(() => {
    previewSeconds = 0;
    clockTimer = window.setInterval(() => {
      clock = Date.now();
    }, 1000);
    void (async () => {
      await fetchState();
      try {
        fleetMap = new FleetMap();
        await fleetMap.init('map', (address) => {
          selectedOrigin = address;
          fleetMap?.setSelected(address);
        });
        fleetMap.setBalloons(balloons, selectedOrigin);
        fleetMap.fit();
      } catch (error) {
        mapError = (error as Error).message;
        addLog(`地図を利用できません: ${mapError}`);
      }
      previewTimer = window.setInterval(previewFrame, 90);
      try {
        await initSignalR();
      } catch (error) {
        addLog(`SignalR 初期化に失敗: ${(error as Error).message}`);
      }
    })();

    const poll = window.setInterval(fetchState, 15_000);
    return () => {
      window.clearInterval(clockTimer);
      window.clearInterval(previewTimer);
      window.clearInterval(poll);
      if (hub) {
        void hub.stop();
      }
      if (fleetMap) {
        fleetMap.dispose();
      }
    };
  });
</script>

<svelte:head>
  <title>{model.title} — Effects</title>
</svelte:head>

<div class="app-shell">
  <div id="map" class="map"></div>

  <header class="topbar">
    <div class="brand">
      <span class="brand-mark"></span>
      <div>
        <h1>{model.title}</h1>
        <p>抽象エフェクト・コンポーザー</p>
      </div>
    </div>
    <nav class="nav">
      <a class="nav-item active" href="/">エフェクト</a>
      <a class="nav-item" href="/control">詳細設定</a>
    </nav>
    <div class="stats">
      {@render stat(balloons.length, 'nodes')}
      {@render stat(onlineCount, 'online')}
      {@render stat(gateways.length, 'gateways')}
      {@render stat(anchors.length, 'anchors')}
      {@render stat(statusLine === 'Live' ? 'LIVE' : 'POLL', statusLine,
        statusLine === 'Live' ? 'good' : 'bad', true)}
    </div>
    <div class="user-chip">
      {#if model.userAvatar}
        <img src={model.userAvatar} alt="" />
      {:else}
        <span class="user-avatar">{model.userName.slice(0, 1).toUpperCase()}</span>
      {/if}
      <div class="user-meta">
        <strong>{model.userName}</strong>
        <small>{model.userProvider} · {model.canAdmin ? 'Admin' : model.canOperate ? 'Operator' : 'Viewer'}</small>
      </div>
      <form method="post" action="/auth/logout">
        <button class="logout" type="submit">Logout</button>
      </form>
    </div>
  </header>

  <main class="workspace">
    <section class="panel composer">
      <div class="panel-head">
        <div>
          <p class="eyebrow">ライトの設定</p>
          <h2>どんな光にしますか？</h2>
        </div>
        <span class="state-pill" class:live={effect?.running}>
          {effect?.stopAt ? '終了待ち' : effect?.nextKind ? '変更待ち' : effect?.running && clock < Date.parse(effect.startedAt) ? '準備中' : effect?.running ? '送信中' : 'プレビュー中'}
        </span>
      </div>

      <p class="step-label">1　動きを選ぶ</p>
      <div class="preset-grid">
        {#each effectPresets as item}
          {@render presetCard(item)}
        {/each}
      </div>

      <p class="preset-desc"><strong>{preset.label}</strong> — {preset.description}</p>

      <p class="step-label">2　光を設定する</p>
      <div class="field-grid">
        {#if params.kind === 'flow'}
          <label class="field wide">
            <span>光が広がる起点</span>
            <select value={selectedOrigin ?? ''} onchange={(event) => selectOrigin(eventValue(event))}>
              <option value="">自動（重心）</option>
              {#each balloons as balloon}
                <option value={balloon.meshAddress}>{balloon.name} · {formatAddress(balloon.meshAddress)}</option>
              {/each}
            </select>
            <small class="field-help">地図の丸をクリックしても選べます。</small>
          </label>
        {/if}

        {#if params.kind !== 'rainbow'}
          <div class="field wide">
            <span>光の色</span>
            <div class="colour-row">
              {@render colourPicker('光の色', primaryHex, (hex) => setPrimary(hex))}
            </div>
            <div class="presets">
              {#each colourPresets as colour}
                <button class="preset-swatch" style={`--c:${colour}`}
                        aria-label={`Use ${colour}`}
                        onclick={() => setPrimary(colour)}></button>
              {/each}
            </div>
          </div>
        {/if}

      </div>

      <details class="advanced">
        <summary>動きの詳しい設定</summary>
        <div class="field-grid advanced-fields">
          {#if params.kind !== 'rainbow'}
            <div class="field wide"><span>残光色</span>
              <div class="colour-row">{@render colourPicker('残光色', secondaryHex, (hex) => setSecondary(hex))}</div>
            </div>
          {/if}
          {#if params.kind === 'twinkle' || params.kind === 'alternate' || params.kind === 'solid'}
            {@render rangeField(params.kind === 'solid' ? '消灯までの周期' : '明滅の周期', '秒', 5, 20, 1, params.wavelength / params.speed, (value) => (params.wavelength = value * params.speed))}
          {:else if params.kind === 'pulse'}
            {@render rangeField('明滅の周期', '秒', 0.5, 20, 0.5, params.wavelength / Math.max(0.5, params.speed), (value) => (params.wavelength = value * Math.max(0.5, params.speed)))}
          {:else}
            {@render rangeField('速度', 'm/s', 1, 200, 1, params.speed, (value) => (params.speed = value))}
            {@render rangeField('波長', 'm', 5, 600, 5, params.wavelength, (value) => (params.wavelength = value))}
          {/if}
          {#if params.kind === 'flow' || params.kind === 'wave'}
            {@render rangeField('残光の長さ', '', 0.05, 1, 0.01, params.width, (value) => (params.width = value))}
          {/if}
          {#if params.kind === 'wave'}
            {@render rangeField('進行方向', '°', 0, 359, 1, params.directionDeg, (value) => (params.directionDeg = value))}
          {/if}
        </div>
      </details>

      {#if !model.canOperate}
        <p class="hint">Operator 以上のロールでエフェクトを開始できます。</p>
      {:else if positionedCount === 0}
        <p class="hint">位置情報のあるノードがありません。デモを開始すると 50 ノードが動き出します。</p>
      {:else}
        <p class="hint">地図は設定のプレビューです。開始するとライトは繰り返し点灯します。</p>
      {/if}
      {#if effect?.scheduleWarning}
        <p class="schedule-warning" role="alert">{effect.scheduleWarning}</p>
      {/if}
      <div class="action-bar">
        <p class="step-label">3　地図で確認して開始</p>
        <div class="actions">
          <button class="primary" onclick={startEffect}
                  disabled={!model.canOperate || busy || positionedCount === 0 || !!effect?.stopAt || !!effect?.nextKind}>
            {effect?.running ? '予約後にこの設定へ変更' : '30秒後の開始を予約'}
          </button>
          {#if effect?.running}
            <button onclick={stopEffect} disabled={busy || !!effect.stopAt || !!effect.nextKind}>
              {effect.stopAt ? '終了待ち' : '予約後に送信を終了'}
            </button>
          {/if}
        </div>
        {#if effect?.stopAt}
          <p class="hint">予約済みの周が終わると消灯し、送信を終了します。</p>
        {:else if effect?.running}
          <p class="hint">約30秒先まで予約します。変更や終了は、予約済みの周が終わってから反映されます。</p>
        {/if}
      </div>
    </section>

    <aside class="side">
      <section class="panel live-panel">
        <div class="panel-head compact">
          <div>
            <p class="eyebrow">ライトの状態</p>
            <h2>{effect?.stopAt ? '予約後に終了' : effect?.nextKind ? '予約後に変更' : effect?.running && clock < Date.parse(effect.startedAt) ? '開始準備中' : effect?.running ? 'ループ送信中' : '待機中'}</h2>
          </div>
          <span class="live-dot" class:on={effect?.running}></span>
        </div>
        <dl class="mini-grid">
          <div><dt>起点</dt><dd>{effect?.running ? (effect.originName ?? '重心') : (originBalloon?.name ?? '重心')}</dd></div>
          <div><dt>残り</dt><dd>{remainingLabel}</dd></div>
        </dl>
        <div class="progress"><span style={`width:${progress}%`}></span></div>
        <div class="preview-toggle">
          <label class="field">
            <span>地図の表示</span>
            <select value={displayMode} onchange={selectDisplay}>
              <option value="preview">編集中のプレビュー</option>
              <option value="shadow">送信命令からの予測</option>
            </select>
          </label>
          {#if displayMode === 'shadow'}
            <p class="hint">ゲートウェイへ渡した命令を時計同期済みとして実行した予測です。実機の受信確認ではありません。</p>
          {/if}
        </div>
      </section>

      <details class="panel fleet-panel">
        <summary class="side-summary">ノード一覧 <span>{positionedCount}台</span></summary>
        <div class="panel-head compact">
          <div>
            <p class="eyebrow">地図上のノード</p>
            <h2>選択して起点を変更</h2>
          </div>
          <span class="state-pill">{params.kind.toUpperCase()}</span>
        </div>
        <div class="node-list">
          {#each balloons as balloon}
            {@render nodeRow(balloon)}
          {/each}
        </div>
      </details>

      <details class="panel log-panel">
        <summary class="side-summary">操作履歴</summary>
        <div class="panel-head compact">
          <div>
            <p class="eyebrow">履歴</p>
            <h2>イベント</h2>
          </div>
        </div>
        {#if logLines.length === 0}
          <p class="hint">操作ログがここに表示されます。</p>
        {:else}
          <ul class="log">
            {#each logLines as line}<li>{line}</li>{/each}
          </ul>
        {/if}
      </details>
    </aside>
  </main>

  {#if mapError}
    <div class="map-error">地図を利用できません: {mapError}</div>
  {/if}
</div>

{#snippet presetCard(item)}
  <button class="preset-card" class:selected={params.kind === item.id}
          onclick={() => choosePreset(item.id)}>
    <span class="preset-glyph">{item.glyph}</span>
    <span class="preset-main">
      <strong>{item.label}</strong>
      <small>{item.english}</small>
    </span>
  </button>
{/snippet}

{#snippet stat(value, label, tone = '', link = false)}
  <div class="stat" class:good={tone === 'good'} class:bad={tone === 'bad'} class:link={link}>
    <strong>{value}</strong><span>{label}</span>
  </div>
{/snippet}

{#snippet colourPicker(label, hex, onInput)}
  <label class="colour-picker">
    <input type="color" aria-label={label} value={hex} oninput={(event) => onInput(eventValue(event))} />
    <span>{hex.toUpperCase()}</span>
  </label>
{/snippet}

{#snippet nodeRow(balloon)}
  <button class="node-row" class:selected={balloon.meshAddress === selectedOrigin}
          onclick={() => selectOrigin(String(balloon.meshAddress))}>
    <span class="swatch" style={`background:${nodeColorCss(balloon)}; box-shadow:0 0 10px ${nodeColorCss(balloon)}`}></span>
    <span class="node-main">
      <strong>{balloon.name}</strong>
      <small>{formatAddress(balloon.meshAddress)} · {balloon.groupName}</small>
    </span>
    <span class="node-meta">
      <small>{balloon.online === false ? 'offline' : 'online'}</small>
      {#if displayMode === 'shadow'}
        {#if shadowNodes[balloon.meshAddress]}
          <small>予測の予約: {shadowNodes[balloon.meshAddress].queueCount}件</small>
          {#if !shadowNodes[balloon.meshAddress].known}<small>予測色: 不明</small>{/if}
        {:else}
          <small>予測: 不明</small>
        {/if}
        {#if (shadowNodes[balloon.meshAddress]?.rejectedCount ?? 0) > 0}
          <small>予測の除外: {shadowNodes[balloon.meshAddress]?.rejectedCount}件</small>
        {/if}
      {/if}
      <small>{balloon.lat == null ? 'no fix' : `${balloon.lat.toFixed(4)}, ${balloon.lon?.toFixed(4)}`}</small>
    </span>
  </button>
{/snippet}

{#snippet rangeField(label, unit, min, max, step, value, onInput)}
  <label class="field range-field">
    <span>{label} <em>{value}{unit}</em></span>
    <input type="range" {min} {max} {step} {value}
           oninput={(event) => onInput(eventNumber(event))} />
  </label>
{/snippet}

<style>
  :global(html), :global(body) {
    margin: 0;
    padding: 0;
    height: 100%;
    background: #050b14;
    color: #e2e8f0;
    font-family: "Inter", "Noto Sans JP", "Segoe UI", system-ui, sans-serif;
  }
  :global(*) { box-sizing: border-box; }

  .app-shell { position: fixed; inset: 0; overflow: hidden; }
  .map {
    position: absolute;
    inset: 0;
    background: radial-gradient(circle at 50% 40%, #10243f 0%, #050b14 70%);
  }

  .topbar {
    position: absolute;
    top: 16px;
    left: 16px;
    right: 16px;
    z-index: 20;
    display: flex;
    align-items: center;
    gap: 18px;
    padding: 12px 16px;
    border-radius: 18px;
    background: rgba(7, 17, 30, 0.85);
    border: 1px solid rgba(148, 163, 184, 0.18);
    box-shadow: 0 18px 40px rgba(0, 0, 0, 0.35);
    backdrop-filter: blur(16px);
  }
  .brand { display: flex; align-items: center; gap: 12px; min-width: 230px; }
  .brand-mark {
    width: 32px; height: 32px; border-radius: 50%;
    background: conic-gradient(from 210deg, #38bdf8, #a78bfa, #f472b6, #38bdf8);
    box-shadow: 0 0 24px rgba(56, 189, 248, 0.5);
  }
  .brand h1 { margin: 0; font-size: 18px; letter-spacing: 0.02em; }
  .brand p { margin: 2px 0 0; font-size: 11px; color: #94a3b8; }

  .nav { display: flex; gap: 4px; padding: 3px; border-radius: 999px; background: rgba(15, 23, 42, 0.7); border: 1px solid rgba(148, 163, 184, 0.16); }
  .nav-item {
    padding: 7px 14px; border-radius: 999px; font-size: 11px; font-weight: 700;
    color: #94a3b8; text-decoration: none; letter-spacing: 0.02em;
  }
  .nav-item:hover { color: #e2e8f0; background: rgba(56, 189, 248, 0.08); }
  .nav-item.active { color: #bae6fd; background: rgba(56, 189, 248, 0.16); }

  .stats { display: flex; gap: 16px; margin-left: auto; align-items: center; }
  .stat { display: flex; flex-direction: column; min-width: 52px; }
  .stat strong { font-size: 16px; line-height: 1.1; }
  .stat span { font-size: 9px; text-transform: uppercase; letter-spacing: 0.08em; color: #94a3b8; }
  .stat.link { min-width: 108px; }
  .stat.link strong { font-size: 11px; letter-spacing: 0.14em; }
  .good { color: #34d399 !important; }
  .bad { color: #f87171 !important; }

  .user-chip {
    display: flex; align-items: center; gap: 9px; padding: 6px 8px 6px 6px;
    border-radius: 12px; border: 1px solid rgba(148, 163, 184, 0.18);
    background: rgba(15, 23, 42, 0.55);
  }
  .user-chip img, .user-avatar {
    width: 30px; height: 30px; border-radius: 9px; object-fit: cover;
    display: grid; place-items: center; font-size: 12px; font-weight: 700;
    background: rgba(56, 189, 248, 0.16); color: #7dd3fc;
  }
  .user-meta { display: flex; flex-direction: column; line-height: 1.15; }
  .user-meta strong { font-size: 11px; max-width: 130px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .user-meta small { font-size: 9px; color: #94a3b8; }
  .logout { padding: 6px 8px; font-size: 9px; }

  .workspace {
    position: absolute;
    top: 88px;
    left: 16px;
    right: 16px;
    bottom: 16px;
    z-index: 15;
    display: flex;
    gap: 12px;
    pointer-events: none;
  }
  .workspace > * { pointer-events: auto; }

  .panel {
    background: rgba(7, 17, 30, 0.9);
    border: 1px solid rgba(148, 163, 184, 0.16);
    border-radius: 18px;
    padding: 16px;
    backdrop-filter: blur(16px);
    box-shadow: 0 18px 40px rgba(0, 0, 0, 0.28);
  }
  .composer {
    width: 430px;
    max-height: 100%;
    overflow-y: auto;
    scrollbar-width: thin;
  }
  .side {
    margin-left: auto;
    width: 330px;
    max-height: 100%;
    display: flex;
    flex-direction: column;
    gap: 12px;
    overflow-y: auto;
    scrollbar-width: thin;
  }

  .panel-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; margin-bottom: 12px; }
  .panel-head.compact { margin-bottom: 8px; }
  .eyebrow { margin: 0 0 2px; font-size: 9px; text-transform: uppercase; letter-spacing: 0.16em; color: #64748b; }
  .panel-head h2 { margin: 0; font-size: 16px; letter-spacing: 0.01em; }

  .state-pill {
    font-size: 9px; letter-spacing: 0.12em; text-transform: uppercase;
    color: #94a3b8; border: 1px solid rgba(148, 163, 184, 0.3);
    border-radius: 999px; padding: 4px 9px; white-space: nowrap;
  }
  .state-pill.live { color: #6ee7b7; border-color: rgba(52, 211, 153, 0.5); background: rgba(52, 211, 153, 0.1); }

  .preset-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
  .preset-card {
    display: flex; align-items: center; gap: 10px;
    padding: 11px; border-radius: 13px; text-align: left;
    border: 1px solid rgba(148, 163, 184, 0.2);
    background: rgba(15, 23, 42, 0.72);
    color: #e2e8f0; cursor: pointer;
  }
  .preset-card:hover { border-color: rgba(56, 189, 248, 0.45); background: rgba(56, 189, 248, 0.09); }
  .preset-card.selected {
    border-color: rgba(56, 189, 248, 0.65);
    background: linear-gradient(135deg, rgba(56, 189, 248, 0.2), rgba(167, 139, 250, 0.14));
    box-shadow: inset 0 0 0 1px rgba(56, 189, 248, 0.25);
  }
  .preset-glyph {
    width: 42px; height: 30px; border-radius: 8px; display: grid; place-items: center;
    font-size: 8px; font-weight: 800; letter-spacing: 0.1em;
    color: #7dd3fc; background: rgba(56, 189, 248, 0.13);
  }
  .preset-card.selected .preset-glyph { color: #bae6fd; background: rgba(56, 189, 248, 0.24); }
  .preset-main { display: flex; flex-direction: column; line-height: 1.2; }
  .preset-main strong { font-size: 13px; }
  .preset-main small { font-size: 9px; color: #94a3b8; }

  .preset-desc { margin: 12px 0 14px; font-size: 11px; line-height: 1.6; color: #94a3b8; }
  .preset-desc strong { color: #e2e8f0; }
  .step-label { margin: 17px 0 8px; color: #bae6fd; font-size: 12px; font-weight: 700; }
  .field-help { color: #94a3b8; font-size: 10px; }
  .advanced { margin-top: 14px; border-top: 1px solid rgba(148, 163, 184, 0.18); padding-top: 10px; }
  .advanced summary, .side-summary { color: #cbd5e1; font-size: 12px; font-weight: 700; cursor: pointer; }
  .advanced-fields { margin-top: 12px; }
  .side-summary span { float: right; color: #94a3b8; font-weight: 400; }
  .side-summary + .panel-head { margin-top: 14px; }

  .field-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 10px 12px; }
  .field { display: flex; flex-direction: column; gap: 5px; }
  .field.wide { grid-column: 1 / -1; }
  .field > span {
    display: flex; justify-content: space-between; gap: 8px;
    font-size: 10px; letter-spacing: 0.06em; color: #94a3b8;
  }
  .field > span em { font-style: normal; color: #7dd3fc; font-variant-numeric: tabular-nums; }
  .field select {
    border: 1px solid rgba(148, 163, 184, 0.22);
    background: rgba(15, 23, 42, 0.8);
    color: #e2e8f0;
    border-radius: 9px;
    padding: 8px 10px;
    font-size: 12px;
  }
  .range-field input[type="range"] {
    width: 100%; accent-color: #38bdf8; height: 18px; margin: 0;
  }

  .colour-row { display: flex; gap: 8px; flex-wrap: wrap; }
  .colour-picker {
    display: flex; align-items: center; gap: 7px;
    border: 1px solid rgba(148, 163, 184, 0.22);
    border-radius: 10px; padding: 4px 8px; font-size: 10px; color: #94a3b8;
  }
  .colour-picker input { width: 28px; height: 24px; padding: 0; border: none; background: transparent; }
  .presets { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 8px; }
  .preset-swatch {
    width: 20px; height: 20px; padding: 0; border-radius: 50%;
    background: var(--c); border: 2px solid rgba(255, 255, 255, 0.22);
  }
  .preset-swatch:hover { transform: scale(1.12); }

  .actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-top: 16px; }
  .action-bar { position: sticky; bottom: -16px; margin: 0 -16px -16px; padding: 1px 16px 16px; background: #0b1928; border-top: 1px solid rgba(148, 163, 184, 0.18); }
  .action-bar .step-label { margin: 10px 0 0; }
  .action-bar .actions { margin-top: 8px; }
  .check { display: flex; align-items: center; gap: 6px; font-size: 10px; color: #94a3b8; }
  .check input { accent-color: #38bdf8; }

  button {
    border: 1px solid rgba(148, 163, 184, 0.25);
    background: rgba(15, 23, 42, 0.75);
    color: #e2e8f0;
    border-radius: 9px;
    padding: 8px 12px;
    font-size: 11px;
    font-weight: 600;
    cursor: pointer;
    transition: border-color 0.15s ease, background 0.15s ease, transform 0.15s ease;
  }
  button:hover { border-color: rgba(56, 189, 248, 0.5); background: rgba(56, 189, 248, 0.12); }
  button:active { transform: translateY(1px); }
  button.primary { background: rgba(56, 189, 248, 0.2); border-color: rgba(56, 189, 248, 0.5); color: #bae6fd; }
  button:disabled { opacity: 0.42; cursor: not-allowed; }
  button:disabled:hover { border-color: rgba(148, 163, 184, 0.25); background: rgba(15, 23, 42, 0.75); }

  .hint { margin: 10px 0 0; font-size: 10px; line-height: 1.5; color: #94a3b8; }
  .schedule-warning { margin: 10px 0; color: #fca5a5; font-size: 12px; line-height: 1.5; }

  .live-panel, .fleet-panel, .log-panel { flex: 0 0 auto; }
  .live-dot {
    width: 10px; height: 10px; border-radius: 50%;
    background: #475569; box-shadow: 0 0 0 4px rgba(71, 85, 105, 0.18);
  }
  .live-dot.on { background: #34d399; box-shadow: 0 0 0 4px rgba(52, 211, 153, 0.16), 0 0 14px rgba(52, 211, 153, 0.8); }
  .mini-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 7px 12px; margin: 6px 0 12px; }
  .mini-grid dt { font-size: 9px; text-transform: uppercase; letter-spacing: 0.08em; color: #64748b; }
  .mini-grid dd { margin: 1px 0 0; font-size: 12px; overflow-wrap: anywhere; }
  .progress { height: 4px; border-radius: 999px; background: rgba(148, 163, 184, 0.18); overflow: hidden; }
  .progress span { display: block; height: 100%; background: linear-gradient(90deg, #38bdf8, #a78bfa, #f472b6); transition: width 0.3s linear; }
  .preview-toggle { margin-top: 10px; }

  .node-list { max-height: 260px; overflow-y: auto; display: flex; flex-direction: column; gap: 3px; scrollbar-width: thin; }
  .node-row {
    display: flex; align-items: center; gap: 9px; width: 100%;
    border: 1px solid transparent; background: transparent; color: inherit;
    border-radius: 9px; padding: 6px 7px; cursor: pointer; text-align: left;
  }
  .node-row:hover { background: rgba(56, 189, 248, 0.08); }
  .node-row.selected { background: rgba(56, 189, 248, 0.14); border-color: rgba(56, 189, 248, 0.35); }
  .swatch {
    width: 12px; height: 12px; border-radius: 50%; flex: 0 0 auto;
    border: 1px solid rgba(255, 255, 255, 0.35);
    transition: background 0.09s linear, box-shadow 0.09s linear;
  }
  .node-main { display: flex; flex-direction: column; gap: 1px; min-width: 0; }
  .node-main strong { font-size: 11px; }
  .node-main small, .node-meta small { color: #94a3b8; font-size: 9px; }
  .node-meta { margin-left: auto; text-align: right; display: flex; flex-direction: column; gap: 1px; }

  .log { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 5px; }
  .log li { font-size: 10px; color: #94a3b8; border-left: 2px solid rgba(56, 189, 248, 0.4); padding-left: 8px; }

  .map-error {
    position: absolute; left: 16px; bottom: 16px; z-index: 30;
    padding: 10px 14px; border-radius: 12px;
    background: rgba(127, 29, 29, 0.85); color: #fecaca; font-size: 12px;
  }

  :global(.balloon-marker) {
    display: flex; flex-direction: column; align-items: center; gap: 3px;
    cursor: pointer; user-select: none;
  }
  :global(.balloon-marker .balloon-dot) {
    width: 15px; height: 15px; border-radius: 50%;
    border: 2px solid rgba(255, 255, 255, 0.7);
    box-shadow: 0 0 16px var(--glow, rgba(56, 189, 248, 0.45)), 0 2px 6px rgba(0, 0, 0, 0.5);
    transition: background 0.09s linear, box-shadow 0.09s linear, transform 0.15s ease;
  }
  :global(.balloon-marker .balloon-label) {
    font-size: 8px; font-weight: 700; color: #e2e8f0;
    text-shadow: 0 1px 3px #000, 0 0 4px #000;
  }
  :global(.balloon-marker.selected .balloon-dot) {
    transform: scale(1.55);
    border-color: #facc15;
    box-shadow: 0 0 0 5px rgba(250, 204, 21, 0.18), 0 0 24px rgba(250, 204, 21, 0.7);
  }
  :global(.balloon-marker.offline) { opacity: 0.55; }

  :global(.maplibregl-ctrl-group) {
    background: rgba(7, 17, 30, 0.85) !important;
    border: 1px solid rgba(148, 163, 184, 0.2);
  }
  :global(.maplibregl-ctrl-group button span) { filter: invert(1); }

  @media (max-width: 1180px) {
    .composer { width: 390px; }
    .side { width: 290px; }
  }
  @media (max-width: 980px) {
    .topbar { flex-wrap: wrap; }
    .stats { order: 4; width: 100%; margin-left: 0; }
    .nav { order: 3; }
    .workspace { flex-direction: column; overflow-y: auto; }
    .composer, .side { width: auto; max-height: none; margin-left: 0; }
    .node-list { max-height: 180px; }
  }
</style>
