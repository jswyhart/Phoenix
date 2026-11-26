// Small building blocks for the screens: elements, icons, artwork, long lists, tile grids, menus, dialogs, sliders.

type Child = Node | string | null | undefined | false;

/** Makes an element: h("div.cap", "SONGS") or h("button.pill", { onclick }, "ok"). */
export function h<K extends keyof HTMLElementTagNameMap>(spec: K | `${K}.${string}`, ...rest: (Child | Record<string, any>)[]): HTMLElementTagNameMap[K] {
  const [tag, ...classes] = (spec as string).split(".");
  const el = document.createElement(tag) as HTMLElementTagNameMap[K];
  if (classes.length) el.className = classes.join(" ");
  for (const r of rest) {
    if (r == null || r === false) continue;
    if (typeof r === "string") el.append(r);
    else if (r instanceof Node) el.append(r);
    else for (const [k, v] of Object.entries(r)) {
      if (v == null) continue;
      if (k === "style") Object.assign(el.style, v);
      else if (k === "data") Object.assign(el.dataset, v);
      else (el as any)[k] = v;
    }
  }
  return el;
}

// line icons in a 16 x 16 box (the same drawings as the Avalonia build)
const ICONS: Record<string, string> = {
  back: "M10,3 L5,8 L10,13 M5,8 L14,8",
  search: "M2,6.5 A4.5,4.5 0 1 0 11,6.5 A4.5,4.5 0 1 0 2,6.5 M9.8,9.8 L14,14",
  play: "M4.5,2 L13,8 L4.5,14 Z",
  pause: "M3.5,2 L6.5,2 L6.5,14 L3.5,14 Z M9.5,2 L12.5,2 L12.5,14 L9.5,14 Z",
  prev: "M3,2 L3,14 M13,2.5 L5,8 L13,13.5 Z",
  next: "M13,2 L13,14 M3,2.5 L11,8 L3,13.5 Z",
  shuffle: "M1,4.5 L4,4.5 C8,4.5 8,11.5 12,11.5 L15,11.5 M13,9.5 L15,11.5 L13,13.5 M1,11.5 L4,11.5 C8,11.5 8,4.5 12,4.5 L15,4.5 M13,2.5 L15,4.5 L13,6.5",
  repeat: "M2.5,9.5 L2.5,5 L13,5 M11,3 L13,5 L11,7 M13.5,6.5 L13.5,11 L3,11 M5,9 L3,11 L5,13",
  repeatOne: "M2.5,9.5 L2.5,5 L13,5 M11,3 L13,5 L11,7 M13.5,6.5 L13.5,11 L3,11 M5,9 L3,11 L5,13 M7.5,7 L8.5,6.4 L8.5,9.6",
  heart: "M8,14 C3,10.2 1,8 1,5.5 C1,3 3,1.5 5,1.5 C6.5,1.5 7.5,2.5 8,3.6 C8.5,2.5 9.5,1.5 11,1.5 C13,1.5 15,3 15,5.5 C15,8 13,10.2 8,14 Z",
  volume: "M1.5,6 L4.5,6 L8.5,2.5 L8.5,13.5 L4.5,10 L1.5,10 Z M11,5.5 C12.3,6.8 12.3,9.2 11,10.5 M13,3.5 C15.3,5.8 15.3,10.2 13,12.5",
  mute: "M1.5,6 L4.5,6 L8.5,2.5 L8.5,13.5 L4.5,10 L1.5,10 Z M11,6 L15,10 M15,6 L11,10",
  visualizer: "M2,14 L2,9 M5,14 L5,4 M8,14 L8,7 M11,14 L11,2 M14,14 L14,10",
  full: "M2,6 L2,2 L6,2 M10,2 L14,2 L14,6 M14,10 L14,14 L10,14 M6,14 L2,14 L2,10",
  bookmark: "M8,1.5 L10,6 L15,6.3 L11.2,9.5 L12.4,14.5 L8,11.8 L3.6,14.5 L4.8,9.5 L1,6.3 L6,6 Z",
  playlist: "M3,1.5 L13,1.5 L13,14.5 L3,14.5 Z M5.5,5 L10.5,5 M5.5,8 L10.5,8 M5.5,11 L10.5,11",
  note: "M6,12 A2,2 0 1 1 6,11.99 M8,12 L8,2.5 L13,1.5 L13,4 L8,5",
  plus: "M8,3 L8,13 M3,8 L13,8",
  forward: "M6,3 L11,8 L6,13 M11,8 L2,8",
  reload: "M13,8 A5,5 0 1 1 11.5,4.5 M11.8,1.5 L11.8,4.8 L8.5,4.8",
  home: "M2,8 L8,2.5 L14,8 M4,6.5 L4,14 L12,14 L12,6.5",
  more: "M3,8 L3.01,8 M8,8 L8.01,8 M13,8 L13.01,8",
  up: "M8,13 L8,3 M4,7 L8,3 L12,7",
  down: "M8,3 L8,13 M4,9 L8,13 L12,9",
  radio: "M2,6 L14,6 L14,14 L2,14 Z M4,6 L12,2 M10,10 A1.6,1.6 0 1 0 10,9.99 M4,9 L7,9 M4,11 L7,11",
  phone: "M5.2,1.5 L10.8,1.5 Q12,1.5 12,2.7 L12,13.3 Q12,14.5 10.8,14.5 L5.2,14.5 Q4,14.5 4,13.3 L4,2.7 Q4,1.5 5.2,1.5 Z M7.2,12.4 L8.8,12.4",
};

