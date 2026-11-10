// Small wrapper around MapLibre used by the effects composer.  Marker colours
// can be overridden by a local preview without mutating the fleet snapshot.
//
// Plain JavaScript syntax: SvelteSharp compileModule() does not strip TS.

export class FleetMap {
  constructor() {
    this.map = null;
    this.maplibregl = null;
    this.markers = new Map();
    this.balloons = new Map();
    this.overlay = new Map();
    this.selected = null;
    this.onSelect = null;
    this.ready = false;
  }

  get isReady() {
    return this.ready;
  }

  async init(containerId, onSelect) {
    this.onSelect = onSelect ?? null;
    await loadStylesheet('https://unpkg.com/maplibre-gl@4.7.1/dist/maplibre-gl.css');
    await loadScript('https://unpkg.com/maplibre-gl@4.7.1/dist/maplibre-gl.js');
    this.maplibregl = window.maplibregl;
    if (!this.maplibregl) {
      throw new Error('MapLibre did not initialise.');
    }

    this.map = new this.maplibregl.Map({
      container: containerId,
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
    this.map.addControl(new this.maplibregl.NavigationControl({ visualizePitch: false }), 'bottom-left');
    this.map.addControl(new this.maplibregl.ScaleControl({ unit: 'metric' }), 'bottom-left');
    this.ready = true;
    this.syncAll();
  }

  setBalloons(balloons, selected) {
    this.selected = selected;
    this.balloons.clear();
    for (const balloon of balloons) {
      this.balloons.set(balloon.meshAddress, balloon);
      this.ensureMarker(balloon);
    }
    for (const address of [...this.markers.keys()]) {
      if (!this.balloons.has(address)) {
        this.markers.get(address).marker.remove();
        this.markers.delete(address);
        this.overlay.delete(address);
      }
    }
    this.syncAll();
  }

  setSelected(address) {
    this.selected = address;
    this.syncAll();
  }

  setEffectColors(colors) {
    this.overlay.clear();
    for (const color of colors) {
      this.overlay.set(color.meshAddress, color);
    }
    this.paintMarkers();
  }

  clearEffectColors() {
    this.overlay.clear();
    this.paintMarkers();
  }

  fit() {
    if (!this.ready || !this.map) {
      return;
    }
    const points = [];
    for (const balloon of this.balloons.values()) {
      if (balloon.lat != null && balloon.lon != null) {
        points.push([balloon.lon, balloon.lat]);
      }
    }
    if (points.length === 0) {
      return;
    }
    const bounds = points.reduce(
      (acc, point) => acc.extend(point),
      new this.maplibregl.LngLatBounds(points[0], points[0])
    );
    this.map.fitBounds(bounds, { padding: 90, maxZoom: 17, duration: 0 });
  }

  dispose() {
    for (const record of this.markers.values()) {
      record.marker.remove();
    }
    this.markers.clear();
    this.ready = false;
    if (this.map) {
      this.map.remove();
      this.map = null;
    }
  }

  ensureMarker(balloon) {
    if (this.markers.has(balloon.meshAddress)) {
      return;
    }
    const element = document.createElement('div');
    element.className = 'balloon-marker';
    element.innerHTML = '<span class="balloon-dot"></span><span class="balloon-label"></span>';
    element.addEventListener('click', (event) => {
      event.stopPropagation();
      if (this.onSelect) {
        this.onSelect(balloon.meshAddress);
      }
    });
    const dot = element.querySelector('.balloon-dot');
    const label = element.querySelector('.balloon-label');
    label.textContent = balloon.name.replace(/^B-/, '');
    const marker = new this.maplibregl.Marker({ element, anchor: 'center' });
    this.markers.set(balloon.meshAddress, { marker, element, dot, label });
  }

  syncAll() {
    if (!this.ready || !this.map) {
      return;
    }
    for (const balloon of this.balloons.values()) {
      const record = this.markers.get(balloon.meshAddress);
      if (!record) {
        continue;
      }
      if (balloon.lat != null && balloon.lon != null) {
        record.marker.setLngLat([balloon.lon, balloon.lat]);
        if (!record.marker.getElement().isConnected) {
          record.marker.addTo(this.map);
        }
      } else {
        record.marker.remove();
      }
      record.label.textContent = balloon.name.replace(/^B-/, '');
      record.element.classList.toggle('selected', balloon.meshAddress === this.selected);
      record.element.classList.toggle('offline', balloon.online === false);
      record.element.title =
        `${balloon.name} • 0x${balloon.meshAddress.toString(16).toUpperCase().padStart(4, '0')}`;
    }
    this.paintMarkers();
  }

  paintMarkers() {
    for (const balloon of this.balloons.values()) {
      const record = this.markers.get(balloon.meshAddress);
      if (!record) {
        continue;
      }
      const color = this.overlay.get(balloon.meshAddress) ?? balloon;
      const lit = Math.max(color.r, color.g, color.b) >= 8;
      record.dot.style.background = lit
        ? `rgb(${color.r}, ${color.g}, ${color.b})`
        : '#64748b';
      record.dot.style.setProperty(
        '--glow',
        lit ? `rgba(${color.r}, ${color.g}, ${color.b}, 0.7)` : 'rgba(56, 189, 248, 0.35)'
      );
    }
  }
}

let maplibreLoading = null;

async function loadScript(src) {
  if (document.querySelector(`script[src="${src}"]`)) {
    return;
  }
  maplibreLoading ??= new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = src;
    script.async = true;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error(`Failed to load ${src}`));
    document.head.appendChild(script);
  });
  await maplibreLoading;
}

async function loadStylesheet(href) {
  if (document.querySelector(`link[href="${href}"]`)) {
    return;
  }
  await new Promise((resolve, reject) => {
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = href;
    link.onload = () => resolve();
    link.onerror = () => reject(new Error(`Failed to load ${href}`));
    document.head.appendChild(link);
  });
}
