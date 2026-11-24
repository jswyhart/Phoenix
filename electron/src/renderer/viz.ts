// Classic music visualizer: fourteen styles and eleven colour schemes drawn on a canvas from the helper's spectrum
// analysis (bars, their peak caps, and the raw wave). The same drawings as the Avalonia build's.

type RGB = [number, number, number];
export interface VizFrame { bars: number[]; peaks: number[]; scope: Float32Array }

const HISTORY_ROWS = 48;
const rgb = (hex: number): RGB => [(hex >> 16) & 255, (hex >> 8) & 255, hex & 255];
const RAMPS: RGB[][] = [
  [rgb(0xd5358f), rgb(0xf0603a), rgb(0xffd070)],   // sunset
  [],                                               // rainbow (per position)
  [rgb(0x1e4fd8), rgb(0x35c6f0), rgb(0xf0fbff)],   // ice
  [rgb(0x0b6b3a), rgb(0x2fd06e), rgb(0xd8ff9a)],   // emerald
  [rgb(0x707070), rgb(0xc8c8c8), rgb(0xffffff)],   // mono
  [rgb(0x9a1010), rgb(0xff6a00), rgb(0xfff080)],   // fire
  [rgb(0x7a1fff), rgb(0x19f0ff), rgb(0xb9ff4a)],   // neon
  [rgb(0x082a6b), rgb(0x109cb8), rgb(0xb8ffe8)],   // ocean
  [rgb(0x3a107a), rgb(0xa040e0), rgb(0xf4b8ff)],   // violet
  [rgb(0x6b4a08), rgb(0xe0a820), rgb(0xfff4b0)],   // gold
  [],                                               // album colours (from the cover)
];
const mix = (a: RGB, b: RGB, t: number): RGB => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
const css = (c: RGB, alpha = 1) => `rgba(${c[0] | 0},${c[1] | 0},${c[2] | 0},${alpha})`;
const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));
const CAP = "rgba(255,255,255,0.88)";

function hsv(h: number, s: number, v: number): RGB {
  h = (((h % 1) + 1) % 1) * 6;
  const i = Math.floor(h), f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
  const [r, g, b] = i === 0 ? [v, t, p] : i === 1 ? [q, v, p] : i === 2 ? [p, v, t] : i === 3 ? [p, q, v] : i === 4 ? [t, p, v] : [v, p, q];
  return [r * 255, g * 255, b * 255];
}

/** The most colourful average colour of a picture, or null if it has none (for the "album colours" scheme). */
export function dominantColor(url: string | null): Promise<RGB | null> {
  return new Promise(resolve => {
    if (!url) { resolve(null); return; }
    const img = new Image();
    img.onerror = () => resolve(null);
    img.onload = () => {
      try {
        const c = document.createElement("canvas");
        c.width = c.height = 24;
        const g = c.getContext("2d", { willReadFrequently: true })!;
        g.drawImage(img, 0, 0, 24, 24);
        const px = g.getImageData(0, 0, 24, 24).data;
        let r = 0, gr = 0, b = 0, total = 0;
        for (let i = 0; i < px.length; i += 4) {
          const R = px[i], G = px[i + 1], B = px[i + 2], max = Math.max(R, G, B), min = Math.min(R, G, B);
          const weight = 0.05 + ((max - min) / 255) * Math.min(1, max / 128);   // favour vivid, not-too-dark pixels
          r += R * weight; gr += G * weight; b += B * weight; total += weight;
        }
        if (total <= 0) { resolve(null); return; }
        let col: RGB = [r / total, gr / total, b / total];
        const peak = Math.max(...col);
        if (peak < 140 && peak > 0) { const k = 170 / peak; col = [Math.min(255, col[0] * k), Math.min(255, col[1] * k), Math.min(255, col[2] * k)]; }
        resolve(col);
      } catch { resolve(null); }
    };
    img.src = url;
  });
}

export class Viz {
  mode = 1;
  palette = 0;
  coverColor: RGB | null = null;
  private g: CanvasRenderingContext2D;
  private bars: number[] = new Array(40).fill(0);
  private peaks: number[] = new Array(40).fill(0);
  private scope: Float32Array = new Float32Array(1024);
  private history: number[][] = [];
  private phase = 0;
  private energy = 0;
  private bassAvg = 0;
  private beat = 0;
  private lastBeat = 0;
  private ripples: { r: number; level: number; hue: number }[] = [];
  private sparks: { x: number; y: number; vx: number; vy: number; life: number; hue: number; size: number }[] = [];
  private needle = [0, 0];
  /** Self-test: the clock used for beat spacing. */
  now: () => number = () => performance.now();

