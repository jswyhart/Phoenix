// collection → videos: movies, tv shows (series → seasons → episodes), collections, other videos, all.
import { call, host, isMac, on, time } from "./api";
import { debounce, Page } from "./shell";
import { art, ask, dialogButtons, h, menuAt, MenuEntry, openDialog, showMenu, tabs, TileGrid, ViewBar, VList } from "./ui";

interface Video { id: number; title: string; caption: string; art: string; rowTitle: string; code: string; dur: number; local: boolean }
interface Group { name: string; art: string; count: number; key: string }
interface View {
  tabs: string[]; tab: string; counts: string; empty: boolean; groups: Group[] | null; groupCap: string; group: string | null;
  seasons: { name: string; season: number | null }[] | null; season: number | null; cap: string; layout: "rows" | "poster" | "wide"; items: Video[];
}

/** Edits what a video is and where it is grouped, for one video or many. */
export async function editVideos(ids: number[]) {
  const info = await call<any>("videos.info", { ids });
  if (!info) return;
  const single = info.count === 1;
  const d = openDialog(500);
  const text = (value: string, short = false, enabled = true) => h(`input${short ? ".short" : ""}` as "input", { type: "text", value, disabled: !enabled });
  const kinds: [string, string][] = [["movie", "Movie"], ["episode", "TV episode"], ["video", "Other video"]];
  const kind = h("select.box", { style: { width: "200px" } }, h("option", { value: "" }, ""), ...kinds.map(([v, l]) => h("option", { value: v }, l)));
  kind.value = info.kind;
  const title = text(single ? info.title : "", false, single), series = text(info.series), collection = text(info.collection);
  const season = text(info.season, true), episode = text(single ? info.episode : "", true, single), year = text(info.year, true);
  const field = (label: string, el: HTMLElement) => h("div.field", h("span", label), el);
  let hint = single ? "" : "Changes apply to every selected video. Fields left blank keep their current values. ";
  if (info.source !== "local") hint += `These come from ${info.source}: your changes are kept in Phoenix and survive the next sync; the server itself is not changed.`;
  d.body.append(h("div.h2", single ? "edit video info" : `edit ${info.count} videos`), h(hint ? "div.note" : "div.hidden", hint),
    field("Title", title), field("Type", kind), field("Series / folder group", series), field("Season", season), field("Episode number", episode), field("Year", year), field("Collection", collection),
    dialogButtons(d, "save", async () => {
      await call("videos.save", { ids, kind: kind.value, title: title.value, series: series.value, season: season.value, episode: episode.value, year: year.value, collection: collection.value });
      d.close();
    }));
  (single ? title : series).focus();
}

export interface VideosPage extends Page { showTab(tab: string): void; ready(): Promise<void>; items(): Video[]; bar: ViewBar; tabRow: HTMLElement }

