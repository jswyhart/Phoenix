// Song rows, their column headings, and the shared right-click menus for songs, albums and artists.
import { call, host, isMac, Song, time } from "./api";
import { shell } from "./shell";
import { h, MenuEntry, menuAt, textBtn } from "./ui";

const heartText = (r: number) => (r > 0 ? "♥" : r < 0 ? "✕" : "♡");

/** One song in a list: number down the list, title, (artist, album, tags), length, heart. */
export function songRow(s: Song, index: number, full: boolean): HTMLElement {
  const heart = h("div.heart", { title: "Like / dislike", data: { noselect: "1", noactivate: "1" } }, heartText(s.rating));
  if (s.rating === 0) heart.classList.add("unrated");
  heart.onclick = e => {
    e.stopPropagation();
    // show the new heart at once; the list reloads when the helper confirms
    s.rating = s.rating === 0 ? 1 : s.rating === 1 ? -1 : 0;
    heart.textContent = heartText(s.rating);
    heart.classList.toggle("unrated", s.rating === 0);
    void call("rate", { ids: [s.id], rating: s.rating });
  };
  const no = h("div.no", String(index + 1));
  const title = h("div.ell", s.title);
  const dur = h("div.time", time(s.dur));
  return full
    ? h("div.songrow.full", no, title, h("div.ell.dim", s.artist), h("div.ell.dim", s.album), h("div.ell.tags", s.tags), dur, heart)
    : h("div.songrow.compact", no, title, dur, heart);
}

/** Column headings for a full song list: click to sort, again to reverse, a third time for the original order. */
export class SongHeader {
  readonly el = h("div.songhead");
  key = "";
  desc = false;
  onChange: () => void = () => {};
  private cells = new Map<string, { b: HTMLButtonElement; label: string }>();

  constructor() {
    const add = (key: string, label: string, cls = "") => {
      const b = textBtn(label, () => { this.click(key); }, cls);
      this.cells.set(key, { b, label });
      this.el.append(b);
    };
    add("no", "#", "r"); add("title", "title"); add("artist", "artist"); add("album", "album"); add("tags", "tags"); add("time", "time", "r"); add("rating", "♥", "c");
    (this.cells.get("no")!.b.style.marginRight = "12px");
    this.el.addEventListener("contextmenu", e => {
      e.preventDefault();
      menuAt([
        ...shell.init.sortKeys.map(k => ({ label: "Sort by " + k.label, checked: this.key === k.key, action: () => this.set(k.key, this.key === k.key && !this.desc) })),
        "-",
        { label: "Original order", disabled: this.key === "", action: () => this.set("", false) },
      ], e);
    });
    this.paint();
  }

  private click(key: string) {
    if (this.key !== key) this.set(key, false);
    else if (!this.desc) this.set(key, true);
    else this.set("", false);
  }

  set(key: string, desc: boolean) {
    this.key = key;
    this.desc = desc;
    this.paint();
    this.onChange();
  }

  private paint() {
    for (const [key, { b, label }] of this.cells) {
      const active = this.key === key;
      b.textContent = active ? label + (this.desc ? " ▼" : " ▲") : label;
      b.classList.toggle("sorted", active);
    }
  }
}

const revealLabel = isMac ? "Show in Finder" : "Show in file manager";

/** Extra entries other modules add to the menus (playlists, editing, Discogs); filled in by app.ts. */
export const menuHooks = {
  addToPlaylist: (_ids: () => Promise<number[]> | number[]): MenuEntry => null,
  addToPhone: (_ids: () => Promise<number[]> | number[]): MenuEntry => null,
  editSongs: (_ids: number[], _album: boolean): void => {},
  discogs: (_albumKey: string): void => {},
};

/** The menu for one or more chosen songs. With one song, "Play" starts it and carries on down <context>. */
export function songMenu(songs: Song[], context: Song[] | null): MenuEntry[] {
  const ids = songs.map(s => s.id);
  return [
    { label: "Play", action: () => (context && songs.length === 1 ? call("play.tracks", { ids: context.map(s => s.id), start: ids[0] }) : call("play.tracks", { ids })) },
    { label: "Add to now playing", action: () => call("queue.tracks", { ids }) },
    menuHooks.addToPlaylist(() => ids),
    menuHooks.addToPhone(() => ids),
    "-",
    { label: "Rate", sub: [
      { label: "Like", action: () => call("rate", { ids, rating: 1 }) },
      { label: "Dislike", action: () => call("rate", { ids, rating: -1 }) },
      { label: "Clear rating", action: () => call("rate", { ids, rating: 0 }) },
    ] },
    { label: songs.length === 1 ? "Edit song info…" : `Edit info for ${songs.length} songs…`, action: () => menuHooks.editSongs(ids, false) },
    { label: "Find album info on Discogs…", action: async () => { const key = await call<string | null>("track.albumKey", { id: ids[0] }); if (key) menuHooks.discogs(key); } },
    songs.length === 1 && songs[0].local ? { label: revealLabel, action: async () => { const p = await call<string | null>("track.path", { id: ids[0] }); if (p) void host.reveal(p); } } : null,
  ];
}

export async function albumMenu(key: string): Promise<MenuEntry[]> {
  const a = await call<{ key: string; title: string; pinned: boolean; ids: number[] } | null>("album.info", { key });
  if (!a) return [];
  return [
    { label: "Play album", action: () => call("play.album", { key }) },
    { label: "Add to now playing", action: () => call("queue.album", { key }) },
    menuHooks.addToPlaylist(() => a.ids),
    menuHooks.addToPhone(() => a.ids),
    { label: a.pinned ? "Unpin from quickplay" : "Pin to quickplay", action: () => call("pin.toggle", { kind: "album", key, title: a.title }) },
    "-",
    { label: "Find album info on Discogs…", action: () => menuHooks.discogs(key) },
    { label: "Edit album info…", action: () => menuHooks.editSongs(a.ids, true) },
  ];
}

export async function artistMenu(artist: string): Promise<MenuEntry[]> {
  const [ids, pinned] = await Promise.all([call<number[]>("artist.tracks", { artist }), call<boolean>("pin.is", { kind: "artist", key: artist })]);
  return [
    { label: "Play all", action: () => call("play.tracks", { ids }) },
    { label: "Shuffle", action: () => call("play.tracks", { ids, shuffle: true }) },
    { label: "Play smart mix", action: () => call("artist.smartMix", { artist }) },
    { label: "Add to now playing", action: () => call("queue.tracks", { ids }) },
    menuHooks.addToPlaylist(() => ids),
    menuHooks.addToPhone(() => ids),
    { label: pinned ? "Unpin from quickplay" : "Pin to quickplay", action: () => call("pin.toggle", { kind: "artist", key: artist, title: artist }) },
  ];
}