  constructor(readonly canvas: HTMLCanvasElement) {
    this.g = canvas.getContext("2d")!;
  }

  clear() {
    this.history = [];
    this.ripples = [];
    this.sparks = [];
    this.bars.fill(0);
    this.peaks.fill(0);
    this.scope.fill(0);
    this.g.clearRect(0, 0, this.canvas.width, this.canvas.height);
  }

  /** Takes one analysis step from the helper and draws it. */
  feed(f: VizFrame) {
    this.bars = f.bars;
    this.peaks = f.peaks;
    this.scope = f.scope;
    this.energy = this.bars.reduce((a, b) => a + b, 0) / this.bars.length;
    this.phase += 0.02 + this.energy * 0.12;
    this.history.unshift(this.bars.slice());
    if (this.history.length > HISTORY_ROWS) this.history.pop();
    this.animate();
    this.render();
  }

  /** Advances beat detection, ripples, sparks and meter needles by one analysis step. */
  private animate() {
    const bars = this.bars, n = bars.length;
    let bass = 0;
    for (let b = 0; b < 6; b++) bass += bars[b];
    bass /= 6;
    this.bassAvg = this.bassAvg * 0.94 + bass * 0.06;
    this.beat *= 0.86;
    const now = this.now();
    if (bass > 0.3 && bass > this.bassAvg * 1.22 + 0.04 && now - this.lastBeat > 170) {
      this.lastBeat = now;
      this.beat = 1;
      this.ripples.push({ r: 0, level: bass, hue: Math.random() });
      if (this.ripples.length > 8) this.ripples.shift();
      this.burst(bass);
    }
    for (let i = this.ripples.length - 1; i >= 0; i--) {
      this.ripples[i].r += 0.012 + this.energy * 0.03;
      if (this.ripples[i].r > 1) this.ripples.splice(i, 1);
    }
    for (let b = 0; b < n; b += 2)
      if (bars[b] > 0.55 && Math.random() < 0.18 && this.sparks.length < 500)
        this.sparks.push({ x: (b + 0.5) / n, y: 1, vx: (Math.random() - 0.5) * 0.004, vy: -(0.008 + bars[b] * 0.016), life: 1, hue: b / n, size: 1.5 + bars[b] * 2 });
    for (let i = this.sparks.length - 1; i >= 0; i--) {
      const k = this.sparks[i];
      k.x += k.vx; k.y += k.vy; k.vy += 0.00045; k.life -= 0.02;
      if (k.life <= 0) this.sparks.splice(i, 1);
    }
    let low = 0, high = 0;
    const lowEnd = Math.max(1, Math.floor(n / 4)), highStart = Math.floor((n * 5) / 8);
    for (let b = 0; b < lowEnd; b++) low += bars[b];
    for (let b = highStart; b < n; b++) high += bars[b];
    low /= lowEnd; high /= n - highStart;
    this.needle[0] += (Math.min(1, low * 1.15) - this.needle[0]) * 0.35;
    this.needle[1] += (Math.min(1, high * 1.5) - this.needle[1]) * 0.35;
  }

  private burst(level: number) {
    if (this.sparks.length > 420) return;
    const cx = 0.2 + Math.random() * 0.6, cy = 0.15 + Math.random() * 0.4, hue = Math.random(), count = 26 + Math.floor(level * 40);
    for (let i = 0; i < count; i++) {
      const a = (2 * Math.PI * i) / count + Math.random() * 0.2, speed = 0.004 + Math.random() * 0.01 * (0.6 + level);
      this.sparks.push({ x: cx, y: cy, vx: Math.cos(a) * speed, vy: Math.sin(a) * speed, life: 1, hue: (hue + Math.random() * 0.15) % 1, size: 2 + level * 2 });
    }
  }

  // ---------- colour ----------

  private ramp(): RGB[] {
    if (this.palette === 10) {
      const c = this.coverColor ?? rgb(0xe0447c);
      return [mix(c, [0, 0, 0], 0.55), c, mix(c, [255, 255, 255], 0.6)];
    }
    return RAMPS[clamp(this.palette, 0, RAMPS.length - 1)];
  }

  private colorAt(across: number, level: number): RGB {
    if (this.palette === 1) return hsv(across * 0.85 + this.phase * 0.02, 0.85, 0.55 + 0.45 * level);
    const ramp = this.ramp();
    return level < 0.5 ? mix(ramp[0], ramp[1], level * 2) : mix(ramp[1], ramp[2], level * 2 - 1);
  }

