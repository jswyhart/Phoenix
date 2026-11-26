// collection → playlists: the automatic lists and your own on the left, their songs on the right. Also search results.
import { call, on, Song, TileData } from "./api";
import { debounce, Page } from "./shell";
import { albumMenu, menuHooks, SongHeader, songMenu, songRow } from "./songs";
import { ask, confirm, h, menuAt, MenuEntry, showMenu, textBtn, TileGrid, ViewBar, VList } from "./ui";
import { attachServiceTabs } from "./web";
import { editSmart, SmartRule } from "./tools";

interface ListRow { name: string; id: number | null; auto: string | null; smart: string | null; rules: string; pinned: boolean }

/** "Add to playlist ▸" for the song, album and artist menus. */
export function installPlaylistMenu() {
  let lists: ListRow[] = [];
  const refresh = async () => { lists = (await call<ListRow[]>("playlists.list")).filter(l => l.id != null); };
  void refresh();
  on("lib", debounce(() => void refresh(), 300));
  menuHooks.addToPlaylist = ids => ({
    label: "Add to playlist",
    sub: [
      { label: "New playlist…", action: async () => {
        const name = await ask("New playlist", "Playlist name", "");
        if (name && name.trim() !== "") await call("playlist.create", { name, ids: await ids() });
      } },
      lists.length ? "-" : null,
      ...lists.map(l => ({ label: l.name, action: async () => { await call("playlist.add", { id: l.id, ids: await ids() }); } })),
    ],
  });
}

export function playlistsPage(): Page {
  let rows: ListRow[] = [], row: ListRow | null = null, songs: Song[] = [], dirty = false, seq = 0;
  const songCap = h("div.cap", "SONGS");
  const header = new SongHeader();
  header.onChange = () => void show(false);
  const lists = new VList<ListRow>({
    rowHeight: 30,
    render: r => h("div.name", { title: r.rules || "" }, h("div.grow.ell", r.name), r.smart ? h("div.faint", { style: { fontSize: "11px", marginLeft: "8px" } }, "auto") : null),
    onSelect: items => { row = items[0] ?? null; void show(false); },
    onActivate: () => void call("play.tracks", { ids: songs.map(s => s.id) }),
    onContext: (items, e) => { row = items[0]; void show(false); menuAt(listMenu(items[0]), e); },
  });
  const songList = new VList<Song>({
    rowHeight: 30, multi: true,
    render: (s, i) => songRow(s, i, true),
    onActivate: s => void call("play.tracks", { ids: songs.map(x => x.id), start: s.id }),
    onContext: (chosen, e) => menuAt(songMenuFor(chosen), e),
  });
  const bar = new ViewBar("playlists", call);
  bar.onChange = () => void show(false);
  const actions = h("div", { style: { margin: "-6px 28px 6px 0", display: "flex", gap: "8px", alignItems: "center" } }, bar.el,
    textBtn("play", () => void call("play.tracks", { ids: songs.map(s => s.id) })),
    textBtn("shuffle", () => void call("play.tracks", { ids: songs.map(s => s.id), shuffle: true })));
  const newAuto = textBtn("new auto playlist", async () => { const id = await editSmart(null); if (id) { row = { name: "", id: null, auto: null, smart: id, rules: "", pinned: false }; await reload(); } });
  newAuto.title = "A playlist that fills itself by rules: genre, years, rating, how often or how lately played";
  const left = h("div.pane", h("div.split", h("div.cap", "PLAYLISTS"), h("div", { style: { margin: "-4px 4px 6px 0", display: "flex", gap: "12px" } }, textBtn("new playlist", create), newAuto)), lists.el);
  const rulesText = h("div.faint.hidden", { style: { fontSize: "12px", margin: "-4px 0 6px 8px" } });
  const right = h("div.pane", { style: { marginLeft: "18px" } }, h("div.split", songCap, actions), rulesText, header.el, songList.el);
  const serviceRow = h("div.tabs.hidden");
  attachServiceTabs(serviceRow, "playlists", () => serviceRow.classList.toggle("hidden", serviceRow.childElementCount === 0));
  const el = h("div.page", serviceRow, h("div.cols", { style: { gridTemplateColumns: "280px 1fr" } }, left, right));

  const same = (a: ListRow | null, b: ListRow) => !!a && a.auto === b.auto && a.id === b.id && a.smart === b.smart;

  async function reload() {
    rows = await call<ListRow[]>("playlists.list");
    row = rows.find(r => same(row, r)) ?? rows[0];
    lists.setItems(rows, true);
    lists.select(rows.indexOf(row));
    await show(true);
  }

  async function show(keep: boolean) {
    if (!row) return;
    const mine = ++seq;
    rulesText.textContent = row.rules;
    rulesText.classList.toggle("hidden", !row.smart);
    const v = await call<{ songs: Song[]; total: string; all: number }>("playlists.songs", { id: row.id, auto: row.auto, smart: row.smart, sortKey: header.key, sortDesc: header.desc, view: bar.value });
    if (mine !== seq) return;
    songs = v.songs;
    songCap.textContent = `SONGS  ${songs.length === v.all ? songs.length : `${songs.length} of ${v.all}`}` + (v.total ? `   ·   ${v.total}` : "");
    songList.setItems(songs, keep);
  }

  async function create() {
    const name = await ask("New playlist", "Playlist name", "");
    if (!name || name.trim() === "") return;
    const id = await call<number>("playlist.create", { name });
    row = { name: name.trim(), id, auto: null, smart: null, rules: "", pinned: false };
    await reload();
  }

  function listMenu(r: ListRow): MenuEntry[] {
    const ids = songs.map(s => s.id);
    const items: MenuEntry[] = [
      { label: "Play", action: () => call("play.tracks", { ids }) },
      { label: "Add to now playing", action: () => call("queue.tracks", { ids }) },
      menuHooks.addToPhone(() => ids),
    ];
    if (r.smart != null) {
      const sid = r.smart;
      items.push("-",
        { label: "Edit rules…", action: async () => { const rule = await call<SmartRule | null>("smart.get", { id: sid }); if (rule && (await editSmart(rule))) await reload(); } },
        { label: "Delete auto playlist", action: async () => {
          if (!(await confirm(`Delete the auto playlist “${r.name}”? The songs stay in your collection.`, "delete"))) return;
          row = null;
          await call("smart.delete", { id: sid });
        } });
    }
    if (r.id != null) {
      const id = r.id;
      items.push(
        { label: r.pinned ? "Unpin from quickplay" : "Pin to quickplay", action: () => call("pin.toggle", { kind: "playlist", key: String(id), title: r.name }) },
        "-",
        { label: "Rename…", action: async () => { const name = await ask("Rename playlist", "Playlist name", r.name); if (name && name.trim() !== "") await call("playlist.rename", { id, name }); } },
        { label: "Delete playlist", action: async () => {
          if (!(await confirm(`Delete the playlist “${r.name}”? The songs stay in your collection.`, "delete"))) return;
          row = null;
          await call("playlist.delete", { id });
        } });
    }
    return items;
  }

  function songMenuFor(chosen: Song[]): MenuEntry[] {
    const m = songMenu(chosen, chosen.length === 1 ? songs : null);
    if (row?.id == null || header.key !== "") return m;
    const id = row.id;
    const indexes = chosen.map(s => songs.indexOf(s)).sort((a, b) => a - b);
    const save = (ids: number[]) => call("playlist.setOrder", { id, ids });
    const move = (delta: number) => {
      const ids = songs.map(s => s.id);
      for (const i of delta < 0 ? indexes : [...indexes].reverse()) [ids[i], ids[i + delta]] = [ids[i + delta], ids[i]];
      return save(ids);
    };
    m.push("-",
      { label: "Move up", disabled: indexes[0] <= 0, action: () => move(-1) },
      { label: "Move down", disabled: indexes[indexes.length - 1] >= songs.length - 1, action: () => move(1) },
      { label: "Remove from playlist", action: () => save(songs.map(s => s.id).filter((_, i) => !indexes.includes(i))) });
    return m;
  }

  const changed = debounce(() => { if (el.isConnected) void reload(); else dirty = true; });
  on("lib", changed);
  on("ratings", () => { if (row?.auto === "liked") changed(); else if (el.isConnected) void show(true); });
  void reload();
  return { el, shown: () => { dirty = false; void reload(); } };
}