export function icon(name: string, size = 16, height = size): SVGSVGElement {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", "0 0 16 16");
  svg.setAttribute("width", String(size));
  svg.setAttribute("height", String(height));
  if (height !== size) svg.setAttribute("preserveAspectRatio", "none");
  const p = document.createElementNS(ns, "path");
  p.setAttribute("d", ICONS[name] ?? "");
  svg.append(p);
  return svg;
}

export function setIcon(button: HTMLElement, name: string) {
  const svg = button.querySelector("svg");
  svg?.querySelector("path")?.setAttribute("d", ICONS[name] ?? "");
}

export function iconBtn(name: string, tip: string, onclick: () => void, cls = "", size = 16): HTMLButtonElement {
  return h(`button.iconbtn${cls ? "." + cls.split(" ").join(".") : ""}` as "button", { title: tip, onclick, tabIndex: -1 }, icon(name, size));
}
export const textBtn = (label: string, onclick: () => void, cls = "") => h(`button.textbtn${cls ? "." + cls : ""}` as "button", { onclick, tabIndex: -1 }, label);
export const pill = (label: string, onclick: () => void) => h("button.pill", { onclick }, label);

// artwork changes on disk (a new cover) are picked up by changing this number in every address
let artEpoch = 0;
export const bumpArt = () => { artEpoch++; };

/** The address of a picture, scaled to about <size> CSS pixels wide (0 = as it is). */
export function artUrl(path: string, size: number): string {
  const px = size === 0 ? 0 : Math.round(size * Math.min(2, window.devicePixelRatio || 1));
  return `phoenix://app/_art?p=${encodeURIComponent(path)}&s=${px}&v=${artEpoch}`;
}

/** A picture that stays out of sight when there is nothing to show. */
export function art(path: string | null | undefined, size = 300): HTMLImageElement {
  const img = h("img", { loading: "lazy", draggable: false, alt: "" });
  setArt(img, path, size);
  return img;
}

export function setArt(img: HTMLImageElement, path: string | null | undefined, size = 300) {
  img.onerror = () => img.classList.add("art-missing");
  img.onload = () => img.classList.remove("art-missing");
  if (!path) { img.removeAttribute("src"); img.classList.add("art-missing"); return; }
  img.classList.remove("art-missing");
  img.src = artUrl(path, size);
}

