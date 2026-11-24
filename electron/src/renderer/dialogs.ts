// Edit song / album info, and "Find album info on Discogs".
import { call } from "./api";
import { menuHooks } from "./songs";
import { art, dialogButtons, h, message, openDialog, pill, setArt, VList } from "./ui";

/** Edits one song, or the shared fields of several songs / a whole album. Blank shared fields are left unchanged. */
async function editSongs(ids: number[], album: boolean) {
  const info = await call<any>("tracks.info", { ids });
  if (!info) return;
  const single = info.count === 1 && !album;
  const d = openDialog(500);
  const text = (value: string, short = false, enabled = true) => h(`input${short ? ".short" : ""}` as "input", { type: "text", value, disabled: !enabled });
  const title = text(single ? info.title : "", false, single), trackNo = text(single ? info.trackNo : "", true, single);
  const artist = text(info.artist), albumBox = text(info.album), albumArtist = text(info.albumArtist), genre = text(info.genre), year = text(info.year, true);
  const field = (label: string, el: HTMLElement) => h("div.field", h("span", label), el);
  let hint = single ? "" : "Changes apply to every selected song. Fields left blank keep their current values. ";
  if (info.source !== "local") hint += `This music comes from ${info.source}: your changes are kept in Phoenix and survive the next sync; the server itself is not changed.`;
  d.body.append(h("div.h2", single ? "edit song info" : album ? "edit album info" : `edit ${info.count} songs`), h(hint ? "div.note" : "div.hidden", hint),
    field("Song title", title), field("Song artist", artist), field("Album", albumBox), field("Album artist", albumArtist), field("Genre", genre), field("Year", year), field("Track number", trackNo),
    dialogButtons(d, "save", async () => {
      const failed = await call<number>("tracks.save", { ids, album, title: title.value, trackNo: trackNo.value, artist: artist.value, albumName: albumBox.value, albumArtist: albumArtist.value, genre: genre.value, year: year.value });
      d.close();
      if (failed > 0) await message(`Saved in Phoenix, but ${failed} file(s) couldn't be updated (in use or read-only).`);
    }));
  (single ? title : artist).focus();
}

interface Result { id: number; title: string; details: string; thumb: string }
interface Release { id: number; title: string; artist: string; cover: string; meta: string; tracks: string[] }

