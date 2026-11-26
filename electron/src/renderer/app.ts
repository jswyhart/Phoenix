// The window: header navigation, the page in the middle, the now-playing layer, and the transport bar.
import { call, clock, host, Init, on, PlayerState, time } from "./api";

/** A second of silence as a WAV (for the system's media controls; see paintSession). */
function silentWav(): string {
  const rate = 8000, n = rate, buf = new ArrayBuffer(44 + n), v = new DataView(buf);
  const text = (o: number, t: string) => [...t].forEach((c, i) => v.setUint8(o + i, c.charCodeAt(0)));
  text(0, "RIFF"); v.setUint32(4, 36 + n, true); text(8, "WAVE"); text(12, "fmt "); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
  v.setUint32(24, rate, true); v.setUint32(28, rate, true); v.setUint16(32, 1, true); v.setUint16(34, 8, true); text(36, "data"); v.setUint32(40, n, true);
  for (let i = 0; i < n; i++) v.setUint8(44 + i, 128);
  return URL.createObjectURL(new Blob([buf], { type: "audio/wav" }));
}
import { booksPage } from "./books";
import { carePage } from "./care";
import { mixviewPage, MixviewPage } from "./mixview";
import { menuHooks } from "./songs";
import { installDialogs } from "./dialogs";
import { installTools } from "./tools";
import { chooseCastDevice } from "./cast";
import { loadDisplay } from "./settingsDisplay";
import { applyTheme, loadTheme, ThemeShape } from "./themes";
import { musicPage } from "./music";
import { NowPlaying } from "./nowplaying";
import { installPhoneMenu, phonePage } from "./phone";
import { picturesPage } from "./pictures";
import { installPlaylistMenu, playlistsPage, searchPage } from "./playlists";
import { podcastsPage } from "./podcasts";
import { quickplayPage } from "./quickplay";
import { radioPage } from "./radio";
import { statsPage } from "./stats";
import { settingsPage } from "./settings";
import { Page, shell } from "./shell";
import { runSmoke } from "./smoke";
import { videosPage } from "./videos";
import { customTabPage, loadWeb, onWeb, remote, serviceById, WebServicePage, webState } from "./web";
import { art, artUrl, bumpArt, h, icon, iconBtn, MenuEntry, menuAt, setArt, setIcon, Slider } from "./ui";

const makers: Record<string, () => Page> = {
  quickplay: quickplayPage, music: musicPage, videos: videosPage, pictures: picturesPage, podcasts: podcastsPage,
  books: booksPage, playlists: playlistsPage, radio: radioPage, phone: phonePage, search: searchPage, settings: settingsPage, stats: statsPage, care: carePage, mixview: mixviewPage,
};