// ---------- long lists ----------

export interface VListOptions<T> {
  rowHeight: number;
  /** Builds one row's element; the list positions it. */
  render: (item: T, index: number) => HTMLElement;
  multi?: boolean;
  plain?: boolean;
  boxed?: boolean;
  onSelect?: (items: T[]) => void;
  onActivate?: (item: T, index: number) => void;
  onContext?: (items: T[], e: MouseEvent) => void;
}

/** A list that only builds the rows in view, so fifty thousand songs scroll like fifty. */
export class VList<T> {
  readonly el: HTMLDivElement;
  items: T[] = [];
  private spacer: HTMLDivElement;
  private rows = new Map<number, HTMLElement>();
  private sel = new Set<number>();
  private anchor = -1;
  rowHeight: number;

  constructor(private opts: VListOptions<T>) {
    this.rowHeight = opts.rowHeight;
    this.spacer = h("div.spacer");
    this.el = h("div.vlist", this.spacer);
    if (opts.plain) this.el.classList.add("plain");
    if (opts.boxed) this.el.classList.add("boxed");
    this.el.addEventListener("scroll", () => this.draw(), { passive: true });
    new ResizeObserver(() => this.draw()).observe(this.el);
    this.spacer.addEventListener("mousedown", e => this.pointer(e));
    this.spacer.addEventListener("dblclick", e => {
      const i = this.indexAt(e);
      if (i >= 0 && !(e.target as HTMLElement).closest("[data-noactivate]")) opts.onActivate?.(this.items[i], i);
    });
    this.spacer.addEventListener("contextmenu", e => {
      const i = this.indexAt(e);
      if (i < 0) return;
      e.preventDefault();
      if (!this.sel.has(i)) this.select(i);
      opts.onContext?.(this.selected(), e);
    });
  }

  private indexAt(e: Event): number {
    const row = (e.target as HTMLElement).closest<HTMLElement>(".row");
    return row && row.parentElement === this.spacer ? Number(row.dataset.i) : -1;
  }

  private pointer(e: MouseEvent) {
    if (e.button !== 0) return;
    const i = this.indexAt(e);
    if (i < 0 || (e.target as HTMLElement).closest("[data-noselect]")) return;
    const multi = this.opts.multi;
    if (multi && e.shiftKey && this.anchor >= 0) {
      this.sel.clear();
      for (let k = Math.min(i, this.anchor); k <= Math.max(i, this.anchor); k++) this.sel.add(k);
    } else if (multi && (e.ctrlKey || e.metaKey)) {
      if (this.sel.has(i)) this.sel.delete(i); else this.sel.add(i);
      this.anchor = i;
    } else {
      this.sel.clear();
      this.sel.add(i);
      this.anchor = i;
    }
    this.paintSelection();
    this.opts.onSelect?.(this.selected());
  }

  private paintSelection() {
    for (const [i, row] of this.rows) row.classList.toggle("sel", this.sel.has(i));
  }

  selected(): T[] { return [...this.sel].sort((a, b) => a - b).map(i => this.items[i]).filter(x => x !== undefined); }
  selectedIndex(): number { return this.sel.size ? Math.min(...this.sel) : -1; }

  /** Chooses one row without telling onSelect (the caller already knows). */
  select(index: number | null, reveal = false) {
    this.sel.clear();
    if (index != null && index >= 0 && index < this.items.length) { this.sel.add(index); this.anchor = index; if (reveal) this.reveal(index); }
    this.paintSelection();
  }

  reveal(index: number) {
    const top = index * this.rowHeight, view = this.el.clientHeight;
    if (top < this.el.scrollTop) this.el.scrollTop = top;
    else if (top + this.rowHeight > this.el.scrollTop + view) this.el.scrollTop = top + this.rowHeight - view;
  }