/** Finds an album on Discogs and applies its details, track names and cover. */
async function discogs(key: string) {
  const album = await call<{ key: string; artist: string; title: string; source: string; tracks: string[]; hasToken: boolean } | null>("discogs.album", { key });
  if (!album) return;
  const d = openDialog(940, 620);
  let release: Release | null = null, chosen: Result | null = null;
  const artist = h("input", { type: "text", value: album.artist }), albumBox = h("input", { type: "text", value: album.title });
  const resultCap = h("div.cap", "RELEASES"), status = h("div.statusline");
  const relTitle = h("div", { style: { fontSize: "20px", fontWeight: "300", maxHeight: "56px", overflow: "hidden" } }), relArtist = h("div", { style: { fontSize: "14px" } }), relMeta = h("div.note");
  const cover = art(null, 300);
  const check = (label: string) => { const box = h("input", { type: "checkbox", checked: true }); return { box, el: h("label.check", { style: { marginRight: "18px" } }, box, label) }; };
  const chkAlbum = check("album title, artist, year, genre"), chkTitles = check("song titles"), chkCover = check("cover art");
  const results = new VList<Result>({
    rowHeight: 60,
    render: r => h("div", h("div.thumb", { style: { width: "48px", height: "48px", marginRight: "12px" } }, art(r.thumb, 100)),
      h("div.grow", h("div.ell", r.title), h("div.ell.dim", { style: { fontSize: "12px" } }, r.details))),
    onSelect: sel => { if (sel[0]) void loadRelease(sel[0]); },
  });
  const compare = new VList<{ no: string; mine: string; theirs: string }>({
    rowHeight: 26, plain: true,
    render: c => h("div", h("div.faint", { style: { width: "28px", flex: "none" } }, c.no), h("div.grow.ell.dim", { style: { marginRight: "10px" } }, c.mine), h("div.grow.ell", c.theirs)),
  });
  const searchBtn = pill("search", search), applyBtn = pill("apply to album", apply), closeBtn = pill("close", d.close);
  searchBtn.style.marginRight = "0";
  closeBtn.style.marginRight = "0";
  applyBtn.disabled = true;
  d.onEnter = search;
  d.body.append(
    h("div", { style: { display: "grid", gridTemplateColumns: "1fr 1fr auto", columnGap: "10px", alignItems: "end", marginBottom: "14px", flex: "none" } },
      h("div", h("div.label", "ARTIST"), artist), h("div", h("div.label", "ALBUM"), albumBox), searchBtn),
    h("div", { style: { flex: "1 1 0", minHeight: "0", display: "grid", gridTemplateColumns: "1fr 1.15fr", columnGap: "18px" } },
      h("div.pane", resultCap, results.el),
      h("div.pane",
        h("div", { style: { display: "flex", marginBottom: "12px", flex: "none" } },
          h("div.thumb", { style: { width: "120px", height: "120px", marginRight: "14px" } }, cover),
          h("div", { style: { alignSelf: "center", minWidth: "0" } }, relTitle, relArtist, relMeta)),
        h("div.cap", "YOUR SONGS  →  DISCOGS TRACK NAMES"), compare.el)),
    h("div", { style: { display: "flex", justifyContent: "space-between", alignItems: "flex-end", marginTop: "14px", flex: "none" } },
      h("div", h("div", { style: { display: "flex" } }, chkAlbum.el, chkTitles.el, chkCover.el), status),
      h("div", { style: { display: "flex", flex: "none" } }, applyBtn, closeBtn)));

  function showTracks(rel: Release | null) {
    const mine = album!.tracks, theirs = rel?.tracks ?? [];
    compare.setItems(Array.from({ length: Math.max(mine.length, theirs.length) }, (_, i) => ({ no: String(i + 1), mine: mine[i] ?? "—", theirs: theirs[i] ?? "—" })));
    if (!rel) return;
    const same = mine.length === theirs.length;
    chkTitles.box.disabled = !same;
    if (!same) chkTitles.box.checked = false;
    chkCover.box.disabled = rel.cover === "";
    if (rel.cover === "") chkCover.box.checked = false;
    status.textContent = same ? "Pick the release that matches your copy." : `This release has ${theirs.length} tracks but your album has ${mine.length}, so song titles won't be changed.`;
  }

  async function search() {
    searchBtn.disabled = true;
    status.textContent = "searching Discogs…";
    try {
      const list = await call<Result[]>("discogs.search", { artist: artist.value, album: albumBox.value });
      results.setItems(list);
      resultCap.textContent = `RELEASES  ${list.length}`;
      status.textContent = list.length === 0 ? "No releases found. Try a shorter album or artist name." : "Pick the release that matches your copy.";
      if (list.length > 0) { results.select(0); void loadRelease(list[0]); }
    } catch (e: any) { status.textContent = String(e.message ?? e); }
    searchBtn.disabled = false;
  }

  async function loadRelease(r: Result) {
    chosen = r;
    applyBtn.disabled = true;
    try {
      const rel = await call<Release>("discogs.release", { id: r.id });
      if (chosen !== r) return;   // selection moved on while loading
      release = rel;
      relTitle.textContent = rel.title;
      relArtist.textContent = rel.artist;
      relMeta.textContent = rel.meta;
      setArt(cover, rel.cover || null, 300);
      showTracks(rel);
      applyBtn.disabled = false;
    } catch (e: any) { status.textContent = String(e.message ?? e); }
  }

  async function apply() {
    if (!release) return;
    applyBtn.disabled = true;
    status.textContent = "applying…";
    try {
      const failed = await call<number>("discogs.apply", { key, id: release.id, albumInfo: chkAlbum.box.checked, titles: chkTitles.box.checked, cover: chkCover.box.checked });
      d.close();
      if (failed > 0) await message(`Saved in Phoenix, but ${failed} file(s) couldn't be updated (in use or read-only).`);
    } catch (e: any) { status.textContent = String(e.message ?? e); applyBtn.disabled = false; }
  }

  showTracks(null);
  if (album.source !== "local") status.textContent = `This album comes from ${album.source}: changes are kept in Phoenix and survive the next sync; the server itself is not changed.`;
  if (!album.hasToken) status.textContent = "Add your Discogs personal access token in settings first.";
  else void search();
}

export function installDialogs() {
  menuHooks.editSongs = (ids, album) => void editSongs(ids, album);
  menuHooks.discogs = key => void discogs(key);
}