async function start() {
  const init = await call<Init>("init");
  shell.init = init;
  shell.settings = init.settings;
  shell.player = init.player;
  shell.queue = init.queue;
  shell.tabs = init.tabs;
  await loadWeb();
  await loadDisplay();
  void call<{ canIdentify: boolean }>("meta.state").then(m => { shell.canIdentify = m.canIdentify; });
  const root = document.getElementById("root")!;

  // ---------- header ----------
  const backBtn = iconBtn("back", "Back", () => { const p = back.pop(); if (p) navigate(p, false); });
  backBtn.style.margin = "0 10px 0 -10px";
  const top = new Map<string, HTMLElement>();
  const topNav = (key: string, label: string) => {
    const el = h("div.nav.topnav", { onclick: () => navigate(key === "collection" ? collPage : key) }, label);
    top.set(key, el);
    return el;
  };
  const status = h("div.status.ell");
  const search = h("input", { type: "text", placeholder: "search", spellcheck: false });
  const logo = h("button.logo", { title: "Quickplay", onclick: () => navigate("quickplay"), tabIndex: -1 }, h("img", { src: "phoenix.svg", alt: "", draggable: false }));
  const miniBtn = iconBtn("mini", "Mini player: a small window that stays on top", () => void host.mini(true));
  miniBtn.style.marginRight = "14px";
  const sub = new Map<string, HTMLElement>();
  const subNav = h("div.subnav");
  /**
   * The collection's tabs, in order: its own that are switched on, then tabs of your own, then streaming services that
   * have a tab of their own (as on Windows).
   */
  const collectionTabs = (): [page: string, label: string][] => [
    ...shell.tabs.tabs.filter(t => t.on).map(t => [t.key, t.key] as [string, string]),
    ...shell.tabs.custom.map(t => [t.page, t.name.toLowerCase()] as [string, string]),
    ...webState.value.services.filter(s => s.on && s.place === "tab").map(s => [s.page, s.name.toLowerCase()] as [string, string]),
  ];
  const collection = () => collectionTabs().map(t => t[0]);
  const builtIn = () => shell.tabs.tabs.filter(t => t.on).map(t => t.key);
  /** The collection tab a page lights: itself, or for a service shown under a collection, that collection. */
  const litTab = (page: string) => {
    if (!page.startsWith("web:")) return page;
    const s = serviceById(page.slice(4));
    return s && s.place !== "tab" ? s.place : page;
  };
  function paintTabs() {
    sub.clear();
    subNav.replaceChildren(...collectionTabs().map(([p, label]) => { const el = h("div.nav.section", { onclick: () => navigate(p) }, label); el.dataset.page = p; sub.set(p, el); return el; }));
    sub.forEach((el, key) => el.classList.toggle("on", key === litTab(shell.page)));
  }
  paintTabs();
  const header = h("div.header",
    h("div.top",
      h("div", backBtn, topNav("quickplay", "QUICKPLAY"), topNav("collection", "COLLECTION"), topNav("settings", "SETTINGS"), status),
      h("div", miniBtn, h("div.searchbox", search, icon("search", 12)), logo)),
    subNav);
  // pages are swapped in the first slot; streaming services' pages stay in the second (a <webview> taken out of the
  // window would stop playing and lose its place), shown or moved out of sight
  const pageSlot = h("div.slot"), webSlot = h("div.slot.web");
  const hostEl = h("div", { style: { minHeight: "0", position: "relative" } }, pageSlot, webSlot);

  // ---------- navigation ----------
  const pages = new Map<string, Page>();
  const back: string[] = [];
  let collPage = "music";

  function get<T extends Page>(name: string): T {
    let p = pages.get(name);
    if (!p) {
      if (name.startsWith("web:")) {
        const s = serviceById(name.slice(4));
        if (!s) throw new Error("that streaming service is not on");
        p = new WebServicePage(s);
      } else if (name.startsWith("tab:")) p = customTabPage(name);
      else p = makers[name]();
      pages.set(name, p);
    }
    return p as T;
  }

  function navigate(page: string, push = true) {
    if (now.open) now.show(false);
    if (page === shell.page) return;
    if (page.startsWith("web:") && !serviceById(page.slice(4))?.on) page = collPage;
    if (push && shell.page !== "") back.push(shell.page);
    shell.page = page;
    const lit = litTab(page);
    const isColl = collection().includes(lit) || page.startsWith("web:");
    if (isColl) collPage = lit === page ? page : lit;
    const p = get(page);
    if ((p as any).persistent) {
      if (p.el.parentElement !== webSlot) webSlot.append(p.el);
      pageSlot.replaceChildren();
    } else pageSlot.replaceChildren(p.el);
    for (const child of webSlot.children) child.classList.toggle("offstage", child !== p.el);
    webSlot.classList.toggle("showing", (p as any).persistent === true);
    p.shown?.();
    top.get("quickplay")!.classList.toggle("on", page === "quickplay" || page === "stats");
    top.get("collection")!.classList.toggle("on", isColl || page === "search" || page === "phone");
    top.get("settings")!.classList.toggle("on", page === "settings" || page === "care");
    subNav.classList.toggle("hidden", !isColl);
    sub.forEach((el, key) => el.classList.toggle("on", key === lit));
    backBtn.disabled = back.length === 0;
  }

  shell.navigate = navigate;
  shell.get = get;
  shell.goBackOr = fallback => { const p = back.pop(); navigate(p ?? fallback, p === undefined); };
  shell.setStatus = text => { status.textContent = text; status.title = text; };
  // light or dark (the self-test); a theme of your own is chosen in settings → display
  shell.applyTheme = theme => void call<ThemeShape>("themes.use", { id: theme === "dark" ? "dark" : "light" }).then(applyTheme);
  await loadTheme();
  on("status", (s: string) => shell.setStatus(s ?? ""));
  // a tab switched off in settings leaves the collection; if it was showing, the first tab that is on takes its place
  const tabsChanged = () => {
    paintTabs();
    if (!collection().includes(collPage)) collPage = builtIn()[0] ?? "music";
    const gone = (shell.tabs.tabs.some(x => x.key === shell.page && !x.on))
      || (shell.page.startsWith("tab:") && !shell.tabs.custom.some(t => t.page === shell.page))
      || (shell.page.startsWith("web:") && !serviceById(shell.page.slice(4))?.on);
    if (gone) navigate(collPage, false);
    else sub.forEach((el, key) => el.classList.toggle("on", key === litTab(shell.page)));
  };
  on("tabs", (t: typeof shell.tabs) => { shell.tabs = t; tabsChanged(); });
  onWeb(tabsChanged);
  on("art", () => bumpArt());

  // ---------- transport ----------
  const barArt = art(null, 120);
  const barArtBox = h("div.barart", { title: "Now playing", onclick: () => now.show(!now.open) }, icon("note", 18), barArt);
  const barTitle = h("div.title.ell", "nothing playing"), barArtist = h("div.artist.ell");
  const words = h("div.words", { title: "Now playing", onclick: () => now.show(!now.open) }, barTitle, barArtist);
  const bookmarkBtn = iconBtn("bookmark", "Bookmark this spot in the audiobook", async () => {
    const label = await call<string | null>("book.bookmarkHere");
    if (label) shell.setStatus(`bookmarked ${label}`);
  }, "small hidden");
  /** A streaming page is what is being listened to, and Phoenix itself is not playing: the bar works the page. */
  const usingRemote = () => !!remote.active?.state && !shell.player.playing && (remote.active.state.hasMedia || remote.active.state.playing);
  const R = () => remote.active!;
  const heartBtn = iconBtn("heart", "Like / dislike this song", () => {
    if (usingRemote()) { R().like(); return; }
    const id = shell.player.current?.trackId; if (id != null) void call("rate.cycle", { id });
  }, "small");
  const posText = h("div.t"), durText = h("div.t.r");
  const seek = new Slider();
  seek.onInput = v => { posText.textContent = clock(v); };
  seek.onCommit = v => { if (usingRemote()) R().seek(v / 1000); else void call("player.seek", { ms: Math.round(v) }); };
  const shuffleBtn = iconBtn("shuffle", "Shuffle", () => { if (usingRemote()) R().shuffle(); else void call("player.shuffle"); });
  // the "more" button: sleep timer, playback speed, listening stats (lit while the timer or a speed is on)
  const moreBtn = iconBtn("more", "Sleep timer, playback speed, play on another device", () => menuAt(moreMenu(), moreBtn));
  function moreMenu(): MenuEntry[] {
    const s = shell.player;
    const left = s.sleep.replace("sleep: ", "").replace("sleep ", "");
    const speeds = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3];
    return [
      { label: s.sleepOn ? `Sleep timer  (${left})` : "Sleep timer", sub: [
        { label: "Off", checked: !s.sleepOn, action: () => call("player.sleep", {}) },
        ...[15, 30, 45, 60, 90, 120].map(n => ({ label: `${n} minutes`, action: () => call("player.sleep", { minutes: n }) })),
        { label: "When this one ends", checked: s.sleepAtEnd, disabled: !s.current, action: () => call("player.sleep", { atEnd: true }) },
      ] },
      { label: s.canSpeed ? "Playback speed" : "Playback speed (podcasts, audiobooks, video)", disabled: !s.canSpeed, sub: speeds.map(r => ({
        label: r === 1 ? "1× (normal)" : `${r}×`, checked: Math.abs(s.speed - r) < 0.01, action: () => call("player.speed", { value: r }),
      })) },
      "-",
      s.castName ? { label: `Stop playing on ${s.castName}`, action: () => call("cast.stop") } : null,
      { label: s.castName ? "Play on a different device…" : "Play on another device…", action: () => void chooseCastDevice() },
      "-",
      { label: "Mixview for this album", disabled: s.current?.trackId == null, action: async () => {
        const key = s.current?.trackId != null ? await call<string | null>("track.albumKey", { id: s.current.trackId }) : null;
        if (key) menuHooks.mixview(key, s.current?.artist ?? "");
      } },
      { label: "Listening stats", action: () => navigate("stats") },
    ];
  }
  const toggle = () => { if (usingRemote()) R().toggle(); else void call("player.toggle"); };
  shell.toggle = toggle;
  const playBtn = iconBtn("play", "Play / pause (Space)", toggle, "filled", 22);
  playBtn.style.width = "48px";
  const repeatBtn = iconBtn("repeat", "Repeat", () => { if (usingRemote()) R().repeat(); else void call("player.repeat"); });
  const muteBtn = iconBtn("volume", "Mute", () => {
    if (usingRemote()) { R().muted = !R().muted; setIcon(muteBtn, R().muted ? "mute" : "volume"); return; }
    void call("player.mute");
  });
  muteBtn.style.marginLeft = "6px";
  const volume = new Slider(84);
  volume.set(init.player.volume, 1);
  volume.onInput = v => { if (usingRemote()) R().volume(v); else void call("player.volume", { value: v }); };
  volume.onCommit = volume.onInput;
  const prevBtn = iconBtn("prev", "Previous", () => { if (usingRemote()) R().previous(); else void call("player.prev"); }, "filled");
  const nextBtn = iconBtn("next", "Next", () => { if (usingRemote()) R().next(); else void call("player.next"); }, "filled");
  const transport = h("div.transport",
    h("div.left",
      h("button.dropbtn", { title: "Phone. Click to see what syncs with your phone.", onclick: () => navigate("phone"), tabIndex: -1 }, icon("phone", 22, 28)),
      h("button.dropbtn", { title: "Click to see playlists", onclick: () => navigate("playlists"), tabIndex: -1 }, icon("playlist", 22, 28))),
    h("div.mid", barArtBox,
      h("div.info",
        h("div.titles", words, bookmarkBtn, heartBtn),
        h("div.seekrow", posText, seek.el, durText))),
    h("div.right", moreBtn, shuffleBtn, prevBtn, playBtn, nextBtn, repeatBtn, muteBtn, volume.el));

  const now = new NowPlaying(transport);
  shell.nowPlaying = now;
  shell.showNow = show => now.show(show);
  shell.nowOpen = () => now.open;
  shell.video = now.videoView;

  let lastArt: string | null = null, lastTitle = "";
  /** The bar while a streaming page is being listened to: what the page says it is playing, and only the controls it has. */
  function paintRemote() {
    const r = R().state!;
    barTitle.textContent = r.title || R().service.name;
    barArtist.textContent = [r.artist, R().service.name].filter(x => x).join("  ·  ");
    if (r.art !== lastArt) { lastArt = r.art; setArt(barArt, r.art || null, 120); }
    bookmarkBtn.classList.add("hidden");
    setIcon(playBtn, r.playing ? "pause" : "play");
    shuffleBtn.classList.toggle("hidden", r.shuffle === null);
    shuffleBtn.classList.toggle("on", (r.shuffle ?? 0) > 0);
    repeatBtn.classList.toggle("hidden", r.repeat === null);
    setIcon(repeatBtn, r.repeat === 2 ? "repeatOne" : "repeat");
    repeatBtn.classList.toggle("on", (r.repeat ?? 0) > 0);
    prevBtn.disabled = !r.canPrevious;
    nextBtn.disabled = !r.canNext;
    heartBtn.disabled = r.liked === null;
    heartBtn.classList.toggle("heartfull", r.liked === true);
    heartBtn.classList.toggle("on", r.liked === true);
    heartBtn.style.opacity = "";
    durText.textContent = time(r.dur * 1000);
    if (!seek.dragging) { posText.textContent = clock(r.pos * 1000); seek.set(Math.min(r.pos, r.dur || 1) * 1000, (r.dur || 1) * 1000); }
  }
  remote.changed = () => { if (usingRemote()) paintRemote(); else paintPlayer(shell.player); };

  function paintPlayer(s: PlayerState) {
    const before = shell.player;
    shell.player = s;
    // Phoenix starting to play pauses a streaming page and takes the bar back, so the two never play over each other
    if (s.playing && remote.active) { if (remote.active.state?.playing) remote.active.pause(); remote.active = null; }
    for (const b of [shuffleBtn, repeatBtn]) b.classList.remove("hidden");
    prevBtn.disabled = nextBtn.disabled = false;
    if (usingRemote()) { paintRemote(); return; }
    const cur = s.current;
    barTitle.textContent = cur?.title ?? "nothing playing";
    barArtist.textContent = cur?.subtitle ?? "";
    if ((cur?.art ?? null) !== lastArt) { lastArt = cur?.art ?? null; setArt(barArt, lastArt, 120); }
    bookmarkBtn.classList.toggle("hidden", !cur?.isBook);
    setIcon(playBtn, s.playing ? "pause" : "play");
    shuffleBtn.classList.toggle("on", s.shuffle);
    setIcon(repeatBtn, s.repeat === 2 ? "repeatOne" : "repeat");
    repeatBtn.classList.toggle("on", s.repeat > 0);
    setIcon(muteBtn, s.muted ? "mute" : "volume");
    paintMore(s.sleep);
    // liked: a filled pink heart; disliked: a faded one; otherwise an outline
    const rated = cur?.trackId != null ? cur.rating : 0;
    heartBtn.disabled = cur?.trackId == null;
    heartBtn.classList.toggle("on", rated !== 0);
    heartBtn.classList.toggle("heartfull", rated > 0);
    heartBtn.style.opacity = cur?.trackId == null ? "" : rated < 0 ? "0.45" : "";
    paintClock(s.pos, s.dur);
    paintSession(s);
    const changed = (cur?.title ?? "") !== lastTitle || before?.index !== s.index || (before?.current == null) !== (cur == null);
    lastTitle = cur?.title ?? "";
    now.playerChanged(s, changed);
  }

  /** What the "more" button has switched on, in words, as its tooltip; the button is lit while anything is. */
  function paintMore(sleep: string) {
    const s = shell.player;
    const mode = [s.castName ? "playing on " + s.castName : "", sleep, s.canSpeed && Math.abs(s.speed - 1) > 0.01 ? `${s.speed}× speed` : ""].filter(x => x).join("  ·  ");
    moreBtn.classList.toggle("on", mode !== "");
    moreBtn.title = mode || "Sleep timer, playback speed, play on another device";
  }

  // ---------- the system's media controls (macOS Now Playing, Linux MPRIS, media keys) ----------
  // Chromium shows a page's media session to the system only while the page itself plays something, so a silent
  // sound loops while Phoenix plays (the sound is VLC's, in the helper).
  const silence = new Audio(silentWav());
  silence.loop = true;
  silence.volume = 0.01;
  const session = navigator.mediaSession;
  try {
    session.setActionHandler("play", () => { if (!shell.player.playing) toggle(); });
    session.setActionHandler("pause", () => { if (shell.player.playing) toggle(); });
    session.setActionHandler("nexttrack", () => void call("player.next"));
    session.setActionHandler("previoustrack", () => void call("player.prev"));
    session.setActionHandler("seekto", d => { if (d.seekTime != null) void call("player.seek", { ms: Math.round(d.seekTime * 1000) }); });
    session.setActionHandler("stop", () => void call("player.stop"));
  } catch { /* an older engine without some actions */ }
  let lastSession = "";
  function paintSession(s: PlayerState) {
    const cur = s.current;
    const key = `${cur?.title}|${cur?.subtitle}|${cur?.art}`;
    if (key !== lastSession) {
      lastSession = key;
      session.metadata = cur ? new MediaMetadata({ title: cur.title, artist: cur.subtitle, album: cur.album, artwork: cur.art ? [{ src: artUrl(cur.art, 512), sizes: "512x512", type: "image/jpeg" }] : [] }) : null;
    }
    session.playbackState = !cur ? "none" : s.playing ? "playing" : "paused";
    if (s.playing && cur && !cur.isVideo) void silence.play().catch(() => {}); else silence.pause();
  }

  function paintClock(pos: number, dur: number) {
    if (!shell.player.current) { posText.textContent = durText.textContent = ""; seek.set(0, 1); return; }
    durText.textContent = time(dur);
    if (seek.dragging) return;
    posText.textContent = clock(pos);
    seek.set(Math.min(pos, dur > 0 ? dur : 1), dur > 0 ? dur : 1);
  }

  on("player", paintPlayer);
  on("tick", (t: { pos: number; dur: number; sleep: string }) => {
    shell.player.pos = t.pos; shell.player.dur = t.dur; shell.player.sleep = t.sleep;
    if (!usingRemote()) paintClock(t.pos, t.dur);
    paintMore(t.sleep);
  });
  on("queue", q => { shell.queue = q; now.queueChanged(); });

  root.append(header, hostEl, transport, now.el);

  // ---------- search ----------
  let searchTimer: ReturnType<typeof setTimeout> | undefined;
  search.addEventListener("input", () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => {
      const q = search.value.trim();
      if (q === "") { if (shell.page === "search") { const p = back.pop(); if (p) navigate(p, false); } return; }
      navigate("search");
      (get("search") as Page & { search?: (q: string) => void }).search?.(q);
    }, 300);
  });

  // ---------- keys ----------
  document.addEventListener("keydown", e => {
    const t = e.target as HTMLElement;
    const typing = t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement || t instanceof HTMLSelectElement || t.isContentEditable;
    const command = e.ctrlKey || e.metaKey;
    const done = () => { e.preventDefault(); e.stopPropagation(); };
    if (e.key === " " && !typing) { toggle(); done(); }
    else if (e.key === "MediaPlayPause") { toggle(); done(); }
    else if (e.key === "MediaTrackNext") { void call("player.next"); done(); }
    else if (e.key === "MediaTrackPrevious") { void call("player.prev"); done(); }
    else if (e.key === "ArrowRight" && !typing && command) { void call("player.next"); done(); }
    else if (e.key === "ArrowLeft" && !typing && command) { void call("player.prev"); done(); }
    else if (e.key === "F11") { if (!now.open) now.show(true); now.toggleFullscreen(); done(); }
    else if (e.key === "Escape" && now.full) { now.toggleFullscreen(); done(); }
    else if (e.key === "Escape" && now.open) { now.show(false); done(); }
    else if (e.key.toLowerCase() === "f" && command) { search.focus(); search.select(); done(); }
    else if (!typing && now.open) now.key(e);
  });
  // dropping a file on the window must not make the page leave itself
  document.addEventListener("dragover", e => e.preventDefault());
  document.addEventListener("drop", e => e.preventDefault());

  installPlaylistMenu();
  installPhoneMenu();
  installDialogs();
  installTools();
  menuHooks.mixview = (key, artist) => { navigate("mixview"); void get<MixviewPage>("mixview").show(key, artist); };
  navigate("quickplay");
  paintPlayer(init.player);
  if (host.smoke) void runSmoke();
}

// the mini player's window loads this same page with #mini
if (location.hash === "#mini") void import("./mini").then(m => m.startMini());
else start().catch(e => {
  document.getElementById("root")!.append(h("div.center-note", h("div.h2", "Phoenix couldn't start"), h("div.note", String(e?.message ?? e))));
  if (host.smoke) void host.smokeDone(`FAIL  app starts  ${e?.message ?? e}\n1 FAILED\n`, 1);
});
