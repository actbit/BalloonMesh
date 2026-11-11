// Shared effect model for the default composer screen.  The same formulas are
// implemented in EffectService.cs so the browser preview matches the hardware.
//
// This file uses plain JavaScript syntax because SvelteSharp calls
// compileModule(), which does not strip TypeScript annotations.  The .svelte.ts
// extension is required so the build pipeline treats it as a local module.

/** @typedef {'flow'|'wave'|'pulse'|'rainbow'|'twinkle'|'alternate'|'solid'} EffectKind */

/**
 * @typedef {object} EffectParams
 * @property {EffectKind} kind
 * @property {number|null} origin
 * @property {number} speed
 * @property {number} wavelength
 * @property {number} width
 * @property {number} directionDeg
 * @property {number} r
 * @property {number} g
 * @property {number} b
 * @property {number} r2
 * @property {number} g2
 * @property {number} b2
 * @property {number} durationSeconds
 * @property {number} frameIntervalMs
 */

export const effectPresets = [
  {
    id: 'flow',
    glyph: 'FLOW',
    label: '流れ',
    english: 'Flow from node',
    description: '選んだノードから光の帯が外へ流れます。起点を変えると波紋の中心も動きます。',
    speed: 55,
    wavelength: 110,
    width: 0.5,
    directionDeg: 0
  },
  {
    id: 'wave',
    glyph: 'WAVE',
    label: '波',
    english: 'Wave sweep',
    description: '面状の光の波が指定した方向へ押し寄せます。角度と波長で見え方が変わります。',
    speed: 45,
    wavelength: 150,
    width: 0.35,
    directionDeg: 90
  },
  {
    id: 'pulse',
    glyph: 'PULSE',
    label: '鼓動',
    english: 'Synchronised pulse',
    description: '全ノードが同じリズムで明滅します。周期は速度と波長から決まります。',
    speed: 1,
    wavelength: 3,
    width: 1,
    directionDeg: 0
  },
  {
    id: 'rainbow',
    glyph: 'HUE',
    label: '虹',
    english: 'Rainbow drift',
    description: '色相がノード間を流れます。速度と波長で色の巡回を調整します。',
    speed: 25,
    wavelength: 240,
    width: 1,
    directionDeg: 0
  },
  {
    id: 'twinkle', glyph: 'STAR', label: 'きらめき', english: 'Twinkle',
    description: 'ノードごとに異なるタイミングで光が明滅します。',
    speed: 1, wavelength: 6, width: 1, directionDeg: 0
  },
  {
    id: 'alternate', glyph: 'ALT', label: '交互', english: 'Alternating',
    description: 'ノードを2組に分け、交互に明るくします。',
    speed: 1, wavelength: 6, width: 1, directionDeg: 0
  },
  {
    id: 'solid', glyph: 'ON', label: '点灯', english: 'Steady',
    description: 'すべてのノードを選んだ色で点灯します。',
    speed: 1, wavelength: 10, width: 1, directionDeg: 0
  }
];

export function presetById(kind) {
  return effectPresets.find((preset) => preset.id === kind) ?? effectPresets[0];
}

export function defaultParams(kind = 'flow') {
  const preset = presetById(kind);
  return {
    kind: preset.id,
    origin: null,
    speed: preset.speed,
    wavelength: preset.wavelength,
    width: preset.width,
    directionDeg: preset.directionDeg,
    r: 56,
    g: 189,
    b: 248,
    r2: 0,
    g2: 0,
    b2: 0,
    durationSeconds: 0,
    frameIntervalMs: 250
  };
}

export function applyPreset(params, kind) {
  const preset = presetById(kind);
  return {
    ...params,
    kind: preset.id,
    speed: preset.speed,
    wavelength: preset.wavelength,
    width: preset.width,
    directionDeg: preset.directionDeg
  };
}

