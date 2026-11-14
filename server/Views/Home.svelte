<script lang="ts">
  import { onMount } from 'svelte';
  import type { HomePage } from '@sveltesharp/models';

  let { model }: { model: HomePage } = $props();

  type Balloon = {
    meshAddress: number;
    name: string;
    groupId: number;
    groupName: string;
    lat: number | null;
    lon: number | null;
    confidence: number | null;
    r: number;
    g: number;
    b: number;
    queueCount: number;
    estimatedQueueCount: number;
    lastMethod: string | null;
    online: boolean;
  };
  type Anchor = {
    anchorId: number;
    name: string;
    lat: number;
    lon: number;
    altM: number;
    timeValid: boolean;
    positionValid: boolean;
  };
  type Gateway = {
    id: string;
    webSocket: boolean;
    remote: string | null;
    firmware: string | null;
    lastSeen: string;
    pendingCommands: number;
    provisioned: boolean;
    spoolPending: number;
  };
  type GatewayMeshRange = {
    rangeStart: number;
    rangeEnd: number;
    nextAddress: number;
    allocatedNodes: number;
  };
  type GatewayMeshAllocation = {
    gatewayId: string;
    meshAddress: number;
    ranges: GatewayMeshRange[];
  };
  type Group = { id: number; name: string; meshGroupAddress: number; balloonCount: number };
  type HistorySample = { lat: number; lon: number; confidence: number; method: string };
  type MockDevice = {
    id: string;
    name: string;
    kind: string;
    uuid: string;
    meshAddress: number | null;
    state: string;
    progress: number;
    error: string | null;
  };
  type ShowStep = {
    id: number;
    name: string;
    targetKind: string;
    target: string;
    executeAt: string;
    r: number;
    g: number;
    b: number;
    status: string;
  };

  let balloons = $state<Balloon[]>([]);
  let anchors = $state<Anchor[]>([]);
  let gateways = $state<Gateway[]>([]);
  let meshAllocations = $state<GatewayMeshAllocation[]>([]);
  let groups = $state<Group[]>([]);
  let simulationRunning = $state(false);
  let selectedAddress = $state<number | null>(null);
  let search = $state('');
  let targetMode = $state<'balloon' | 'all'>('balloon');
  let colorHex = $state('#38bdf8');
  let executeLocal = $state('');
  let pickMode = $state(false);
  let signalConnected = $state(false);
  let mapReady = $state(false);
  let mapError = $state('');
  let statusLine = $state('Connecting…');
  let logLines = $state<string[]>([]);
  let trailVisible = $state(true);
  let selectedHistory = $state<HistorySample[]>([]);
  let showSteps = $state<ShowStep[]>([]);
  let onboarding = $state<MockDevice[]>([]);
  let busy = $state(false);

  let map: any = null;
  let maplibregl: any = null;
  let markers = new Map<number, any>();
  let attachedMarkers = new Set<number>();
  let anchorMarkers: any[] = [];
  let hub: any = null;
  let stateLoaded = false;

  const selectedBalloon = $derived(
    balloons.find((balloon) => balloon.meshAddress === selectedAddress) ?? null
  );
  const filteredBalloons = $derived(
    balloons.filter((balloon) => {
      const query = search.trim().toLowerCase();
      return !query || balloon.name.toLowerCase().includes(query) ||
        balloon.meshAddress.toString().includes(query);
    })
  );
  const onlineCount = $derived(balloons.filter((balloon) => balloon.online).length);
  const provisionedGateways = $derived(gateways.filter((gateway) => gateway.provisioned).length);

  function roleLabel(): string {
    return model.canAdmin ? 'Admin' : model.canOperate ? 'Operator' : 'Viewer';
  }

  function addLog(message: string) {
    const now = new Date().toLocaleTimeString();
    logLines = [`${now}  ${message}`, ...logLines].slice(0, 6);
    statusLine = message;
  }

  async function loadScript(src: string): Promise<void> {
    if (document.querySelector(`script[src="${src}"]`)) return;
    await new Promise<void>((resolve, reject) => {
      const script = document.createElement('script');
      script.src = src;
      script.async = true;
      script.onload = () => resolve();
      script.onerror = () => reject(new Error(`Failed to load ${src}`));
      document.head.appendChild(script);
    });
  }

  async function loadStylesheet(href: string): Promise<void> {
    if (document.querySelector(`link[href="${href}"]`)) return;
    await new Promise<void>((resolve, reject) => {
      const link = document.createElement('link');
      link.rel = 'stylesheet';
      link.href = href;
      link.onload = () => resolve();
      link.onerror = () => reject(new Error(`Failed to load ${href}`));
      document.head.appendChild(link);
    });
  }

  async function api(path: string, options: RequestInit = {}) {
    const response = await fetch(path, {
      ...options,
      headers: { 'Content-Type': 'application/json', ...(options.headers ?? {}) }
    });
    if (response.status === 204) return null;
    const text = await response.text();
    const body = text ? JSON.parse(text) : null;
    if (!response.ok) {
      throw new Error(body?.error ?? response.statusText);
    }
    return body;
  }

  function applyState(state: any) {
    balloons = state.balloons ?? [];
    anchors = state.anchors ?? [];
    gateways = state.gateways ?? [];
    groups = state.groups ?? [];
    simulationRunning = !!state.simulationRunning;
    if (!stateLoaded) {
      stateLoaded = true;
      if (!selectedAddress && balloons.length) {
        selectedAddress = balloons[0].meshAddress;
      }
      fitInitialView();
    }
    syncMarkers();
    syncAnchors();
    if (selectedAddress != null) {
      void loadTrail(selectedAddress);
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
    updateMarker(balloon);
    if (balloon.meshAddress === selectedAddress) {
      void loadTrail(balloon.meshAddress);
    }
  }

  function markerColor(balloon: Balloon): string {
    const max = Math.max(balloon.r, balloon.g, balloon.b);
    if (max < 8) return '#64748b';
    return `rgb(${balloon.r}, ${balloon.g}, ${balloon.b})`;
  }

  function createMarker(balloon: Balloon) {
    const element = document.createElement('div');
    element.className = 'balloon-marker';
    element.innerHTML = '<span class="balloon-dot"></span><span class="balloon-label"></span>';
    element.addEventListener('click', (event) => {
      event.stopPropagation();
      selectBalloon(balloon.meshAddress);
    });
    const marker = new maplibregl.Marker({ element, anchor: 'center' });
    markers.set(balloon.meshAddress, marker);
    updateMarkerElement(balloon, element);
    attachMarker(balloon, marker);
    return marker;
  }

  function updateMarkerElement(balloon: Balloon, element: HTMLElement) {
    const dot = element.querySelector('.balloon-dot') as HTMLElement | null;
    const label = element.querySelector('.balloon-label') as HTMLElement | null;
    if (dot) dot.style.background = markerColor(balloon);
    if (label) label.textContent = balloon.name.replace(/^B-/, '');
    element.classList.toggle('selected', balloon.meshAddress === selectedAddress);
    element.classList.toggle('offline', !balloon.online);
    element.title = `${balloon.name} • 0x${balloon.meshAddress.toString(16).toUpperCase()}`;
  }

  function attachMarker(balloon: Balloon, marker: any) {
    if (!mapReady || balloon.lat == null || balloon.lon == null) return;
    marker.setLngLat([balloon.lon, balloon.lat]);
    if (!attachedMarkers.has(balloon.meshAddress)) {
      marker.addTo(map);
      attachedMarkers.add(balloon.meshAddress);
    }
  }

  function updateMarker(balloon: Balloon) {
    let marker = markers.get(balloon.meshAddress);
    if (!marker) {
      marker = createMarker(balloon);
    }
    const element = marker.getElement();
    updateMarkerElement(balloon, element);
    attachMarker(balloon, marker);
  }

  function syncMarkers() {
    if (!maplibregl || !mapReady) return;
    for (const balloon of balloons) {
      updateMarker(balloon);
    }
  }

  function syncAnchors() {
    if (!maplibregl || !mapReady) return;
    for (const marker of anchorMarkers) marker.remove();
    anchorMarkers = [];
    for (const anchor of anchors) {
      const element = document.createElement('div');
      element.className = 'anchor-marker';
      element.innerHTML = '<span></span><em></em>';
      (element.querySelector('em') as HTMLElement).textContent =
        `A${anchor.anchorId}${anchor.timeValid ? '' : ' · no time'}`;
      const marker = new maplibregl.Marker({ element, anchor: 'bottom' })
        .setLngLat([anchor.lon, anchor.lat])
        .addTo(map);
      anchorMarkers.push(marker);
    }
  }

  function fitInitialView() {
    if (!mapReady || !map) return;
    const points = balloons
      .filter((balloon) => balloon.lat != null && balloon.lon != null)
      .map((balloon) => [balloon.lon as number, balloon.lat as number]);
    for (const anchor of anchors) points.push([anchor.lon, anchor.lat]);
    if (points.length === 0) return;
    const bounds = points.reduce(
      (acc, point) => acc.extend(point),
      new maplibregl.LngLatBounds(points[0], points[0])
    );
    map.fitBounds(bounds, { padding: 80, maxZoom: 17, duration: 0 });
  }

  function selectBalloon(address: number) {
    selectedAddress = address;
    for (const balloon of balloons) {
      const marker = markers.get(balloon.meshAddress);
      if (marker) updateMarkerElement(balloon, marker.getElement());
    }
    void loadTrail(address);
  }

  async function loadTrail(address: number) {
    try {
      const history = await api(`/api/balloons/${address}/history?limit=160`) as HistorySample[];
      selectedHistory = history;
      updateTrailLayer(history);
    } catch {
      // trail is optional
    }
  }

  function updateTrailLayer(history: HistorySample[]) {
    if (!mapReady || !map) return;
    const source = map.getSource('trail');
    if (!source) return;
    const coordinates = history
      .slice()
      .reverse()
      .map((sample) => [sample.lon, sample.lat]);
    source.setData({
      type: 'Feature',
      geometry: {
        type: 'LineString',
        coordinates: trailVisible ? coordinates : []
      }
    });
  }

  function toggleTrail() {
    trailVisible = !trailVisible;
    updateTrailLayer(selectedHistory);
  }

  async function initMap() {
    await loadStylesheet('https://unpkg.com/maplibre-gl@4.7.1/dist/maplibre-gl.css');
    await loadScript('https://unpkg.com/maplibre-gl@4.7.1/dist/maplibre-gl.js');
    maplibregl = (window as any).maplibregl;
    if (!maplibregl) throw new Error('MapLibre did not initialise.');

    map = new maplibregl.Map({
      container: 'map',
      center: [139.775, 35.63],
      zoom: 16,
      style: {
        version: 8,
        sources: {
          osm: {
            type: 'raster',
            tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'],
            tileSize: 256,
            attribution: '© OpenStreetMap contributors'
          }
        },
        layers: [{ id: 'osm', type: 'raster', source: 'osm' }]
      }
    });
    // Markers can be attached before the style finishes loading.  This keeps
    // the initial fleet visible even when the raster tile request is slow.
    mapReady = true;
    syncMarkers();
    syncAnchors();

    map.addControl(new maplibregl.NavigationControl({ visualizePitch: false }), 'bottom-left');
    map.addControl(new maplibregl.ScaleControl({ unit: 'metric' }), 'bottom-left');

    map.on('load', () => {
      map.addSource('trail', {
        type: 'geojson',
        data: { type: 'Feature', geometry: { type: 'LineString', coordinates: [] } }
      });
      map.addLayer({
        id: 'trail',
        type: 'line',
        source: 'trail',
        paint: {
          'line-color': '#38bdf8',
          'line-width': 2,
          'line-opacity': 0.65
        }
      });
      mapReady = true;
      syncMarkers();
      syncAnchors();
      fitInitialView();
      updateTrailLayer(selectedHistory);
    });

    map.on('click', async (event: any) => {
      if (!pickMode || !selectedBalloon) return;
      const lat = event.lngLat.lat;
      const lon = event.lngLat.lng;
      await applyInitialPosition(lat, lon);
    });
  }

  async function initSignalR() {
    await loadScript('https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.7/dist/browser/signalr.min.js');
    const signalR = (window as any).signalR;
    if (!signalR) throw new Error('SignalR did not initialise.');

    hub = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/monitor')
      .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
      .build();
    hub.on('state', applyState);
    hub.on('balloonUpdated', upsertBalloon);
    hub.on('queueUpdated', upsertBalloon);
    hub.on('gatewaysUpdated', (value: Gateway[]) => { gateways = value; });
    hub.on('simulationChanged', (value: boolean) => { simulationRunning = value; });
    hub.on('onboardingUpdated', (value: MockDevice[]) => { onboarding = value; });
    hub.on('commandSent', (result: any) => {
      if (result?.message) addLog(result.message);
    });
    hub.onreconnecting(() => { signalConnected = false; statusLine = 'Reconnecting…'; });
    hub.onreconnected(() => { signalConnected = true; statusLine = 'Live'; });
    hub.onclose(() => { signalConnected = false; statusLine = 'Disconnected'; });

    try {
      await hub.start();
      signalConnected = true;
      statusLine = 'Live';
    } catch (error) {
      signalConnected = false;
      statusLine = 'Polling mode';
      addLog(`SignalR unavailable: ${(error as Error).message}`);
    }
  }

  async function fetchOnboarding() {
    if (!model.canAdmin) return;
    try {
      onboarding = await api('/api/onboarding/mock') as MockDevice[];
    } catch {
      // onboarding is optional
    }
  }

  async function provisionMock(id: string) {
    if (!model.canAdmin) return;
    try {
      await api(`/api/onboarding/mock/${id}/provision`, { method: 'POST' });
      await fetchOnboarding();
    } catch (error) {
      addLog(`Mock provision: ${(error as Error).message}`);
    }
  }

  async function resetMockOnboarding() {
    if (!model.canAdmin) return;
    try {
      await api('/api/onboarding/mock/reset', { method: 'POST' });
      await fetchOnboarding();
      addLog('Mock onboarding reset');
    } catch (error) {
      addLog(`Mock reset: ${(error as Error).message}`);
    }
  }

  async function fetchShow() {
    try {
      showSteps = await api('/api/show?limit=12') as ShowStep[];
    } catch {
      // show list is optional
    }
  }

  async function fetchGatewayMeshAllocations() {
    try {
      meshAllocations = await api('/api/gateway-mesh-allocations') as GatewayMeshAllocation[];
    } catch {
      // Allocation state is supplementary to the live gateway state.
    }
  }

  async function fetchState() {
    try {
      const state = await api('/api/state');
      applyState(state);
      void fetchShow();
      void fetchOnboarding();
      void fetchGatewayMeshAllocations();
    } catch (error) {
      addLog(`State refresh failed: ${(error as Error).message}`);
    }
  }

  async function cancelShowStep(id: number) {
    if (!model.canOperate) return;
    try {
      const result = await api(`/api/show/${id}`, { method: 'DELETE' });
      if (result?.message) addLog(result.message);
      await fetchShow();
      await fetchState();
    } catch (error) {
      addLog(`Cancel show step: ${(error as Error).message}`);
    }
  }

  function targetLabel(step: ShowStep): string {
    if (step.targetKind === 'all') return 'All balloons';
    if (step.targetKind === 'group') {
      const group = groups.find((item) => String(item.id) === step.target);
      return group ? group.name : `Group ${step.target}`;
    }
    const balloon = balloons.find((item) => String(item.meshAddress) === step.target);
    return balloon ? balloon.name : `Balloon ${step.target}`;
  }

  function formatDateTime(value: string): string {
    const date = new Date(value);
    return date.toLocaleString([], {
      month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit'
    });
  }

  function formatMeshAddress(value: number): string {
    return `0x${value.toString(16).toUpperCase().padStart(4, '0')}`;
  }

  function meshAllocationFor(gatewayId: string): GatewayMeshAllocation | null {
    return meshAllocations.find((allocation) => allocation.gatewayId === gatewayId) ?? null;
  }

  function rangeCapacity(range: GatewayMeshRange): number {
    return range.rangeEnd - range.rangeStart + 1;
  }

  function hexToRgb(hex: string): { r: number; g: number; b: number } {
    const normalized = hex.replace('#', '');
    const value = normalized.length === 3
      ? normalized.split('').map((part) => part + part).join('')
      : normalized;
    const number = Number.parseInt(value, 16);
    return {
      r: (number >> 16) & 255,
      g: (number >> 8) & 255,
      b: number & 255
    };
  }

  function toLocalInput(date: Date): string {
    const pad = (value: number) => String(value).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}` +
      `T${pad(date.getHours())}:${pad(date.getMinutes())}`;
  }

  function targetPayload() {
    if (targetMode === 'all') return { targetKind: 'all', target: 'all' };
    if (!selectedBalloon) throw new Error('Select a balloon first.');
    return { targetKind: 'balloon', target: String(selectedBalloon.meshAddress) };
  }

  async function runCommand(action: () => Promise<any>, label: string) {
    if (busy) return;
    busy = true;
    try {
      const result = await action();
      if (result?.message) addLog(result.message);
      if (result?.accepted === false) addLog(`${label}: ${result.message ?? 'rejected'}`);
      await fetchState();
      await fetchShow();
    } catch (error) {
      addLog(`${label}: ${(error as Error).message}`);
    } finally {
      busy = false;
    }
  }

  async function setNow() {
    await runCommand(async () => {
      const { r, g, b } = hexToRgb(colorHex);
      return api('/api/commands/now', {
        method: 'POST',
        body: JSON.stringify({ ...targetPayload(), r, g, b })
      });
    }, 'LED now');
  }

  async function schedule() {
    await runCommand(async () => {
      const { r, g, b } = hexToRgb(colorHex);
      return api('/api/commands/led-set', {
        method: 'POST',
        body: JSON.stringify({
          ...targetPayload(),
          executeAt: new Date(executeLocal).toISOString(),
          r, g, b
        })
      });
    }, 'LED schedule');
  }

  async function cancelAt() {
    await runCommand(async () => api('/api/commands/cancel', {
      method: 'POST',
      body: JSON.stringify({
        ...targetPayload(),
        executeAt: new Date(executeLocal).toISOString()
      })
    }), 'LED cancel');
  }

  async function resetAll(setNow: boolean) {
    await runCommand(async () => {
      const { r, g, b } = hexToRgb(colorHex);
      return api('/api/commands/reset', {
        method: 'POST',
        body: JSON.stringify({
          ...targetPayload(),
          cutoffAt: new Date().toISOString(),
          setNow,
          r, g, b
        })
      });
    }, setNow ? 'Reset + set' : 'Reset');
  }

  async function applyInitialPosition(lat: number, lon: number) {
    if (!model.canOperate || !selectedBalloon) return;
    try {
      const updated = await api(`/api/balloons/${selectedBalloon.meshAddress}/initial-position`, {
        method: 'POST',
        body: JSON.stringify({ lat, lon })
      });
      upsertBalloon(updated);
      pickMode = false;
      addLog(`Initial position set for ${updated.name}`);
      await fetchState();
    } catch (error) {
      addLog(`Initial position: ${(error as Error).message}`);
    }
  }

  async function setInitialFromInputs() {
    if (!selectedBalloon || selectedBalloon.lat == null || selectedBalloon.lon == null) return;
    await applyInitialPosition(selectedBalloon.lat, selectedBalloon.lon);
  }

  async function toggleSimulation() {
    if (!model.canAdmin) return;
    try {
      const path = simulationRunning ? '/api/demo/stop' : '/api/demo/start';
      await api(path, { method: 'POST' });
      simulationRunning = !simulationRunning;
      addLog(simulationRunning ? 'Demo simulation started' : 'Demo simulation stopped');
    } catch (error) {
      addLog(`Simulation: ${(error as Error).message}`);
    }
  }

  onMount(() => {
    const onError = (event: ErrorEvent) => {
      addLog(`JS error: ${event.message}`);
    };
    const onRejection = (event: PromiseRejectionEvent) => {
      addLog(`JS rejection: ${String(event.reason)}`);
    };
    window.addEventListener('error', onError);
    window.addEventListener('unhandledrejection', onRejection);
    executeLocal = toLocalInput(new Date(Date.now() + 120_000));
    void (async () => {
      await fetchState();
      try {
        await initMap();
      } catch (error) {
        mapError = (error as Error).message;
        addLog(`Map unavailable: ${mapError}`);
      }
      try {
        await initSignalR();
      } catch (error) {
        addLog(`SignalR init failed: ${(error as Error).message}`);
      }
    })();

    const timer = window.setInterval(fetchState, 15_000);
    return () => {
      window.clearInterval(timer);
      window.removeEventListener('error', onError);
      window.removeEventListener('unhandledrejection', onRejection);
      if (hub) void hub.stop();
      if (map) map.remove();
    };
  });
</script>

<svelte:head>
  <title>{model.title}</title>
</svelte:head>

<div class="app-shell">
  <div id="map" class="map"></div>

  <header class="topbar">
    <div class="brand">
      <span class="brand-mark"></span>
      <div>
        <h1>{model.title}</h1>
        <p>{model.subtitle}</p>
      </div>
    </div>
    <div class="stats">
      <div class="stat"><strong>{balloons.length}</strong><span>balloons</span></div>
      <div class="stat"><strong>{onlineCount}</strong><span>online</span></div>
      <div class="stat"><strong>{gateways.length}</strong><span>gateways</span></div>
      <div class="stat"><strong>{anchors.length}</strong><span>anchors</span></div>
      <div class="stat"><strong>{provisionedGateways}/{gateways.length}</strong><span>provisioned</span></div>
      <div class:good={signalConnected} class:bad={!signalConnected} class="stat link">
        <strong>{signalConnected ? 'LIVE' : 'POLL'}</strong><span>{statusLine}</span>
      </div>
    </div>
    {#if model.canAdmin}
      <button class="sim-toggle" class:on={simulationRunning} onclick={toggleSimulation}>
        {simulationRunning ? 'Demo running' : 'Start demo'}
      </button>
    {/if}

    <div class="user-chip">
      {#if model.userAvatar}
        <img src={model.userAvatar} alt="" />
      {:else}
        <span class="user-avatar">{model.userName.slice(0, 1).toUpperCase()}</span>
      {/if}
      <div class="user-meta">
        <strong>{model.userName}</strong>
        <small>{model.userProvider} · {roleLabel()}</small>
      </div>
      <form method="post" action="/auth/logout">
        <button class="logout" type="submit">Logout</button>
      </form>
    </div>
  </header>

  <aside class="sidebar">
    <section class="panel fleet-panel">
      <div class="panel-title">
        <h2>Fleet</h2>
        <input class="search" bind:value={search} placeholder="Search name or address" />
      </div>
      <div class="balloon-list">
        {#each filteredBalloons as balloon}
          <button class="balloon-row" class:selected={balloon.meshAddress === selectedAddress}
                  onclick={() => selectBalloon(balloon.meshAddress)}>
            <span class="swatch" style={`background:${markerColor(balloon)}`}></span>
            <span class="row-main">
              <strong>{balloon.name}</strong>
              <small>0x{balloon.meshAddress.toString(16).toUpperCase()}</small>
            </span>
            <span class="row-meta">
              <small>{balloon.lat == null ? 'no fix' : `${balloon.lat.toFixed(5)}, ${balloon.lon?.toFixed(5)}`}</small>
              <small class:alert={balloon.estimatedQueueCount >= 401}>{balloon.estimatedQueueCount} queued</small>
            </span>
          </button>
        {/each}
      </div>
    </section>

    {#if selectedBalloon}
      <section class="panel detail-panel">
        <div class="panel-title">
          <h2>{selectedBalloon.name}</h2>
          <span class="pill">0x{selectedBalloon.meshAddress.toString(16).toUpperCase()}</span>
        </div>
        <dl class="detail-grid">
          <div><dt>Position</dt><dd>
            {selectedBalloon.lat == null ? 'not fixed' : `${selectedBalloon.lat.toFixed(6)}, ${selectedBalloon.lon?.toFixed(6)}`}
          </dd></div>
          <div><dt>Confidence</dt><dd>{selectedBalloon.confidence == null ? '—' : `${(selectedBalloon.confidence * 100).toFixed(0)}%`}</dd></div>
          <div><dt>Method</dt><dd>{selectedBalloon.lastMethod ?? '—'}</dd></div>
          <div><dt>Queue</dt><dd>{selectedBalloon.estimatedQueueCount} est. / {selectedBalloon.queueCount} reported</dd></div>
          <div><dt>Colour</dt><dd><span class="swatch inline" style={`background:${markerColor(selectedBalloon)}`}></span></dd></div>
        </dl>

        <div class="button-row">
          <button class:active={pickMode} onclick={() => (pickMode = !pickMode)} disabled={!model.canOperate}>
            {pickMode ? 'Click map to place…' : 'Set initial on map'}
          </button>
          <button onclick={setInitialFromInputs} disabled={!model.canOperate}>Use current point</button>
          <button onclick={toggleTrail}>{trailVisible ? 'Hide trail' : 'Show trail'}</button>
        </div>
      </section>
    {/if}

    <section class="panel command-panel">
      <div class="panel-title">
        <h2>LED control</h2>
        <span class="pill">{busy ? 'sending…' : 'ready'}</span>
      </div>

      <label class="field">
        <span>Target</span>
        <select bind:value={targetMode}>
          <option value="balloon">Selected balloon</option>
          <option value="all">All balloons</option>
        </select>
      </label>

      <div class="colour-row">
        <label class="colour-picker">
          <input type="color" bind:value={colorHex} />
          <span>{colorHex.toUpperCase()}</span>
        </label>
        <div class="presets">
          {#each ['#38bdf8', '#f472b6', '#facc15', '#22c55e', '#ef4444', '#a78bfa', '#ffffff'] as preset}
            <button class="preset" style={`--c:${preset}`} onclick={() => (colorHex = preset)}
                    aria-label={`Use ${preset}`}></button>
          {/each}
        </div>
      </div>

      <label class="field">
        <span>Execute time</span>
        <input type="datetime-local" bind:value={executeLocal} />
      </label>

      <div class="button-grid">
        <button class="primary" onclick={setNow} disabled={!model.canOperate}>LED now</button>
        <button class="primary" onclick={schedule} disabled={!model.canOperate}>Schedule</button>
        <button onclick={cancelAt} disabled={!model.canOperate}>Cancel at time</button>
        <button onclick={() => resetAll(false)} disabled={!model.canOperate}>Reset through now</button>
        <button class="wide" onclick={() => resetAll(true)} disabled={!model.canOperate}>Reset + set colour now</button>
      </div>

      {#if targetMode === 'balloon' && !selectedBalloon}
        <p class="hint">Select a balloon to use per-balloon commands.</p>
      {/if}
    </section>

    {#if model.canAdmin}
      <section class="panel onboarding-panel">
        <div class="panel-title">
          <h2>Onboarding <span class="mock-badge">MOCK</span></h2>
          <button class="mini" onclick={resetMockOnboarding}>Reset</button>
        </div>
        <p class="hint">BLE Mesh Provisioner の UI を模擬しています。Provision は数秒で完了します。</p>
        <ul class="onboarding-list">
          {#each onboarding as device}
            <li>
              <span class="onb-kind">{device.kind === 'balloon' ? 'B' : 'G'}</span>
              <span class="onb-main">
                <strong>{device.name}</strong>
                <small>{device.uuid.slice(0, 8)}… · {device.meshAddress == null ? 'no addr' : `0x${device.meshAddress.toString(16).toUpperCase()}`}</small>
              </span>
              <span class="show-status"
                    class:queued={device.state === 'provisioned'}
                    class:canceled={device.state === 'failed'}
                    class:sent={device.state === 'provisioning'}>{device.state}</span>
              {#if device.state === 'unprovisioned' || device.state === 'failed'}
                <button class="mini" onclick={() => provisionMock(device.id)}>Provision</button>
              {/if}
            </li>
            {#if device.state === 'provisioning'}
              <li class="progress-row">
                <div class="progress"><span style={`width:${device.progress}%`}></span></div>
                <small>{device.progress}%</small>
              </li>
            {/if}
            {#if device.error}
              <li class="error-row">{device.error}</li>
            {/if}
          {/each}
        </ul>
      </section>
    {/if}

    <section class="panel schedule-panel">
      <div class="panel-title">
        <h2>Scheduled</h2>
        <span class="pill">{showSteps.filter((step) => step.status === 'queued').length} queued</span>
      </div>
      {#if showSteps.length === 0}
        <p class="hint">No reservations. Scheduled colours appear here.</p>
      {:else}
        <ul class="show-list">
          {#each showSteps as step}
            <li>
              <span class="swatch" style={`background:rgb(${step.r}, ${step.g}, ${step.b})`}></span>
              <span class="show-main">
                <strong>{step.name}</strong>
                <small>{targetLabel(step)} · {formatDateTime(step.executeAt)}</small>
              </span>
              <span class="show-status" class:queued={step.status === 'queued'}
                    class:sent={step.status === 'sent'}
                    class:canceled={step.status === 'canceled'}
                    class:expired={step.status === 'expired'}>{step.status}</span>
              {#if model.canOperate && (step.status === 'queued' || step.status === 'sent')}
                <button class="mini" onclick={() => cancelShowStep(step.id)}>Cancel</button>
              {/if}
            </li>
          {/each}
        </ul>
      {/if}
    </section>

    <section class="panel log-panel">
      <div class="panel-title"><h2>Activity</h2></div>
      {#if logLines.length === 0}
        <p class="hint">Commands and connection events appear here.</p>
      {:else}
        <ul class="log">
          {#each logLines as line}<li>{line}</li>{/each}
        </ul>
      {/if}
    </section>

    <section class="panel anchor-status-panel">
      <div class="panel-title"><h2>Anchors</h2></div>
      <ul class="anchor-status-list">
        {#each anchors as anchor}
          <li>
            <span class="anchor-dot"></span>
            <strong>A{anchor.anchorId}</strong>
            <small>{anchor.lat.toFixed(5)}, {anchor.lon.toFixed(5)}</small>
            <span class="show-status" class:queued={anchor.timeValid}
                  class:canceled={!anchor.timeValid}>{anchor.timeValid ? 'time' : 'no time'}</span>
          </li>
        {/each}
      </ul>
    </section>

    <section class="panel anchors-panel">
      <div class="panel-title">
        <h2>Gateways</h2>
        <span class="pill">server allocation</span>
      </div>
      <p class="hint gateway-hint">Each Provisioner owns a unique gateway address. Balloon addresses start with 64 slots and grow by 16.</p>
      {#if gateways.length === 0}
        <p class="hint">No gateway connected. Demo mode still aggregates simulated reports.</p>
      {:else}
        <ul class="gateway-list">
          {#each gateways as gateway}
            {@const allocation = meshAllocationFor(gateway.id)}
            <li>
              <div class="gateway-row">
                <span class:good={gateway.webSocket} class:bad={!gateway.webSocket} class="dot"></span>
                <strong>{gateway.id}</strong>
                <span class="show-status" class:queued={gateway.provisioned}
                      class:canceled={!gateway.provisioned}>{gateway.provisioned ? 'prov' : 'unprov'}</span>
                <small>{gateway.webSocket ? 'WS' : 'HTTP'} · {gateway.pendingCommands} cmd · {gateway.spoolPending} spool</small>
              </div>
              {#if allocation}
                <div class="gateway-mesh-summary">
                  <span>Gateway {formatMeshAddress(allocation.meshAddress)}</span>
                  <span>{allocation.ranges.length} range{allocation.ranges.length === 1 ? '' : 's'}</span>
                </div>
                <ul class="gateway-range-list">
                  {#each allocation.ranges as range}
                    <li>
                      <span>{formatMeshAddress(range.rangeStart)}–{formatMeshAddress(range.rangeEnd)}</span>
                      <small>{range.allocatedNodes}/{rangeCapacity(range)} nodes</small>
                    </li>
                  {/each}
                </ul>
              {:else}
                <div class="gateway-mesh-empty">Waiting for server allocation</div>
              {/if}
            </li>
          {/each}
        </ul>
      {/if}
    </section>
  </aside>

  {#if mapError}
    <div class="map-error">Map unavailable: {mapError}</div>
  {/if}
</div>

<style>
  :global(html), :global(body) {
    margin: 0;
    padding: 0;
    height: 100%;
    background: #050b14;
    color: #e2e8f0;
    font-family: "Inter", "Segoe UI", system-ui, sans-serif;
  }
  :global(*) { box-sizing: border-box; }

  .app-shell {
    position: fixed;
    inset: 0;
    overflow: hidden;
  }

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
    gap: 20px;
    padding: 14px 18px;
    border-radius: 18px;
    background: rgba(7, 17, 30, 0.82);
    border: 1px solid rgba(148, 163, 184, 0.18);
    box-shadow: 0 18px 40px rgba(0, 0, 0, 0.35);
    backdrop-filter: blur(16px);
  }

  .brand { display: flex; align-items: center; gap: 12px; min-width: 280px; }
  .brand-mark {
    width: 34px; height: 34px; border-radius: 50%;
    background: conic-gradient(from 210deg, #38bdf8, #a78bfa, #f472b6, #38bdf8);
    box-shadow: 0 0 24px rgba(56, 189, 248, 0.5);
  }
  .brand h1 { margin: 0; font-size: 20px; letter-spacing: 0.02em; }
  .brand p { margin: 2px 0 0; font-size: 12px; color: #94a3b8; }

  .stats { display: flex; gap: 18px; margin-left: auto; align-items: center; }
  .stat { display: flex; flex-direction: column; min-width: 56px; }
  .stat strong { font-size: 17px; line-height: 1.1; }
  .stat span { font-size: 10px; text-transform: uppercase; letter-spacing: 0.08em; color: #94a3b8; }
  .stat.link { min-width: 120px; }
  .stat.link strong { font-size: 12px; letter-spacing: 0.14em; }
  .good { color: #34d399 !important; }
  .bad { color: #f87171 !important; }

  .sim-toggle {
    border: 1px solid rgba(56, 189, 248, 0.4);
    background: rgba(56, 189, 248, 0.12);
    color: #7dd3fc;
    padding: 10px 16px;
    border-radius: 999px;
    font-size: 12px;
    font-weight: 600;
    cursor: pointer;
  }
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
  .user-meta strong { font-size: 11px; max-width: 140px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .user-meta small { font-size: 9px; color: #94a3b8; }
  .logout { padding: 6px 8px; font-size: 9px; }
  .sim-toggle.on {
    border-color: rgba(52, 211, 153, 0.5);
    background: rgba(52, 211, 153, 0.14);
    color: #6ee7b7;
  }

  .sidebar {
    position: absolute;
    top: 92px;
    right: 16px;
    bottom: 16px;
    width: 390px;
    z-index: 15;
    display: flex;
    flex-direction: column;
    gap: 12px;
    overflow-y: auto;
    padding-right: 4px;
    scrollbar-width: thin;
  }

  .panel {
    background: rgba(7, 17, 30, 0.88);
    border: 1px solid rgba(148, 163, 184, 0.16);
    border-radius: 16px;
    padding: 14px;
    backdrop-filter: blur(14px);
  }
  .panel-title { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 10px; }
  .panel-title h2 { margin: 0; font-size: 13px; text-transform: uppercase; letter-spacing: 0.12em; color: #cbd5e1; }

  .search {
    width: 190px;
    padding: 7px 10px;
    border-radius: 9px;
    border: 1px solid rgba(148, 163, 184, 0.22);
    background: rgba(15, 23, 42, 0.8);
    color: #e2e8f0;
    font-size: 12px;
  }
  .search::placeholder { color: #64748b; }

  .new-group { display: flex; gap: 6px; margin-bottom: 8px; }
  .new-group input {
    flex: 1; min-width: 0; padding: 7px 10px; border-radius: 9px;
    border: 1px solid rgba(148, 163, 184, 0.22);
    background: rgba(15, 23, 42, 0.8); color: #e2e8f0; font-size: 12px;
  }
  .new-group button { padding: 7px 10px; white-space: nowrap; }

  .group-move { display: flex; align-items: flex-end; gap: 8px; margin-bottom: 10px; }
  .group-move .field { flex: 1; margin-bottom: 0; }
  .balloon-list { max-height: 230px; overflow-y: auto; display: flex; flex-direction: column; gap: 4px; }
  .balloon-row {
    display: flex; align-items: center; gap: 10px; width: 100%;
    border: 1px solid transparent; background: transparent; color: inherit;
    border-radius: 10px; padding: 7px 8px; cursor: pointer; text-align: left;
  }
  .balloon-row:hover { background: rgba(56, 189, 248, 0.08); }
  .balloon-row.selected { background: rgba(56, 189, 248, 0.14); border-color: rgba(56, 189, 248, 0.35); }
  .swatch {
    width: 12px; height: 12px; border-radius: 50%; flex: 0 0 auto;
    box-shadow: 0 0 10px currentColor;
    border: 1px solid rgba(255, 255, 255, 0.35);
  }
  .swatch.inline { display: inline-block; width: 16px; height: 16px; vertical-align: middle; }
  .row-main { display: flex; flex-direction: column; gap: 1px; min-width: 0; }
  .row-main strong { font-size: 13px; }
  .row-main small, .row-meta small { color: #94a3b8; font-size: 10px; }
  .row-meta { margin-left: auto; text-align: right; display: flex; flex-direction: column; gap: 1px; }
  .row-meta .alert { color: #fbbf24; }

  .pill {
    font-size: 10px; letter-spacing: 0.08em; text-transform: uppercase;
    color: #7dd3fc; border: 1px solid rgba(56, 189, 248, 0.3);
    border-radius: 999px; padding: 3px 8px; background: rgba(56, 189, 248, 0.08);
  }

  .detail-grid {
    display: grid; grid-template-columns: 1fr 1fr; gap: 8px 12px; margin: 8px 0 12px;
  }
  .detail-grid div { min-width: 0; }
  .detail-grid dt { font-size: 9px; text-transform: uppercase; letter-spacing: 0.1em; color: #64748b; }
  .detail-grid dd { margin: 1px 0 0; font-size: 11px; color: #e2e8f0; overflow-wrap: anywhere; }

  .button-row, .button-grid { display: flex; gap: 8px; flex-wrap: wrap; }

  .show-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
  .show-list li { display: flex; align-items: center; gap: 8px; }
  .show-main { display: flex; flex-direction: column; min-width: 0; }
  .show-main strong { font-size: 11px; }
  .show-main small { font-size: 9px; color: #94a3b8; }
  .show-status {
    margin-left: auto; font-size: 8px; text-transform: uppercase;
    letter-spacing: 0.08em; color: #94a3b8; border: 1px solid rgba(148, 163, 184, 0.25);
    border-radius: 999px; padding: 2px 6px;
  }
  .show-status.queued { color: #7dd3fc; border-color: rgba(56, 189, 248, 0.4); }
  .show-status.sent { color: #6ee7b7; border-color: rgba(52, 211, 153, 0.4); }
  .show-status.canceled { color: #fca5a5; border-color: rgba(248, 113, 113, 0.4); }
  .show-status.expired { color: #fbbf24; border-color: rgba(251, 191, 36, 0.4); }
  button.mini { padding: 4px 7px; font-size: 9px; }  .button-grid { display: grid; grid-template-columns: 1fr 1fr; }
  .button-grid .wide { grid-column: 1 / -1; }
  button {
    border: 1px solid rgba(148, 163, 184, 0.25);
    background: rgba(15, 23, 42, 0.75);
    color: #e2e8f0;
    border-radius: 9px;
    padding: 8px 10px;
    font-size: 11px;
    font-weight: 600;
    cursor: pointer;
    transition: border-color 0.15s ease, background 0.15s ease, transform 0.15s ease;
  }
  button:hover { border-color: rgba(56, 189, 248, 0.5); background: rgba(56, 189, 248, 0.12); }
  button:active { transform: translateY(1px); }
  button.primary { background: rgba(56, 189, 248, 0.18); border-color: rgba(56, 189, 248, 0.45); color: #bae6fd; }
  button.active { border-color: rgba(251, 191, 36, 0.6); background: rgba(251, 191, 36, 0.14); color: #fde68a; }

  .field { display: flex; flex-direction: column; gap: 5px; margin-bottom: 10px; }
  .field span { font-size: 10px; text-transform: uppercase; letter-spacing: 0.1em; color: #64748b; }
  .field select, .field input {
    border: 1px solid rgba(148, 163, 184, 0.22);
    background: rgba(15, 23, 42, 0.8);
    color: #e2e8f0;
    border-radius: 9px;
    padding: 8px 10px;
    font-size: 12px;
  }

  .colour-row { display: flex; align-items: center; gap: 12px; margin-bottom: 10px; }
  .colour-picker {
    display: flex; align-items: center; gap: 8px;
    border: 1px solid rgba(148, 163, 184, 0.22);
    border-radius: 10px; padding: 5px 8px; font-size: 11px; color: #94a3b8;
  }
  .colour-picker input { width: 34px; height: 28px; padding: 0; border: none; background: transparent; }
  .presets { display: flex; gap: 6px; flex-wrap: wrap; }
  .preset {
    width: 22px; height: 22px; padding: 0; border-radius: 50%;
    background: var(--c); border: 2px solid rgba(255, 255, 255, 0.25);
  }
  .preset:hover { transform: scale(1.12); }

  .hint { margin: 8px 0 0; font-size: 11px; color: #64748b; }
  .log { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 5px; }
  .log li { font-size: 10px; color: #94a3b8; border-left: 2px solid rgba(56, 189, 248, 0.4); padding-left: 8px; }
  .mock-badge {
    font-size: 8px; padding: 2px 6px; border-radius: 999px;
    color: #fde68a; border: 1px solid rgba(251, 191, 36, 0.45);
    background: rgba(251, 191, 36, 0.12); letter-spacing: 0.1em;
  }
  .onboarding-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
  .onboarding-list li { display: flex; align-items: center; gap: 8px; }
  .onb-kind {
    width: 20px; height: 20px; border-radius: 6px; display: grid; place-items: center;
    font-size: 9px; font-weight: 700; color: #7dd3fc; background: rgba(56, 189, 248, 0.14);
  }
  .onb-main { display: flex; flex-direction: column; min-width: 0; }
  .onb-main strong { font-size: 11px; }
  .onb-main small { font-size: 9px; color: #94a3b8; }
  .progress-row { padding-left: 28px; }
  .progress-row small { font-size: 9px; color: #94a3b8; width: 30px; text-align: right; }
  .progress { flex: 1; height: 4px; border-radius: 999px; background: rgba(148, 163, 184, 0.18); overflow: hidden; }
  .progress span { display: block; height: 100%; background: linear-gradient(90deg, #38bdf8, #a78bfa); transition: width 0.3s ease; }
  .error-row { padding-left: 28px; font-size: 9px; color: #fca5a5; }
  button:disabled { opacity: 0.42; cursor: not-allowed; }
  button:disabled:hover { border-color: rgba(148, 163, 184, 0.25); background: rgba(15, 23, 42, 0.75); }
  .anchor-status-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
  .anchor-status-list li { display: flex; align-items: center; gap: 8px; font-size: 11px; }
  .anchor-status-list small { color: #94a3b8; font-size: 9px; margin-left: auto; }
  .anchor-dot {
    width: 0; height: 0; border-left: 5px solid transparent; border-right: 5px solid transparent;
    border-bottom: 10px solid #a78bfa;
  }
  .gateway-hint { line-height: 1.45; }
  .gateway-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 10px; }
  .gateway-list li { padding: 8px; border-radius: 10px; background: rgba(15, 23, 42, 0.45); }
  .gateway-row { display: flex; align-items: center; gap: 8px; min-width: 0; font-size: 11px; }
  .gateway-row small { margin-left: auto; color: #64748b; font-size: 10px; white-space: nowrap; }
  .gateway-mesh-summary { display: flex; justify-content: space-between; gap: 8px; margin: 7px 0 4px 16px; color: #7dd3fc; font-size: 10px; }
  .gateway-range-list { list-style: none; margin: 0 0 0 16px; padding: 0; display: flex; flex-direction: column; gap: 3px; }
  .gateway-range-list li { display: flex; justify-content: space-between; gap: 8px; padding: 0; background: transparent; color: #cbd5e1; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 10px; }
  .gateway-range-list small { color: #94a3b8; font-family: "Inter", "Segoe UI", system-ui, sans-serif; font-size: 9px; }
  .gateway-mesh-empty { margin: 7px 0 0 16px; color: #64748b; font-size: 10px; }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: #64748b; }
  .dot.good { background: #34d399; }
  .dot.bad { background: #f87171; }

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
    width: 16px; height: 16px; border-radius: 50%;
    border: 2px solid rgba(255, 255, 255, 0.75);
    box-shadow: 0 0 16px rgba(56, 189, 248, 0.45), 0 2px 6px rgba(0, 0, 0, 0.5);
    transition: transform 0.15s ease;
  }
  :global(.balloon-marker .balloon-label) {
    font-size: 9px; font-weight: 700; color: #e2e8f0;
    text-shadow: 0 1px 3px #000, 0 0 4px #000;
    letter-spacing: 0.02em;
  }
  :global(.balloon-marker.selected .balloon-dot) {
    transform: scale(1.5);
    border-color: #facc15;
    box-shadow: 0 0 0 5px rgba(250, 204, 21, 0.18), 0 0 24px rgba(250, 204, 21, 0.7);
  }
  :global(.balloon-marker.offline) { opacity: 0.55; filter: grayscale(0.4); }

  :global(.anchor-marker) {
    display: flex; flex-direction: column; align-items: center; gap: 2px;
    pointer-events: none;
  }
  :global(.anchor-marker span) {
    width: 0; height: 0;
    border-left: 7px solid transparent;
    border-right: 7px solid transparent;
    border-bottom: 14px solid #a78bfa;
    filter: drop-shadow(0 0 6px rgba(167, 139, 250, 0.65));
  }
  :global(.anchor-marker em) {
    font-style: normal; font-size: 8px; color: #ddd6fe;
    text-shadow: 0 1px 3px #000;
  }

  :global(.maplibregl-ctrl-group) {
    background: rgba(7, 17, 30, 0.85) !important;
    border: 1px solid rgba(148, 163, 184, 0.2);
  }
  :global(.maplibregl-ctrl-group button span) { filter: invert(1); }

  @media (max-width: 980px) {
    .topbar { flex-wrap: wrap; }
    .stats { order: 3; width: 100%; margin-left: 0; }
    .sidebar { top: auto; left: 16px; right: 16px; bottom: 16px; width: auto; max-height: 46vh; }
  }
</style>