  /** Bottom-to-top gradient for one bar, over the strip from y0 (top) to y1 (bottom). */
  private gradient(across: number, y0: number, y1: number): CanvasGradient {
    const gr = this.g.createLinearGradient(0, y1, 0, y0);
    gr.addColorStop(0, css(this.colorAt(across, 0)));
    gr.addColorStop(0.55, css(this.colorAt(across, 0.5)));
    gr.addColorStop(1, css(this.colorAt(across, 1)));
    return gr;
  }

  private across(level: number, w: number): string | CanvasGradient {
    if (this.palette !== 1) return css(this.colorAt(0.5, level));
    const gr = this.g.createLinearGradient(0, 0, w, 0);
    for (let i = 0; i < 7; i++) gr.addColorStop(i / 6, css(this.colorAt(i / 6, level)));
    return gr;
  }

  // ---------- drawing ----------

  render() {
    const canvas = this.canvas, dpr = Math.min(2, window.devicePixelRatio || 1);
    const w = canvas.clientWidth || canvas.width, h = canvas.clientHeight || canvas.height;
    if (w < 10 || h < 10) return;
    if (canvas.clientWidth && (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr))) { canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr); }
    const g = this.g;
    const scale = canvas.clientWidth ? dpr : 1;
    g.setTransform(scale, 0, 0, scale, 0, 0);
    g.clearRect(0, 0, w, h);
    g.lineCap = "round";
    g.lineJoin = "round";
    g.globalAlpha = 1;
    switch (this.mode) {
      case 2: this.drawBars(w, h, true); break;
      case 3: this.drawScope(w, h); break;
      case 4: this.drawRadial(w, h, false); break;
      case 5: this.drawWave(w, h); break;
      case 6: this.drawRadial(w, h, true); break;
      case 7: this.drawWaterfall(w, h); break;
      case 8: this.drawDots(w, h); break;
      case 9: this.drawLissajous(w, h); break;
      case 10: this.drawCircularScope(w, h); break;
      case 11: this.drawRipples(w, h); break;
      case 12: this.drawFireworks(w, h); break;
      case 13: this.drawTunnel(w, h); break;
      case 14: this.drawMeters(w, h); break;
      default: this.drawBars(w, h, false);
    }
  }

  private drawBars(w: number, h: number, mirrored: boolean) {
    const g = this.g, bars = this.bars, peaks = this.peaks, n = bars.length;
    const slot = w / n, barW = Math.max(2, slot - 4), area = mirrored ? h * 0.42 : h * 0.72, baseY = mirrored ? h * 0.5 : h, seg = 7;   // LED-style segments
    const shared = this.palette === 1 ? null : this.gradient(0, baseY - area, baseY);
    for (let b = 0; b < n; b++) {
      const x = b * slot + (slot - barW) / 2, barH = Math.floor((bars[b] * area) / seg) * seg;
      if (barH > 0) {
        g.fillStyle = shared ?? this.gradient(b / n, baseY - area, baseY);
        g.fillRect(x, baseY - barH, barW, barH);
        if (mirrored) {
          g.save();
          g.globalAlpha = 0.45;
          g.translate(0, baseY * 2);
          g.scale(1, -1);
          g.fillRect(x, baseY - barH, barW, barH);
          g.restore();
        }
      }
      g.fillStyle = CAP;
      g.fillRect(x, baseY - Math.max(3, peaks[b] * area) - 3, barW, 3);
    }
  }

  private drawScope(w: number, h: number) {
    const g = this.g, scope = this.scope, points = Math.min(512, scope.length), mid = h * 0.5;
    g.beginPath();
    for (let i = 0; i < points; i++) {
      const x = (w * i) / (points - 1), y = mid - clamp(scope[scope.length - points + i] * 2.2, -1, 1) * h * 0.4;
      if (i === 0) g.moveTo(x, y); else g.lineTo(x, y);
    }
    g.strokeStyle = "rgba(255,255,255,0.25)"; g.lineWidth = 7; g.stroke();   // soft glow
    g.strokeStyle = this.across(0.75, w); g.lineWidth = 2.2; g.stroke();
  }

  private drawRadial(w: number, h: number, starburst: boolean) {
    const g = this.g, bars = this.bars, peaks = this.peaks, n = bars.length, cx = w / 2, cy = h / 2, size = Math.min(w, h);
    const inner = starburst ? size * 0.03 : size * (0.15 + this.energy * 0.05), reach = size * (starburst ? 0.46 : 0.3);
    const spokes = n * 2;   // the spectrum runs up one side and back down the other
    const spin = starburst ? this.phase * 0.15 : -Math.PI / 2;
    for (let i = 0; i < spokes; i++) {
      const b = i < n ? i : spokes - 1 - i, level = bars[b];
      if (level <= 0.01) continue;
      const a = spin + (2 * Math.PI * i) / spokes, cos = Math.cos(a), sin = Math.sin(a);
      g.strokeStyle = css(this.colorAt(b / n, level));
      g.lineWidth = starburst ? 2 + level * 5 : Math.max(2, size * 0.014);
      g.beginPath(); g.moveTo(cx + cos * inner, cy + sin * inner); g.lineTo(cx + cos * (inner + level * reach), cy + sin * (inner + level * reach)); g.stroke();
      if (!starburst) {
        const cap = inner + peaks[b] * reach + 5;
        g.fillStyle = CAP; g.beginPath(); g.arc(cx + cos * cap, cy + sin * cap, 1.6, 0, 2 * Math.PI); g.fill();
      }
    }
    if (!starburst) { g.strokeStyle = css(this.colorAt(0.5, 0.6)); g.lineWidth = 2; g.beginPath(); g.arc(cx, cy, Math.max(1, inner - 4), 0, 2 * Math.PI); g.stroke(); }
  }

  private drawWave(w: number, h: number) {
    // Two filled, smoothed spectrum curves: the live levels in front and the slower peak envelope behind.
    const g = this.g;
    const curve = (levels: number[], scale: number) => {
      const n = levels.length, pts: [number, number][] = [[0, h - levels[0] * scale]];
      for (let b = 0; b < n; b++) pts.push([(w * (b + 0.5)) / n, h - levels[b] * scale]);
      pts.push([w, h - levels[n - 1] * scale]);
      g.beginPath();
      g.moveTo(0, h);
      g.lineTo(pts[0][0], pts[0][1]);
      for (let i = 0; i < pts.length - 1; i++) {
        // Catmull-Rom through the points, as cubic Béziers
        const p0 = pts[Math.max(0, i - 1)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.min(pts.length - 1, i + 2)];
        g.bezierCurveTo(p1[0] + (p2[0] - p0[0]) / 6, p1[1] + (p2[1] - p0[1]) / 6, p2[0] - (p3[0] - p1[0]) / 6, p2[1] - (p3[1] - p1[1]) / 6, p2[0], p2[1]);
      }
      g.lineTo(w, h);
      g.closePath();
      g.fill();
    };
    g.fillStyle = this.palette === 1 ? this.across(0.9, w) : this.gradient(0, h * 0.26, h);
    g.globalAlpha = 0.28; curve(this.peaks, h * 0.74);
    g.globalAlpha = 0.85; curve(this.bars, h * 0.7);
    g.globalAlpha = 1;
  }

  private drawWaterfall(w: number, h: number) {
    // A scrolling spectrogram: each row is a moment in time, newest at the bottom, receding upward.
    const g = this.g, area = h * 0.86, rowH = area / HISTORY_ROWS;
    for (let r = 0; r < this.history.length; r++) {
      const levels = this.history[r], n = levels.length, depth = 1 - r / HISTORY_ROWS;   // 1 = newest
      const inset = (1 - depth) * w * 0.18, slot = (w - inset * 2) / n, y = h - (r + 1) * rowH;   // older rows narrow slightly, like perspective
      for (let b = 0; b < n; b++) {
        const level = levels[b];
        if (level < 0.06) continue;
        g.fillStyle = css(this.colorAt(b / n, level), Math.min(1, level * 1.2) * (0.25 + 0.75 * depth));
        g.fillRect(inset + b * slot, y, Math.max(1, slot - 1), Math.max(1, rowH - 1));
      }
    }
  }

  private dot(x: number, y: number, r: number, fill: string) {
    const g = this.g;
    g.fillStyle = fill; g.beginPath(); g.arc(x, y, Math.max(0.1, r), 0, 2 * Math.PI); g.fill();
  }

  private drawDots(w: number, h: number) {
    // a dot-matrix equalizer: each column is a stack of round lamps
    const rows = 18, n = this.bars.length, slot = w / n, cell = Math.min(slot, (h * 0.78) / rows), r = Math.max(1.5, cell * 0.36), top = h - rows * cell - h * 0.06;
    const dim = css(this.colorAt(0.5, 0.3), 0.086);
    for (let b = 0; b < n; b++) {
      const x = b * slot + slot / 2, lit = Math.round(this.bars[b] * rows), capRow = Math.round(this.peaks[b] * rows);
      for (let row = 0; row < rows; row++) {
        const y = top + (rows - 1 - row) * cell + cell / 2;
        if (row < lit) this.dot(x, y, r, css(this.colorAt(b / n, row / rows)));
        else if (row === capRow - 1 && capRow > 0) this.dot(x, y, r, CAP);
        else this.dot(x, y, r * 0.8, dim);
      }
    }
  }

  private drawLissajous(w: number, h: number) {
    // each sample against the one a moment later: a steady tone draws a clean figure, music a restless knot
    const g = this.g, scope = this.scope, lag = 30, points = Math.min(700, scope.length - lag - 1), base = scope.length - points - lag;
    let peak = 0.05;
    for (let i = 0; i < points; i++) peak = Math.max(peak, Math.abs(scope[base + i]));
    const scale = (Math.min(w, h) * 0.42) / Math.min(peak, 1), cx = w / 2, cy = h / 2;
    g.beginPath();
    for (let i = 0; i < points; i++) {
      const x = scope[base + i], y = scope[base + i + lag];
      const px = cx + (x - y) * 0.7071 * scale * 1.5, py = cy - (x + y) * 0.7071 * scale;
      if (i === 0) g.moveTo(px, py); else g.lineTo(px, py);
    }
    g.strokeStyle = css(this.colorAt(0.5, 0.6), 0.19); g.lineWidth = 8; g.stroke();
    g.strokeStyle = css(this.colorAt(0.25 + this.energy, 1)); g.lineWidth = 2; g.stroke();
  }

  private drawCircularScope(w: number, h: number) {
    const g = this.g, scope = this.scope, size = Math.min(w, h), cx = w / 2, cy = h / 2;
    const radius = size * (0.22 + this.energy * 0.05 + this.beat * 0.015), points = 360, step = Math.max(1, Math.floor(scope.length / (points * 2)));
    g.beginPath();
    for (let i = 0; i <= points; i++) {
      const s = scope[scope.length - 1 - (points - (i % points)) * step];
      const a = (2 * Math.PI * i) / points - Math.PI / 2, r = radius + clamp(s * 2.4, -1, 1) * size * 0.16;
      const px = cx + Math.cos(a) * r, py = cy + Math.sin(a) * r;
      if (i === 0) g.moveTo(px, py); else g.lineTo(px, py);
    }
    g.closePath();
    g.fillStyle = css(this.colorAt(0.5, 0.4), 0.094); g.fill();
    g.strokeStyle = css(this.colorAt(0.5, 0.7), 0.19); g.lineWidth = 9; g.stroke();
    g.strokeStyle = css(this.colorAt(0.35 + ((this.phase * 0.01) % 0.5), 1)); g.lineWidth = 2.2; g.stroke();
    g.strokeStyle = css(this.colorAt(0.8, 0.5), 0.5); g.lineWidth = 1.2; g.beginPath(); g.arc(cx, cy, radius * 0.55, 0, 2 * Math.PI); g.stroke();
  }

  private drawRipples(w: number, h: number) {
    const g = this.g, cx = w / 2, cy = h / 2, reach = Math.sqrt(w * w + h * h) / 2;
    for (const { r, level, hue } of this.ripples) {
      const fade = 1 - r;
      g.strokeStyle = css(this.colorAt(hue, 0.4 + level * 0.6), (220 / 255) * fade * fade);
      g.lineWidth = 2 + level * 8 * fade;
      g.beginPath(); g.arc(cx, cy, r * reach, 0, 2 * Math.PI); g.stroke();
    }
    const core = Math.min(w, h) * (0.05 + this.energy * 0.12 + this.beat * 0.05);
    this.dot(cx, cy, core * 1.7, css(this.colorAt(0.5, 0.8), 0.25));
    this.dot(cx, cy, core, css(this.colorAt(0.5, 1), 0.75));
  }

  private drawFireworks(w: number, h: number) {
    const g = this.g;
    for (const k of this.sparks) {
      const life = clamp(k.life, 0, 1), x = k.x * w, y = k.y * h;
      this.dot(x, y, k.size * 2.6 * life + 1, css(this.colorAt(k.hue, 0.4 + life * 0.6), life * 0.35));
      this.dot(x, y, k.size * life + 0.5, css(this.colorAt(k.hue, 1), life));
    }
    const n = this.bars.length, slot = w / n;
    for (let b = 0; b < n; b++) {
      g.fillStyle = css(this.colorAt(b / n, this.bars[b]), 0.56);
      g.fillRect(b * slot + 1, h - 3 - this.bars[b] * h * 0.1, Math.max(1, slot - 2), 3 + this.bars[b] * h * 0.1);
    }
  }

  private drawTunnel(w: number, h: number) {
    // rings rush towards the viewer, faster when the music is loud, wobbling with the spectrum
    const g = this.g, rings = 16, sides = 72, n = this.bars.length, cx = w / 2, cy = h / 2, reach = (Math.sqrt(w * w + h * h) / 2) * 1.05, drift = (this.phase * 0.7) % 1;
    for (let k = rings - 1; k >= 0; k--) {
      const t = (k + drift) / rings;   // 0 far, 1 near
      const r = Math.pow(t, 2.3) * reach, spin = this.phase * 0.12 * (k % 2 === 0 ? 1 : -1);
      g.beginPath();
      for (let i = 0; i <= sides; i++) {
        const a = (2 * Math.PI * i) / sides + spin, rr = r * (1 + this.bars[Math.floor(((i % sides) * n) / sides)] * 0.12 * t + this.beat * 0.03);
        const px = cx + Math.cos(a) * rr, py = cy + Math.sin(a) * rr * 0.85;
        if (i === 0) g.moveTo(px, py); else g.lineTo(px, py);
      }
      g.closePath();
      g.strokeStyle = css(this.colorAt(t, 0.3 + t * 0.7), (40 + 215 * t) / 255);
      g.lineWidth = 1 + t * 5 + this.energy * 3 * t;
      g.stroke();
    }
  }

  private drawMeters(w: number, h: number) {
    // two analog dials: bass on the left, treble on the right
    const g = this.g, radius = Math.min(w * 0.22, h * 0.36), labels = ["LOW", "HIGH"], from = -150, to = -30;
    for (let m = 0; m < 2; m++) {
      const cx = w * (m === 0 ? 0.27 : 0.73), cy = h * 0.62;
      const at = (deg: number, r: number): [number, number] => { const a = (deg * Math.PI) / 180; return [cx + Math.cos(a) * r, cy + Math.sin(a) * r]; };
      const line = (a: [number, number], b: [number, number], style: string, width: number) => { g.strokeStyle = style; g.lineWidth = width; g.beginPath(); g.moveTo(a[0], a[1]); g.lineTo(b[0], b[1]); g.stroke(); };
      g.beginPath();
      g.moveTo(cx, cy);
      g.arc(cx, cy, radius * 1.12, (from * Math.PI) / 180, (to * Math.PI) / 180);
      g.closePath();
      g.fillStyle = "rgba(255,255,255,0.047)"; g.fill();
      g.strokeStyle = "rgba(255,255,255,0.25)"; g.lineWidth = 1.5; g.stroke();
      for (let t = 0; t <= 20; t++) {
        const frac = t / 20, major = t % 5 === 0, colour = frac > 0.8 ? this.colorAt(0.9, 1) : this.colorAt(0.2, 0.6);
        line(at(from + (to - from) * frac, radius * (major ? 0.82 : 0.9)), at(from + (to - from) * frac, radius), css(colour, major ? 1 : 0.59), major ? 2.4 : 1.2);
      }
      const angle = from + (to - from) * clamp(this.needle[m], 0, 1);
      line(at(angle, 0), at(angle, radius * 0.95), "rgba(0,0,0,0.31)", 6);
      line(at(angle, 0), at(angle, radius * 0.95), css(this.colorAt(0.6, 1)), 3);
      this.dot(cx, cy, radius * 0.06, css(this.colorAt(0.6, 1)));
      g.fillStyle = "rgba(255,255,255,0.69)";
      g.font = `${Math.max(11, radius * 0.1)}px Selawik, sans-serif`;
      g.textAlign = "center";
      g.textBaseline = "top";
      g.fillText(labels[m], cx, cy + radius * 0.14);
    }
  }
}

/** Unpacks the wave the helper sends (one signed byte per sample, base64). */
export function decodeScope(base64: string): Float32Array {
  const raw = atob(base64), out = new Float32Array(raw.length);
  for (let i = 0; i < raw.length; i++) { const v = raw.charCodeAt(i); out[i] = (v > 127 ? v - 256 : v) / 127; }
  return out;
}