  setItems(items: T[], keep = false) {
    const top = this.el.scrollTop;
    this.items = items;
    if (!keep) { this.sel.clear(); this.anchor = -1; }
    else for (const i of [...this.sel]) if (i >= items.length) this.sel.delete(i);
    this.spacer.style.height = items.length * this.rowHeight + "px";
    this.el.scrollTop = keep ? top : 0;
    this.redraw();
  }

  /** Builds the rows in view again (their content changed). */
  redraw() {
    for (const row of this.rows.values()) row.remove();
    this.rows.clear();
    this.draw();
  }

  private draw() {
    const view = this.el.clientHeight || 600;
    const first = Math.max(0, Math.floor(this.el.scrollTop / this.rowHeight) - 8);
    const last = Math.min(this.items.length - 1, Math.ceil((this.el.scrollTop + view) / this.rowHeight) + 8);
    for (const [i, row] of this.rows) if (i < first || i > last) { row.remove(); this.rows.delete(i); }
    for (let i = first; i <= last; i++) {
      if (this.rows.has(i)) continue;
      const row = this.opts.render(this.items[i], i);
      row.classList.add("row");
      row.dataset.i = String(i);
      row.style.top = i * this.rowHeight + "px";
      row.style.height = this.rowHeight + "px";
      if (this.sel.has(i)) row.classList.add("sel");
      this.spacer.append(row);
      this.rows.set(i, row);
    }
  }
}

// ---------- tiles ----------

export interface Tile<T = unknown> { key: string; title: string; caption: string; art: string; item: T }
export type TileKind = "square" | "small" | "poster" | "wide" | "picture";
const TILE_ART: Record<TileKind, number> = { square: 300, small: 240, poster: 320, wide: 300, picture: 300 };

export class TileGrid<T> {
  readonly el = h("div.tilegrid");
  tiles: Tile<T>[] = [];
  selectedKey: string | null = null;
  onClick: (t: Tile<T>) => void = () => {};
  onActivate: (t: Tile<T>) => void = () => {};
  onContext: (t: Tile<T>, e: MouseEvent) => void = () => {};

  constructor(public kind: TileKind) {
    const find = (e: Event) => {
      const el = (e.target as HTMLElement).closest<HTMLElement>(".tile");
      return el ? this.tiles[Number(el.dataset.i)] : undefined;
    };
    this.el.addEventListener("click", e => { const t = find(e); if (t) this.onClick(t); });
    this.el.addEventListener("dblclick", e => { const t = find(e); if (t) this.onActivate(t); });
    this.el.addEventListener("contextmenu", e => { const t = find(e); if (t) { e.preventDefault(); this.onContext(t, e); } });
  }

  setItems(tiles: Tile<T>[]) {
    this.tiles = tiles;
    const frag = document.createDocumentFragment();
    tiles.forEach((t, i) => {
      const el = h(`div.tile.${this.kind}` as "div", { data: { i: String(i) } }, h("div.art", art(t.art, TILE_ART[this.kind])));
      if (this.kind === "picture") el.title = t.title;
      else el.append(h("div.t1.ell", t.title), h("div.t2.ell", t.caption || " "));
      if (t.key === this.selectedKey) el.classList.add("sel");
      frag.append(el);
    });
    this.el.replaceChildren(frag);
  }

  select(key: string | null) {
    this.selectedKey = key;
    this.tiles.forEach((t, i) => (this.el.children[i] as HTMLElement | undefined)?.classList.toggle("sel", t.key === key));
  }
}

// ---------- tabs ----------

export function tabs(items: [key: string, label: string][], onPick: (key: string) => void, cls = "tab") {
  const el = h("div.tabs");
  const buttons = new Map<string, HTMLElement>();
  for (const [key, label] of items) {
    const b = h(`div.nav.${cls}` as "div", { onclick: () => onPick(key) }, label);
    buttons.set(key, b);
    el.append(b);
  }
  return {
    el,
    select: (key: string) => buttons.forEach((b, k) => b.classList.toggle("on", k === key)),
    setVisible: (keys: string[]) => buttons.forEach((b, k) => b.classList.toggle("hidden", !keys.includes(k))),
  };
}

