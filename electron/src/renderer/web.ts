// Streaming services in Phoenix's window, as on Windows: a service's own web player in a <webview> (signed in to on its
// own page, kept in a profile of its own), shown as a collection tab of its own, under any collection, or in a tab you
// made. While a page is what you are listening to, the player bar works it through the hook in webguest.ts.
import { call, on } from "./api";
import { Page, shell } from "./shell";
import { h, iconBtn, tabButton } from "./ui";

export interface Service { id: string; name: string; url: string; video: boolean; profile: string; custom: boolean; page: string; on: boolean; place: string; placeName: string }
export interface WebStateShape { services: Service[]; places: { place: string; name: string }[]; ownTabs: string[]; custom: { id: string; name: string; page: string }[] }

/** What the streaming services look like right now (kept up to date from the helper's "web" event). */
export const webState: { value: WebStateShape } = { value: { services: [], places: [], ownTabs: [], custom: [] } };
const listeners = new Set<() => void>();
export function onWeb(fn: () => void) { listeners.add(fn); }
export async function loadWeb() { webState.value = await call<WebStateShape>("web.state"); listeners.forEach(f => f()); }
on("web", (s: WebStateShape) => { webState.value = s; listeners.forEach(f => f()); });

export const serviceById = (id: string) => webState.value.services.find(s => s.id === id);
/** The services switched on that are shown under a collection (or in a tab of your own). */
export const servicesUnder = (place: string) => webState.value.services.filter(s => s.on && s.place === place);

/**
 * Puts the services shown under <collection> into a row of tabs, just after the row's own tabs and drawn like them
 * ("movies  tv shows  collections  other videos  all  youtube  netflix"). A click opens the service's page.
 */
export function fillServiceTabs(row: HTMLElement, collection: string, current?: string) {
  row.querySelectorAll(".service-tab").forEach(e => e.remove());
  const own = [...row.querySelectorAll(":scope > .nav.tab:not(.tabaction):not(.service-tab)")];
  let after: Element | null = own[own.length - 1] ?? null;
  for (const s of servicesUnder(collection)) {
    const tab = h("div.nav.tab.service-tab", { title: `${s.name}, shown here in Phoenix`, onclick: () => shell.navigate(s.page), data: { page: s.page } }, s.name.toLowerCase());
    tab.classList.toggle("on", current === s.page);
    if (after) after.after(tab); else row.prepend(tab);
    after = tab;
  }
}

/** Keeps a page's row of tabs up to date with the services placed under its collection. */
export function attachServiceTabs(row: HTMLElement, collection: string, changed?: () => void) {
  const refresh = () => { fillServiceTabs(row, collection); changed?.(); };
  onWeb(refresh);
  refresh();
  return refresh;
}

// ---------- the player bar working a page ----------

export interface RemoteState {
  title: string; artist: string; album: string; art: string; playing: boolean; pos: number; dur: number;
  shuffle: number | null; repeat: number | null; liked: boolean | null; canSeek: boolean; canNext: boolean; canPrevious: boolean; hasMedia: boolean;
}

/** The page being listened to (it started playing last and nothing has started since), and its latest report. */
export const remote = {
  active: null as WebServicePage | null,
  changed: () => {},
};

function parseState(raw: unknown, audible: boolean): RemoteState | null {
  if (typeof raw !== "string" || raw === "") return null;
  try {
    const r = JSON.parse(raw);
    const flag = (v: string | null) => (v === "true" || v === "mixed" ? true : v === "false" ? false : null);
    return {
      title: r.title || r.page || "", artist: r.artist || "", album: r.album || "", art: r.art || "", playing: audible || !!r.playing,
      pos: Number(r.pos) || 0, dur: Number(r.dur) || 0,
      shuffle: r.shuffle === "true" ? 1 : r.shuffle === "smart" || r.shuffle === "mixed" ? 2 : r.shuffle === "false" ? 0 : null,
      repeat: r.repeat === "true" ? 1 : r.repeat === "mixed" ? 2 : r.repeat === "false" ? 0 : null,
      liked: flag(r.liked), canSeek: !!r.canSeek, canNext: !!r.canNext, canPrevious: !!r.canPrevious, hasMedia: !!r.hasMedia,
    };
  } catch { return null; }
}

type WebviewTag = HTMLElement & {
  src: string; executeJavaScript(code: string): Promise<unknown>; canGoBack(): boolean; canGoForward(): boolean; goBack(): void; goForward(): void;
  reload(): void; loadURL(url: string): Promise<void>; getURL(): string; getTitle(): string; isCurrentlyAudible(): boolean; setAudioMuted(m: boolean): void; isAudioMuted(): boolean;
};

/** A streaming service's page: a way back to where it is placed, its sibling services, the page's own back/forward/reload/home, and the page. */
export class WebServicePage implements Page {
  readonly el: HTMLDivElement;
  readonly view: WebviewTag;
  readonly persistent = true;
  state: RemoteState | null = null;
  ready = false;
  private note = h("div.center-note");
  private siblings = h("div.tabs", { style: { margin: "0 10px 0 0" } });
  private poll: ReturnType<typeof setInterval> | undefined;
  private started = false;

