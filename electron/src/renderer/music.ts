// collection → music: artists or genres on the left, album covers in the middle, songs on the right.
import { call, on, Song, TileData } from "./api";
import { debounce, Page, shell } from "./shell";
import { albumMenu, artistMenu, SongHeader, songMenu, songRow, sourceFor } from "./songs";
import { h, menuAt, pill, showMenu, tabButton, tabs, TileGrid, ViewBar, VList } from "./ui";
import { attachServiceTabs } from "./web";
import { tr } from "./translate";

interface MusicView { counts: string; empty: boolean; left: string[] | null; sel: string | null; albums: TileData[] | null; album: string | null; songs: Song[] }

export interface MusicPage extends Page {
  setMode(mode: string): void; state(): { mode: string; left: string[]; albums: number; songs: Song[]; counts: string }; ready(): Promise<void>;
  bar: ViewBar; tabRow: HTMLElement;
  /** Shows an album: its artist on the left, the album chosen and its songs. */
  openAlbum(key: string): Promise<void>;
}

export function musicPage(): MusicPage {
  let mode = "artists", sel: string | null = null, album: string | null = null;
  let songs: Song[] = [], leftItems: string[] = [], source: 0 | 1 | 2 = 0;
  let seq = 0, dirty = false, pending: Promise<void> = Promise.resolve();

  const leftCap = h("div.cap", "ARTISTS"), midCap = h("div.cap", "ALBUMS"), rightCap = h("div.cap", "SONGS"), count = h("div.cap.count");
  const header = new SongHeader();
  header.el.classList.add("hidden");
  header.onChange = () => load(false);

  const left = new VList<string>({
    rowHeight: 30,
    render: name => h("div.name", tr(h("div.grow.ell"), name)),
    onSelect: () => { const i = left.selectedIndex(); sel = i <= 0 ? null : leftItems[i]; album = null; load(false); },
    onActivate: () => { if (songs.length) void call("play.tracks", { ids: songs.map(s => s.id) }); },
    onContext: async (_items, e) => { if (mode === "artists" && sel != null) { const x = e.clientX, y = e.clientY; showMenu(await artistMenu(sel), x, y); } },
  });
  const albums = new TileGrid<TileData>("square");
  albums.onClick = t => { album = album === t.key ? null : t.key; albums.select(album); load(false); };
  albums.onActivate = t => void call("play.album", { key: t.key });
  albums.onContext = async (t, e) => { const x = e.clientX, y = e.clientY; showMenu(await albumMenu(t.key), x, y); };
  const songList = new VList<Song>({
    rowHeight: 30, multi: true,
    render: (s, i) => songRow(s, i, mode === "songs", mode === "songs" ? 0 : source),
    onActivate: s => void call("play.tracks", { ids: songs.map(x => x.id), start: s.id }),
    onContext: (chosen, e) => menuAt(songMenu(chosen, chosen.length === 1 ? songs : null), e),
  });

  const tabBar = tabs([["artists", "artists"], ["genres", "genres"], ["albums", "albums"], ["songs", "songs"]], m => setMode(m));
  // drawn and lined up like the tabs beside it, as on Windows
  const shuffleAll = tabButton("shuffle all", () => void call("play.tracks", { ids: songs.map(s => s.id), shuffle: true }), "Shuffle the songs on show");
  const bar = new ViewBar("music", call);
  bar.onChange = () => load(false);
  tabBar.el.append(shuffleAll, count, bar.el);
  attachServiceTabs(tabBar.el, "music");

  const leftPane = h("div.pane", leftCap, left.el);
  const midPane = h("div.pane.gap", midCap, albums.el);
  const rightPane = h("div.pane.gap", rightCap, header.el, songList.el);
  const empty = h("div.center-note.hidden",
    h("div.h2", "your collection is empty"),
    h("div.note", "Add music folders, or connect a Jellyfin or Plex server, in settings."),
    pill("open settings", () => shell.navigate("settings")));
  // each pane keeps its own column, whichever of the others are hidden
  leftPane.style.gridColumn = "1";
  midPane.style.gridColumn = "2";
  rightPane.style.gridColumn = "3";
  const cols = h("div.cols", leftPane, midPane, rightPane, empty);
  const el = h("div.page", tabBar.el, cols);

  function setMode(m: string) {
    mode = m;
    sel = null;
    album = null;
    load(false);
  }

  function load(keep: boolean) {
    const mine = ++seq;
    pending = (async () => {
      const v = await call<MusicView>("music.view", { mode, sel, album, sortKey: header.key, sortDesc: header.desc, view: bar.value });
      if (mine !== seq) return;
      const showLeft = mode === "artists" || mode === "genres", showMid = mode !== "songs";
      tabBar.select(mode);
      cols.style.gridTemplateColumns = `${showLeft ? "230px" : "0"} ${showMid ? (mode === "albums" ? "1.6fr" : "1.25fr") : "0"} 1fr`;
      leftPane.classList.toggle("hidden", !showLeft);
      midPane.classList.toggle("hidden", !showMid);
      midPane.classList.toggle("gap", showLeft);
      rightPane.classList.toggle("gap", showMid);
      header.el.classList.toggle("hidden", mode !== "songs");
      empty.classList.toggle("hidden", !v.empty);
      count.textContent = v.counts;
      sel = v.sel;
      album = v.album;
      if (showLeft && v.left) {
        leftCap.textContent = `${mode === "artists" ? "ARTISTS" : "GENRES"}  ${v.left.length}`;
        leftItems = [mode === "artists" ? "all artists" : "all genres", ...v.left];
        left.setItems(leftItems, true);
        left.select(sel == null ? 0 : Math.max(0, leftItems.findIndex(n => n.toLowerCase() === sel!.toLowerCase())));
      }
      if (showMid && v.albums) {
        midCap.textContent = `ALBUMS  ${v.albums.length}`;
        albums.selectedKey = album;
        albums.setItems(v.albums.map(a => ({ ...a, item: a })));
      }
      songs = v.songs;
      rightCap.textContent = `SONGS  ${songs.length}`;
      source = sourceFor(songs);
      songList.setItems(songs, keep);
    })();
  }

  const reload = debounce(() => { if (el.isConnected) load(true); else dirty = true; });
  on("lib", reload);
  on("ratings", reload);
  load(false);

  return {
    el,
    shown: () => { if (dirty) { dirty = false; load(true); } },
    setMode,
    state: () => ({ mode, left: leftItems.slice(1), albums: albums.tiles.length, songs, counts: count.textContent ?? "" }),
    ready: () => pending,
    bar, tabRow: tabBar.el,
    openAlbum: async key => {
      const a = await call<{ artist: string } | null>("album.info", { key });
      if (!a) return;
      mode = "artists"; sel = a.artist; album = key;
      load(false);
      await pending;
    },
  };
}