// ---------- context menus ----------

export interface MenuItem { label: string; action?: () => void; disabled?: boolean; checked?: boolean; sub?: MenuEntry[] }
export type MenuEntry = MenuItem | "-" | null | undefined | false;

let closeMenu: (() => void) | null = null;

export function showMenu(entries: MenuEntry[], x: number, y: number) {
  closeMenu?.();
  const shield = h("div.menu-shield");
  const open: HTMLElement[] = [];
  const close = () => { shield.remove(); open.forEach(m => m.remove()); document.removeEventListener("keydown", onKey, true); closeMenu = null; };
  const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") { e.stopPropagation(); close(); } };
  shield.addEventListener("mousedown", close);
  shield.addEventListener("contextmenu", e => { e.preventDefault(); close(); });
  document.addEventListener("keydown", onKey, true);
  closeMenu = close;
  document.body.append(shield);

  const build = (items: MenuEntry[], px: number, py: number, level: number, avoid?: DOMRect) => {
    while (open.length > level) open.pop()!.remove();
    const menu = h("div.menu");
    let lastSep = true;
    for (const it of items) {
      if (!it) continue;
      if (it === "-") { if (!lastSep) menu.append(h("div.sep")); lastSep = true; continue; }
      lastSep = false;
      const el = h("div.item", it.label);
      if (it.disabled) el.classList.add("disabled");
      if (it.checked) el.classList.add("checked");
      if (it.sub) {
        el.classList.add("sub");
        const openSub = () => {
          menu.querySelectorAll(".item.open").forEach(o => o.classList.remove("open"));
          el.classList.add("open");
          const r = el.getBoundingClientRect();
          build(it.sub!, r.right - 2, r.top - 5, level + 1, r);
        };
        el.addEventListener("mouseenter", openSub);
        el.addEventListener("click", openSub);
      } else {
        el.addEventListener("mouseenter", () => { while (open.length > level + 1) open.pop()!.remove(); menu.querySelectorAll(".item.open").forEach(o => o.classList.remove("open")); });
        el.addEventListener("click", () => { close(); it.action?.(); });
      }
      menu.append(el);
    }
    if (menu.lastElementChild?.classList.contains("sep")) menu.lastElementChild.remove();
    document.body.append(menu);
    open.push(menu);
    // keep it on screen
    const r = menu.getBoundingClientRect();
    let left = px, top = py;
    if (left + r.width > innerWidth - 4) left = avoid ? Math.max(4, avoid.left - r.width + 2) : Math.max(4, innerWidth - r.width - 4);
    if (top + r.height > innerHeight - 4) top = Math.max(4, innerHeight - r.height - 4);
    menu.style.left = left + "px";
    menu.style.top = top + "px";
  };
  build(entries, x, y, 0);
}

/** Opens a menu under (or at the pointer of) whatever was clicked. */
export function menuAt(entries: MenuEntry[], e: MouseEvent | HTMLElement) {
  if (e instanceof HTMLElement) { const r = e.getBoundingClientRect(); showMenu(entries, r.left, r.bottom + 2); }
  else showMenu(entries, e.clientX, e.clientY);
}

// ---------- dialogs ----------

export interface DialogHandle { el: HTMLDivElement; body: HTMLDivElement; close: () => void; closed: Promise<void>; onEnter?: () => void }

