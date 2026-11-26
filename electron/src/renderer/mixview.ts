// Mixview, as on Windows: one album or artist in the middle, with the artist's other albums and related artists from
// your collection drifting around it. Click one and it moves to the middle with its own surroundings; double-click
// plays it; right-click for its menu.
import { call } from "./api";
import { Page } from "./shell";
import { albumMenu, artistMenu } from "./songs";
import { art, h, showMenu } from "./ui";

interface NodeData { key: string; title: string; sub: string; art: string; artist: string; albumKey: string | null }
interface Tile { data: NodeData; el: HTMLDivElement; line: SVGLineElement; x: number; y: number; size: number; alpha: number; tx: number; ty: number; tsize: number; talpha: number; phase: number; leaving: boolean }

export interface MixviewPage extends Page { show(albumKey: string | null, artist: string): Promise<void>; centre(): NodeData | null; around(): NodeData[]; step(dt: number): void }

export function mixviewPage(): MixviewPage {
  const ns = "http://www.w3.org/2000/svg";
  const lines = document.createElementNS(ns, "svg");
  lines.classList.add("mixlines");
  const stage = h("div.mixstage", lines);
  const hint = h("div.faint", { style: { position: "absolute", left: "28px", bottom: "14px", fontSize: "12px", pointerEvents: "none" } },
    "click a cover to move it to the middle  ·  double-click to play  ·  right-click for more");
  const el = h("div.page", { style: { position: "relative", padding: "0" } },
    h("div.zunehead", { style: { position: "absolute", left: "28px", top: "8px", pointerEvents: "none", zIndex: "1" } }, "mixview"), stage, hint);
  let tiles: Tile[] = [], centre: Tile | null = null, around: Tile[] = [];
  let running = false, last = 0, time = 0;

  function make(d: NodeData, ox: number, oy: number): Tile {
    const img = art(d.art, 360);
    const tileEl = h("div.mixtile", h("div.mixart", img, d.art ? null : h("div.mixglyph", d.albumKey ? "♪" : "☺")), h("div.mixtitle.ell", d.title), h("div.mixsub.ell", d.sub));
    const line = document.createElementNS(ns, "line");
    lines.append(line);
    const t: Tile = { data: d, el: tileEl, line, x: ox, y: oy, size: 40, alpha: 0, tx: ox, ty: oy, tsize: 40, talpha: 1, phase: Math.random() * 20, leaving: false };
    tileEl.addEventListener("click", () => { if (t === centre) void play(t.data); else void show(t.data.albumKey, t.data.artist); });
    tileEl.addEventListener("dblclick", () => void play(t.data));
    tileEl.addEventListener("contextmenu", async e => {
      e.preventDefault();
      const x = e.clientX, y = e.clientY;
      showMenu(t.data.albumKey ? await albumMenu(t.data.albumKey) : await artistMenu(t.data.artist), x, y);
    });
    stage.append(tileEl);
    return t;
  }

  const play = (d: NodeData) => call("mix.play", { key: d.albumKey, artist: d.artist });

  async function show(albumKey: string | null, artist: string) {
    const built = await call<{ centre: NodeData; around: NodeData[] }>("mix.build", { key: albumKey, artist });
    const old = new Map(tiles.filter(t => !t.leaving).map(t => [t.data.key, t]));
    const ox = centre ? centre.x : stage.clientWidth / 2, oy = centre ? centre.y : stage.clientHeight / 2;
    // something already on screen keeps its place and glides to the new one
    const take = (d: NodeData) => {
      const was = old.get(d.key);
      if (was) { old.delete(d.key); was.data = d; return was; }
      const t = make(d, ox, oy);
      tiles.push(t);
      return t;
    };
    centre = take(built.centre);
    around = built.around.map(take);
    for (const gone of old.values()) { gone.leaving = true; gone.talpha = 0; }
    // the middle one is drawn on top
    stage.append(centre.el);
    arrange();
    start();
  }

  function arrange() {
    if (!centre) return;
    const w = Math.max(300, stage.clientWidth), hgt = Math.max(300, stage.clientHeight);
    const unit = Math.min(132, Math.max(60, Math.min(w / 9.5, hgt / 5.6)));
    centre.tx = w / 2; centre.ty = hgt * 0.45; centre.tsize = unit * 1.75; centre.talpha = 1;
    const count = around.length;
    around.forEach((n, i) => {
      // around an ellipse, every other one a little further in (nothing sits straight below the middle: its name is there)
      const gap = Math.PI * 0.42;
      const angle = Math.PI / 2 + gap / 2 + ((i + 0.5) * (2 * Math.PI - gap)) / count;
      const reach = count <= 6 ? 0.86 : i % 2 === 0 ? 1.0 : 0.66;
      n.tx = w / 2 + Math.cos(angle) * (w / 2 - unit * 0.95) * reach;
      n.ty = hgt * 0.45 + Math.sin(angle) * (hgt * 0.45 - unit * 0.78) * reach;
      n.tsize = n.data.albumKey ? unit : unit * 0.86;
      n.talpha = 1;
    });
  }

  /** Moves everything on by a length of time (also used by the self-test). */
  function step(dt: number) {
    time += dt;
    const ease = 1 - Math.exp(-dt / 0.16), fade = 1 - Math.exp(-dt / 0.22);
    for (let i = tiles.length - 1; i >= 0; i--) {
      const t = tiles[i];
      t.x += (t.tx - t.x) * ease; t.y += (t.ty - t.y) * ease;
      t.size += (t.tsize - t.size) * ease;
      t.alpha += (t.talpha - t.alpha) * fade;
      if (t.leaving && t.alpha < 0.02) { t.el.remove(); t.line.remove(); tiles.splice(i, 1); }
    }
    draw();
  }

  function draw() {
    const at = (t: Tile) => (t === centre ? [t.x, t.y] : [t.x + Math.sin(time * 0.42 + t.phase) * 7, t.y + Math.cos(time * 0.33 + t.phase * 1.7) * 6]);
    const [cx, cy] = centre ? at(centre) : [0, 0];
    for (const t of tiles) {
      const [x, y] = at(t);
      const big = t === centre;
      t.el.style.transform = `translate(${x - t.size / 2}px, ${y - t.size / 2}px)`;
      t.el.style.width = t.size + "px";
      (t.el.firstElementChild as HTMLElement).style.height = t.size + "px";
      t.el.style.opacity = String(Math.max(0, Math.min(1, t.alpha)));
      t.el.classList.toggle("centre", big);
      t.line.setAttribute("x1", String(cx)); t.line.setAttribute("y1", String(cy));
      t.line.setAttribute("x2", String(x)); t.line.setAttribute("y2", String(y));
      t.line.style.opacity = big ? "0" : String(0.28 * Math.max(0, Math.min(1, t.alpha)));
    }
  }

  function frame(now: number) {
    if (!running) return;
    const dt = last === 0 ? 0.016 : Math.min(0.1, Math.max(0.0005, (now - last) / 1000));
    last = now;
    step(dt);
    if (!el.isConnected) { running = false; return; }
    requestAnimationFrame(frame);
  }

  function start() {
    if (running) return;
    running = true;
    last = 0;
    requestAnimationFrame(frame);
  }

  new ResizeObserver(() => arrange()).observe(stage);
  return {
    el, shown: () => start(), show, step,
    centre: () => centre?.data ?? null, around: () => around.map(t => t.data),
  };
}