export function renderEffectColors(balloons, params, elapsedSeconds, origin) {
  const positioned = balloons.filter(
    (balloon) => balloon.lat != null && balloon.lon != null
  );
  if (positioned.length === 0) {
    return [];
  }

  const center = centerOf(positioned);
  const originPoint = origin && origin.lat != null && origin.lon != null ? origin : null;
  const originLat = originPoint ? originPoint.lat : center.lat;
  const originLon = originPoint ? originPoint.lon : center.lon;
  const elapsed = Math.max(0, elapsedSeconds);

  return positioned.map((balloon) => {
    const lat = balloon.lat;
    const lon = balloon.lon;
    if (params.kind === 'flow') {
      const distance = distanceMeters(originLat, originLon, lat, lon);
      const phase = distance / params.wavelength - (elapsed * params.speed) / params.wavelength;
      return colorFor(balloon.meshAddress, params, trail(phase, params.width));
    }
    if (params.kind === 'wave') {
      const offset = offsetMeters(center.lat, center.lon, lat, lon);
      const radians = (params.directionDeg * Math.PI) / 180;
      const projection = offset[0] * Math.cos(radians) + offset[1] * Math.sin(radians);
      const phase = projection / params.wavelength - (elapsed * params.speed) / params.wavelength;
      return colorFor(balloon.meshAddress, params, trail(phase, params.width));
    }
    if (params.kind === 'pulse') {
      const period = Math.max(0.2, params.wavelength / Math.max(0.5, params.speed));
      const value = 0.5 - 0.5 * Math.cos((2 * Math.PI * elapsed) / period);
      return colorFor(balloon.meshAddress, params, value);
    }
    if (params.kind === 'solid') {
      return colorFor(balloon.meshAddress, params, 1);
    }
    if (params.kind === 'twinkle' || params.kind === 'alternate') {
      const period = params.wavelength / Math.max(0.5, params.speed);
      const offset = params.kind === 'twinkle'
        ? ((balloon.meshAddress * 37) % 97) / 97
        : (balloon.meshAddress % 2) / 2;
      const value = 0.5 + 0.5 * Math.cos(2 * Math.PI * (elapsed / period + offset));
      return colorFor(balloon.meshAddress, params, value);
    }
    const distance = distanceMeters(originLat, originLon, lat, lon);
    let hue = (elapsed * params.speed) / params.wavelength + distance / params.wavelength;
    hue -= Math.floor(hue);
    const rgb = hsvToRgb(hue, 0.9, 1);
    return {
      meshAddress: balloon.meshAddress,
      r: Math.round(rgb[0] * 0.85),
      g: Math.round(rgb[1] * 0.85),
      b: Math.round(rgb[2] * 0.85)
    };
  });
}

export function colorFor(meshAddress, params, value) {
  const amount = Math.max(0, Math.min(1, value));
  return {
    meshAddress,
    r: Math.round((params.r2 + (params.r - params.r2) * amount) * amount),
    g: Math.round((params.g2 + (params.g - params.g2) * amount) * amount),
    b: Math.round((params.b2 + (params.b - params.b2) * amount) * amount)
  };
}

function trail(phase, width) {
  const fraction = phase - Math.floor(phase);
  if (fraction >= width) {
    return 0;
  }
  const x = 1 - fraction / width;
  return x * x * (3 - 2 * x);
}

function centerOf(balloons) {
  let lat = 0;
  let lon = 0;
  for (const balloon of balloons) {
    lat += balloon.lat;
    lon += balloon.lon;
  }
  return { lat: lat / balloons.length, lon: lon / balloons.length };
}

export function distanceMeters(lat1, lon1, lat2, lon2) {
  const offset = offsetMeters(lat1, lon1, lat2, lon2);
  return Math.sqrt(offset[0] * offset[0] + offset[1] * offset[1]);
}

function offsetMeters(lat1, lon1, lat2, lon2) {
  const meanLat = ((lat1 + lat2) * 0.5 * Math.PI) / 180;
  return [(lon2 - lon1) * 111320 * Math.cos(meanLat), (lat2 - lat1) * 111320];
}

function hsvToRgb(hue, saturation, value) {
  const sector = hue * 6;
  const index = Math.floor(sector) % 6;
  const fraction = sector - Math.floor(sector);
  const p = value * (1 - saturation);
  const q = value * (1 - saturation * fraction);
  const t = value * (1 - saturation * (1 - fraction));
  const rgb =
    index === 0 ? [value, t, p] :
    index === 1 ? [q, value, p] :
    index === 2 ? [p, value, t] :
    index === 3 ? [p, q, value] :
    index === 4 ? [t, p, value] :
    [value, p, q];
  return [rgb[0] * 255, rgb[1] * 255, rgb[2] * 255];
}

export function hexToRgb(hex) {
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

export function rgbToHex(r, g, b) {
  const part = (value) => Math.max(0, Math.min(255, value)).toString(16).padStart(2, '0');
  return `#${part(r)}${part(g)}${part(b)}`;
}

export function formatAddress(address) {
  return address == null ? '—' : `0x${address.toString(16).toUpperCase().padStart(4, '0')}`;
}

export function rgbCss(color) {
  return `rgb(${color.r}, ${color.g}, ${color.b})`;
}