/** A layer over the window holding a box of the given width; Escape closes it. */
export function openDialog(width: number, height?: number): DialogHandle {
  const body = h("div.dialog", { style: { width: width + "px", height: height ? height + "px" : "" } });
  const el = h("div.dialog-shield", body);
  let done!: () => void;
  const closed = new Promise<void>(r => (done = r));
  const handle: DialogHandle = { el, body, close, closed };
  const previous = document.activeElement as HTMLElement | null;
  function onKey(e: KeyboardEvent) {
    if (el !== document.body.querySelector(".dialog-shield:last-of-type")) return;   // a dialog opened over this one
    if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); close(); }
    else if (e.key === "Enter" && handle.onEnter && !(e.target instanceof HTMLTextAreaElement) && !(e.target instanceof HTMLButtonElement)) { e.preventDefault(); handle.onEnter(); }
    else e.stopPropagation();   // typing in a dialog is not a shortcut for the player
  }
  function close() {
    if (!el.isConnected) return;
    el.remove();
    document.removeEventListener("keydown", onKey, true);
    previous?.focus?.();
    done();
  }
  document.addEventListener("keydown", onKey, true);
  document.body.append(el);
  return handle;
}

export function dialogButtons(d: DialogHandle, ok: string, onOk: () => void, cancel: string | null = "cancel"): HTMLDivElement {
  d.onEnter = onOk;
  return h("div.buttons", pill(ok, onOk), cancel ? pill(cancel, d.close) : null);
}

export function ask(title: string, prompt: string, initial: string): Promise<string | null> {
  const d = openDialog(460);
  const box = h("input", { type: "text", value: initial });
  let result: string | null = null;
  d.body.append(h("div.h2", title.toLowerCase()), h("div", { style: { marginBottom: "6px" } }, prompt), box,
    dialogButtons(d, "ok", () => { result = box.value; d.close(); }));
  box.focus();
  box.select();
  return d.closed.then(() => result);
}

export function confirm(text: string, ok = "ok"): Promise<boolean> {
  const d = openDialog(460);
  let yes = false;
  d.body.append(h("div", { style: { whiteSpace: "pre-wrap" } }, text), dialogButtons(d, ok, () => { yes = true; d.close(); }));
  return d.closed.then(() => yes);
}

export function message(text: string): Promise<void> {
  const d = openDialog(460);
  d.body.append(h("div", { style: { whiteSpace: "pre-wrap", userSelect: "text" } }, text), dialogButtons(d, "ok", d.close, null));
  return d.closed;
}

// ---------- slider: thin track, gradient fill, small round thumb ----------

export class Slider {
  readonly el: HTMLDivElement;
  private fill = h("div.fill");
  private knob = h("div.knob");
  max = 1;
  value = 0;
  dragging = false;
  /** While dragging. */
  onInput: (value: number) => void = () => {};
  /** When let go (or clicked). */
  onCommit: (value: number) => void = () => {};

  constructor(width?: number) {
    this.el = h("div.slider", h("div.track"), this.fill, this.knob);
    if (width) { this.el.style.flex = "none"; this.el.style.width = width + "px"; }
    const at = (e: PointerEvent) => {
      const r = this.el.getBoundingClientRect();
      return Math.min(1, Math.max(0, (e.clientX - r.left) / Math.max(1, r.width))) * this.max;
    };
    this.el.addEventListener("pointerdown", e => {
      if (e.button !== 0) return;
      this.dragging = true;
      this.el.setPointerCapture(e.pointerId);
      this.paint(at(e));
      this.onInput(this.value);
    });
    this.el.addEventListener("pointermove", e => { if (this.dragging) { this.paint(at(e)); this.onInput(this.value); } });
    const end = (commit: boolean) => (e: PointerEvent) => {
      if (!this.dragging) return;
      this.dragging = false;
      if (commit) { this.paint(at(e)); this.onCommit(this.value); }
    };
    this.el.addEventListener("pointerup", end(true));
    this.el.addEventListener("pointercancel", end(false));
  }

  private paint(v: number) {
    this.value = v;
    const pct = (this.max > 0 ? Math.min(1, Math.max(0, v / this.max)) : 0) * 100 + "%";
    this.fill.style.width = pct;
    this.knob.style.left = pct;
  }

  /** Follows playback, except while the thumb is being dragged. */
  set(value: number, max = this.max) {
    if (this.dragging) return;
    this.max = max;
    this.paint(value);
  }
}