export interface SearchPage extends Page { search(query: string): void }

export function searchPage(): SearchPage {
  let songs: Song[] = [], query = "";
  const header = h("div.h2", { style: { margin: "0 0 12px 8px" } });
  const albumCap = h("div.cap", "ALBUMS"), songCap = h("div.cap", "SONGS");
  const albums = new TileGrid<TileData>("square");
  const songList = new VList<Song>({
    rowHeight: 30, multi: true,
    render: (s, i) => songRow(s, i, true),
    onActivate: s => void call("play.tracks", { ids: songs.map(x => x.id), start: s.id }),
    onContext: (chosen, e) => menuAt(songMenu(chosen, chosen.length === 1 ? songs : null), e),
  });
  albums.onClick = async t => { albums.select(t.key); setSongs(await call<Song[]>("album.songs", { key: t.key })); };
  albums.onActivate = t => void call("play.album", { key: t.key });
  albums.onContext = async (t, e) => { const x = e.clientX, y = e.clientY; showMenu(await albumMenu(t.key), x, y); };
  const el = h("div.page", header, h("div.cols", { style: { gridTemplateColumns: "1.1fr 1fr" } }, h("div.pane", albumCap, albums.el), h("div.pane.gap", songCap, songList.el)));

  function setSongs(list: Song[]) {
    songs = list;
    songCap.textContent = `SONGS  ${songs.length}`;
    songList.setItems(songs);
  }

  async function search(q: string) {
    query = q;
    const v = await call<{ albums: TileData[]; songs: Song[] }>("search", { query: q });
    if (query !== q) return;
    header.textContent = `results for “${q}”`;
    albumCap.textContent = `ALBUMS  ${v.albums.length}`;
    albums.selectedKey = null;
    albums.setItems(v.albums.map(a => ({ ...a, item: a })));
    setSongs(v.songs);
  }
  on("lib", debounce(() => { if (query !== "" && el.isConnected) void search(query); }));
  return { el, search: q => void search(q) };
}
