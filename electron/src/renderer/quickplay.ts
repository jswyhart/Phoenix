// quickplay: pins, new albums and history across the top, liked songs underneath.
import { call, on } from "./api";
import { debounce, Page, shell } from "./shell";
import { h, menuAt, pill, textBtn, TileGrid } from "./ui";
import type { MusicPage } from "./music";

interface QuickTile { title: string; caption: string; art: string; play: { type: string; key: string }; pin: null | { kind: string; key: string; title: string } }
interface QuickView { empty: boolean; pins: QuickTile[]; fresh: QuickTile[]; history: QuickTile[]; liked: QuickTile[] }

export function quickplayPage(): Page {
  const grid = () => {
    const g = new TileGrid<QuickTile>("small");
    g.onClick = t => {
      if (t.item.play.type === "album" && !shell.display.quickplayPlays) { shell.navigate("music"); void shell.get<MusicPage>("music").openAlbum(t.item.play.key); }
      else void call("quick.play", t.item.play);
    };
    g.onContext = (t, e) => menuAt([
      { label: "Play", action: () => call("quick.play", t.item.play) },
      t.item.pin ? { label: "Unpin from quickplay", action: () => call("pin.toggle", t.item.pin) } : null,
    ], e);
    return g;
  };
  const pins = grid(), fresh = grid(), history = grid(), liked = grid();
  const hint = (text: string) => h("div.note", { style: { margin: "16px 16px 0 6px" } }, text);
  const pinsHint = hint("Right-click an artist, album or playlist and choose “Pin to quickplay” to keep it here.");
  const historyHint = hint("What you play shows up here.");
  const likedHint = hint("Songs you like (the heart next to a song) collect here, newest first.");
  const likedButtons = h("div", { style: { display: "flex" } },
    h("div", { style: { margin: "26px 0 0 22px" } }, textBtn("play all", () => void call("quick.play", { type: "likedAll" }))),
    h("div", { style: { margin: "26px 0 0 14px" } }, textBtn("shuffle", () => void call("quick.play", { type: "likedShuffle" }))));
  const statsLink = h("div", { style: { margin: "26px 0 0 22px" } }, textBtn("listening stats", () => shell.navigate("stats")));
  statsLink.title = "Hours, plays and your top artists, albums and songs";
  const section = (title: string, hintEl: HTMLElement | null, g: TileGrid<QuickTile>, extra?: HTMLElement) =>
    h("div.pane", { style: { marginRight: "16px" } }, h("div", { style: { display: "flex" } }, h("div.zunehead", title), extra), hintEl, g.el);
  const likedSection = section("liked", likedHint, liked, h("div", { style: { display: "flex" } }, likedButtons, statsLink));
  likedSection.style.gridColumn = "1 / span 3";
  const sections = h("div", { style: { display: "grid", gridTemplateColumns: "1fr 1fr 1fr", gridTemplateRows: "380px minmax(0, 1fr)", height: "100%", padding: "14px 0 0 22px" } },
    section("pins", pinsHint, pins), section("new", null, fresh), section("history", historyHint, history), likedSection);
  const empty = h("div.center-note.hidden",
    h("div.h2", { style: { fontSize: "34px" } }, "welcome"),
    h("div.note", "Add your music, video and picture folders, or connect a Jellyfin or Plex server, to get started."),
    pill("open settings", () => shell.navigate("settings")));
  const el = h("div", { style: { height: "100%", position: "relative" } }, sections, empty);

  let dirty = false;
  async function load() {
    const v = await call<QuickView>("quick.view");
    const tiles = (list: QuickTile[]) => list.map((q, i) => ({ key: String(i), title: q.title, caption: q.caption, art: q.art, item: q }));
    empty.classList.toggle("hidden", !v.empty);
    sections.classList.toggle("hidden", v.empty);
    pins.setItems(tiles(v.pins));
    fresh.setItems(tiles(v.fresh));
    history.setItems(tiles(v.history));
    liked.setItems(tiles(v.liked));
    pinsHint.classList.toggle("hidden", v.pins.length > 0);
    historyHint.classList.toggle("hidden", v.history.length > 0);
    likedHint.classList.toggle("hidden", v.liked.length > 0);
    likedButtons.classList.toggle("hidden", v.liked.length === 0);
  }
  const reload = debounce(() => { if (el.isConnected) void load(); else dirty = true; }, 200);
  on("lib", reload);
  on("ratings", reload);
  let lastIndex = -2;
  on("player", s => { if (s.index !== lastIndex) { lastIndex = s.index; reload(); } });
  void load();
  return { el, shown: () => { if (dirty) { dirty = false; void load(); } } };
}