export const sleep = (ms: number) => new Promise<void>(r => setTimeout(r, ms));

// ---------- sorting and filtering bar ----------

export interface ViewState { sort: string; desc: boolean; show: string; text: string }
interface ViewChoices { sorts: { key: string; label: string }[]; shows: { key: string; label: string }[] }

/**
 * The bar a collection page carries, as on Windows: a box to type a filter into, what to show (liked, never played…),
 * what to sort by, and which way. The page asks for its lists with <code>view</code>; the helper remembers the choices.
 */
export class ViewBar {
  readonly el = h("div.viewbar");
  private text = h("input.filter", { type: "text", placeholder: "filter", spellcheck: false, title: "Filter: type part of a name" });
  private show = h("select.box.small", { title: "Show" });
  private sort = h("select.box.small", { title: "Sort by" });
  private dir = iconBtn("up", "First first (press to turn around)", () => { this.state.desc = !this.state.desc; this.paint(); this.onChange(); }, "small");
  state: ViewState = { sort: "", desc: false, show: "", text: "" };
  loaded = false;
  onChange: () => void = () => {};
  readonly ready: Promise<void>;

  constructor(readonly page: string, callFn: (m: string, p?: unknown) => Promise<any>) {
    let timer: ReturnType<typeof setTimeout> | undefined;
    this.text.addEventListener("input", () => { clearTimeout(timer); timer = setTimeout(() => { this.state.text = this.text.value.trim(); this.onChange(); }, 220); });
    this.text.addEventListener("keydown", e => { if (e.key === "Escape" && this.text.value) { e.stopPropagation(); this.text.value = ""; this.state.text = ""; this.onChange(); } });
    this.show.onchange = () => { this.state.show = this.show.value; this.onChange(); };
    this.sort.onchange = () => { this.state.sort = this.sort.value; this.onChange(); };
    this.el.append(this.text, this.show, this.sort, this.dir);
    this.ready = (async () => {
      const [choices, saved] = await Promise.all([callFn("view.options", { page }) as Promise<ViewChoices | null>, callFn("view.get", { page }) as Promise<ViewState>]);
      if (choices) {
        this.show.replaceChildren(...choices.shows.map(c => h("option", { value: c.key }, c.label)));
        this.sort.replaceChildren(...choices.sorts.map(c => h("option", { value: c.key }, "by " + c.label)));
        this.show.classList.toggle("hidden", choices.shows.length === 0);
        this.sort.classList.toggle("hidden", choices.sorts.length === 0);
        this.dir.classList.toggle("hidden", choices.sorts.length === 0);
        if (saved) this.state = { ...saved, text: "" };
        if (!choices.shows.some(c => c.key === this.state.show)) this.state.show = "";
        if (!choices.sorts.some(c => c.key === this.state.sort)) this.state.sort = "";
      }
      this.loaded = true;
      this.paint();
    })();
  }

  /** What to send with a page's request: nothing until the saved choices are known (the helper then uses them itself). */
  get value(): ViewState | undefined { return this.loaded ? { ...this.state } : undefined; }
  get active() { return this.state.sort !== "" || this.state.desc || this.state.show !== "" || this.state.text !== ""; }

  set(v: Partial<ViewState>) { this.state = { ...this.state, ...v }; this.paint(); this.onChange(); }

  private paint() {
    this.show.value = this.state.show;
    this.sort.value = this.state.sort;
    if (this.text.value.trim() !== this.state.text) this.text.value = this.state.text;
    setIcon(this.dir, this.state.desc ? "down" : "up");
    this.dir.classList.toggle("on", this.state.desc);
    this.dir.title = this.state.desc ? "Last first (press to turn around)" : "First first (press to turn around)";
  }
}

/** A button drawn like the page's tabs, for an action that sits on the same line ("shuffle all"). */
export const tabButton = (label: string, onclick: () => void, tip = "") => h("div.nav.tab.tabaction", { onclick, title: tip }, label);