  constructor(readonly service: Service) {
    this.view = document.createElement("webview") as WebviewTag;
    this.view.setAttribute("partition", "persist:" + service.profile);
    this.view.setAttribute("allowpopups", "");
    this.view.className = "webpage";
    const tool = (icon: string, tip: string, click: () => void) => iconBtn(icon, tip, click, "small");
    const caption = h("div.faint", { style: { fontSize: "12px", marginLeft: "10px" } }, service.name);
    const bar = h("div.webbar", this.siblings,
      tool("back", "Back", () => { if (this.view.canGoBack()) this.view.goBack(); }),
      tool("forward", "Forward", () => { if (this.view.canGoForward()) this.view.goForward(); }),
      tool("reload", "Reload", () => this.view.reload()),
      tool("home", service.name + " home", () => void this.view.loadURL(service.url)),
      caption);
    this.note.textContent = `opening ${service.name}…`;
    this.el = h("div.page.webservice", bar, h("div.webbody", this.note, this.view));
    this.view.addEventListener("did-finish-load", () => { this.ready = true; this.note.classList.add("hidden"); });
    this.view.addEventListener("did-fail-load", (e: any) => {
      if (e.errorCode === -3 || !e.isMainFrame) return;   // a navigation that was replaced by another
      this.note.classList.remove("hidden");
      this.note.textContent = `${service.name} could not be reached. Check your connection, then use reload.`;
    });
    // the page starting to play pauses whatever Phoenix is playing, and Phoenix's controls then work the page
    this.view.addEventListener("media-started-playing", () => {
      if (shell.player.playing && !shell.player.current?.isVideo) void call("player.toggle");
      else if (shell.player.playing) void call("player.toggle");
      remote.active = this;
      void this.ask();
    });
    this.view.addEventListener("media-paused", () => void this.ask());
    this.paintSiblings();
    onWeb(() => this.paintSiblings());
  }

  /** Shown under a collection: a way back to it, and the other services there as tabs (this one lit). */
  private paintSiblings() {
    const s = serviceById(this.service.id) ?? this.service;
    this.siblings.replaceChildren();
    if (s.place === "tab") return;
    const row = h("div.tabs", { style: { margin: "0" } });
    this.siblings.append(tabButton("‹  " + s.placeName, () => shell.navigate(s.place), "Back to " + s.placeName), row);
    fillServiceTabs(row, s.place, this.service.page);
  }

  shown() {
    if (!this.started) {
      this.started = true;
      this.view.src = this.service.url;
      this.poll = setInterval(() => { if (remote.active === this || this.el.offsetParent !== null) void this.ask(); }, 700);
    }
    this.paintSiblings();
  }

  /** Self-test only: shows a stand-in page instead of the service. */
  async loadTestPage(url: string) {
    this.started = true;
    this.poll ??= setInterval(() => { if (remote.active === this || this.el.offsetParent !== null) void this.ask(); }, 700);
    const done = new Promise<void>(r => this.view.addEventListener("did-finish-load", () => r(), { once: true }));
    this.view.src = url;
    await Promise.race([done, new Promise(r => setTimeout(r, 8000))]);
  }

  private async run(js: string): Promise<unknown> {
    try { return await this.view.executeJavaScript(js); } catch { return null; }
  }

  async ask() {
    if (!this.ready) return;
    let audible = false;
    try { audible = this.view.isCurrentlyAudible(); } catch { /* not attached yet */ }
    const next = parseState(await this.run("window.__phx ? window.__phx.state() : ''"), audible);
    if (JSON.stringify(next) === JSON.stringify(this.state)) return;
    this.state = next;
    if (remote.active === this) remote.changed();
  }

  async command(name: string, value: number | null = null) {
    await this.run(`window.__phx && window.__phx.run(${JSON.stringify(name)}, ${value === null ? "null" : String(value)})`);
    await new Promise(r => setTimeout(r, 120));
    await this.ask();
  }

  toggle() { void this.command("toggle"); }
  next() { void this.command("next"); }
  previous() { void this.command("previous"); }
  shuffle() { void this.command("shuffle"); }
  repeat() { void this.command("repeat"); }
  like() { void this.command("like"); }
  seek(seconds: number) { void this.command("seek", Math.max(0, seconds)); }
  volume(v: number) { void this.command("volume", Math.min(1, Math.max(0, v))); }
  /** Stops the page playing: by its own controls if it can, else every media element in it. */
  pause() { void this.command(this.state?.playing ? "stop" : "pause"); }
  set muted(m: boolean) { try { this.view.setAudioMuted(m); } catch { /* not attached */ } }
  get muted() { try { return this.view.isAudioMuted(); } catch { return false; } }
}

// ---------- a collection tab of your own ----------

export interface CustomTabPage extends Page { services(): string[] }

/** A collection tab of your own: the streaming services you put in it, as tabs, with a card for each. */
export function customTabPage(page: string): CustomTabPage {
  const row = h("div.tabs");
  const notice = h("div.note", { style: { margin: "6px 28px 0 8px" } });
  const cards = h("div", { style: { display: "flex", flexWrap: "wrap", margin: "0 0 0 8px" } });
  const el = h("div.page", row, notice, cards);
  const paint = () => {
    fillServiceTabs(row, page);
    const list = servicesUnder(page);
    notice.textContent = list.length === 0 ? "Nothing in this tab yet. In settings → servers → streaming, switch a service on and choose this tab under “show in”." : "";
    notice.classList.toggle("hidden", list.length > 0);
    cards.replaceChildren(...list.map(s => h("button.bare.webcard", { title: "Open " + s.name, onclick: () => shell.navigate(s.page) },
      h("div", { style: { fontSize: "30px", fontWeight: "300" } }, s.name.toLowerCase()),
      h("div.faint", { style: { fontSize: "12px", marginTop: "6px" } }, s.url.replace("https://", "").replace(/\/$/, "")))));
  };
  onWeb(paint);
  paint();
  return { el, shown: paint, services: () => [...row.querySelectorAll<HTMLElement>(".service-tab")].map(t => t.dataset.page ?? "") };
}