export function videosPage(): VideosPage {
  let tab = "", group: string | null = null, season: number | null | undefined, view: View | null = null, items: Video[] = [];
  let seq = 0, dirty = false, pending: Promise<void> = Promise.resolve();
  const leftCap = h("div.cap", "SERIES"), rightCap = h("div.cap", "VIDEOS"), count = h("div.cap.count");
  const tabBar = tabs([["movies", "movies"], ["shows", "tv shows"], ["collections", "collections"], ["folders", "other videos"], ["all", "all"]], t => showTab(t));
  const bar = new ViewBar("videos", call);
  bar.onChange = () => load(false);
  tabBar.el.append(count, bar.el);

  const groups = new VList<Group>({
    rowHeight: 32,
    render: g => h("div", g.art ? h("div.thumb", { style: { width: "40px", height: "60px", marginRight: "12px" } }, art(g.art, 120)) : null,
      h("div.grow.ell", { style: { fontSize: "14px" } }, g.name), h("div.faint", { style: { marginLeft: "8px" } }, String(g.count))),
    onSelect: sel => { if (sel[0]) { group = sel[0].key; season = undefined; load(true); } },
    onActivate: () => { if (items.length) playFrom(items[0]); },
    onContext: (sel, e) => { if (sel[0]) void groupMenu(sel[0], e.clientX, e.clientY); },
  });
  const seasons = new VList<{ name: string; season: number | null }>({
    rowHeight: 30,
    render: s => h("div.name", h("div.grow.ell", s.name)),
    onSelect: sel => { if (sel[0]) { season = sel[0].season; load(true); } },
  });
  const rows = new VList<Video>({
    rowHeight: 64, multi: true,
    render: v => h("div", h("div.thumb", { style: { width: "96px", height: "54px", marginRight: "14px" } }, art(v.art, 200)),
      h("div.grow", h("div.ell", { style: { fontSize: "14px" } }, v.rowTitle), h("div.faint", { style: { fontSize: "12px" } }, v.code)),
      h("div.dim", { style: { margin: "0 18px 0 12px" } }, time(v.dur))),
    onActivate: v => playFrom(v),
    onContext: (sel, e) => menuAt(menuFor(sel), e),
  });
  const tiles = new TileGrid<Video>("poster");
  tiles.onClick = t => tiles.select(t.key);
  tiles.onActivate = t => void call("play.videos", { ids: [t.item.id] });
  tiles.onContext = (t, e) => menuAt(menuFor([t.item]), e);

  const leftPane = h("div.pane", { style: { gridColumn: "1" } }, leftCap, groups.el);
  const seasonPane = h("div.pane.gap", { style: { gridColumn: "2" } }, h("div.cap", "SEASONS"), seasons.el);
  const rightPane = h("div.pane", { style: { gridColumn: "3" } }, rightCap, rows.el, tiles.el);
  const empty = h("div.center-note.hidden", h("div.note", "Nothing here yet. Add video folders or connect a server in settings."));
  const cols = h("div.cols", leftPane, seasonPane, rightPane, empty);
  const el = h("div.page", tabBar.el, cols);

  function showTab(t: string) {
    tab = t;
    group = null;
    season = undefined;
    load(false);
  }

  function load(keep: boolean) {
    const mine = ++seq;
    pending = (async () => {
      const v = await call<View>("videos.view", season === undefined ? { tab, group, view: bar.value } : { tab, group, season, view: bar.value });
      if (mine !== seq) return;
      view = v;
      tab = v.tab;
      group = v.group;
      season = v.season;
      tabBar.setVisible(v.tabs);
      tabBar.select(tab);
      count.textContent = v.counts;
      empty.classList.toggle("hidden", !v.empty);
      const grouped = v.groups != null, shows = tab === "shows";
      cols.style.gridTemplateColumns = `${grouped ? "300px" : "0"} ${shows ? "150px" : "0"} 1fr`;
      leftPane.classList.toggle("hidden", !grouped);
      seasonPane.classList.toggle("hidden", !shows);
      // with no list beside it, the grid lines up with the other pages' left edge
      rightPane.classList.toggle("gap", grouped);
      if (v.groups) {
        leftCap.textContent = v.groupCap;
        groups.rowHeight = v.groups.some(g => g.art) ? 70 : 32;
        groups.setItems(v.groups, true);
        groups.select(v.groups.findIndex(g => g.key === group));
      }
      if (shows && v.seasons) {
        seasons.setItems(v.seasons, true);
        seasons.select(v.seasons.findIndex(s => s.season === season));
      }
      items = v.items;
      rightCap.textContent = v.cap;
      const asRows = v.layout === "rows";
      rows.el.classList.toggle("hidden", !asRows);
      tiles.el.classList.toggle("hidden", asRows);
      if (asRows) rows.setItems(items, keep);
      else {
        tiles.kind = v.layout === "wide" ? "wide" : "poster";
        tiles.setItems(items.map(x => ({ key: String(x.id), title: x.title, caption: x.caption, art: x.art, item: x })));
      }
    })();
  }

  /** Plays one row and queues the rest of the list after it, so a season plays through. */
  function playFrom(v: Video) {
    void call("play.videos", { ids: items.map(x => x.id), start: v.id });
  }

  function menuFor(videos: Video[]): MenuEntry[] {
    const ids = videos.map(v => v.id);
    const inRows = view?.layout === "rows";
    return [
      { label: "Play", action: () => (videos.length === 1 && inRows ? playFrom(videos[0]) : void call("play.videos", { ids })) },
      { label: "Add to now playing", action: () => call("queue.videos", { ids }) },
      "-",
      { label: videos.length === 1 ? "Edit video info…" : `Edit info for ${videos.length} videos…`, action: () => editVideos(ids) },
      videos.length === 1 && videos[0].local ? { label: isMac ? "Show in Finder" : "Show in file manager", action: async () => { const p = await call<string | null>("video.path", { id: ids[0] }); if (p) void host.reveal(p); } } : null,
    ];
  }

  async function groupMenu(g: Group, x: number, y: number) {
    const t = tab;
    const ids = await call<number[]>("video.groupIds", { tab: t, key: g.key });
    showMenu([
      { label: "Play all", action: () => call("play.videos", { ids }) },
      { label: "Rename…", action: async () => {
        const name = await ask("Rename", "New name (use an existing name to merge the two)", g.name);
        if (!name || name.trim() === "") return;
        group = name.trim();
        await call("video.renameGroup", { tab: t, key: g.key, name });
      } },
      { label: `Edit info for all ${ids.length}…`, action: () => editVideos(ids) },
    ], x, y);
  }

  on("lib", debounce(() => { if (el.isConnected) load(true); else dirty = true; }));
  load(false);
  return { el, shown: () => { if (dirty) { dirty = false; load(true); } }, showTab, ready: () => pending, items: () => items, bar, tabRow: tabBar.el };
}
