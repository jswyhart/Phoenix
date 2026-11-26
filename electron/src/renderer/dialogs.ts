// Edit song / album info, "Find album info" (from any metadata source, as on Windows), and identifying a song by its sound.
import { call } from "./api";
import { menuHooks } from "./songs";
import { shell } from "./shell";
import { art, confirm, dialogButtons, h, message, openDialog, pill, setArt, VList } from "./ui";

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

interface Result { index: number; title: string; details: string; thumb: string; fit: number }
interface Release { id: string; title: string; artist: string; cover: string; meta: string; tracks: string[] }
interface MetaSource { id: string; name: string; ready: boolean; enabled: boolean; keyHelp: string | null }

/** Finds an album in one of the metadata sources and applies its details, track names and cover. */
async function findAlbumInfo(key: string) {
  const album = await call<{ key: string; artist: string; title: string; source: string; tracks: string[]; tied: string; sources: MetaSource[] } | null>("meta.album", { key });
  if (!album) return;
  const d = openDialog(940, 640);
  let release: Release | null = null, chosen: Result | null = null;
  const offered = album.sources.filter(s => s.enabled || s.id === album.tied);
  let source = (offered.find(s => s.id === album.tied && s.ready) ?? offered.find(s => s.ready && s.id !== "folder") ?? offered.find(s => s.ready) ?? offered[0])?.id ?? "musicbrainz";
  const sourceBox = h("select.box", ...offered.map(s => h("option", { value: s.id, disabled: !s.ready }, s.ready ? s.name : s.name + " (needs its key)")));
  sourceBox.value = source;
  sourceBox.onchange = () => { source = sourceBox.value; compareCap.textContent = `YOUR SONGS  →  ${sourceName().toUpperCase()} TRACK NAMES`; chkTie.box.checked = source === album.tied; void search(); };
  const sourceName = () => offered.find(s => s.id === source)?.name ?? source;
  const artist = h("input", { type: "text", value: album.artist }), albumBox = h("input", { type: "text", value: album.title });
  const resultCap = h("div.cap", "RELEASES"), status = h("div.statusline");
  const relTitle = h("div", { style: { fontSize: "20px", fontWeight: "300", maxHeight: "56px", overflow: "hidden" } }), relArtist = h("div", { style: { fontSize: "14px" } }), relMeta = h("div.note");
  const cover = art(null, 300);
  const check = (label: string) => { const box = h("input", { type: "checkbox", checked: true }); return { box, el: h("label.check", { style: { marginRight: "18px" } }, box, label) }; };
  const chkAlbum = check("album title, artist, year, genre"), chkTitles = check("song titles"), chkCover = check("cover art");
  const chkTie = check("always use this source for this album");
  chkTie.box.checked = source === album.tied;
  chkTie.el.title = "Automatic fill-in will then ask only this source about this album";
  const compareCap = h("div.cap", `YOUR SONGS  →  ${sourceName().toUpperCase()} TRACK NAMES`);
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
    h("div.h2", "find album info"),
    h("div", { style: { display: "grid", gridTemplateColumns: "230px 1fr 1fr auto", columnGap: "10px", alignItems: "end", marginBottom: "14px", flex: "none" } },
      h("div", h("div.label", "SOURCE"), sourceBox), h("div", h("div.label", "ARTIST"), artist), h("div", h("div.label", "ALBUM"), albumBox), searchBtn),
    h("div", { style: { flex: "1 1 0", minHeight: "0", display: "grid", gridTemplateColumns: "1fr 1.15fr", columnGap: "18px" } },
      h("div.pane", resultCap, results.el),
      h("div.pane",
        h("div", { style: { display: "flex", marginBottom: "12px", flex: "none" } },
          h("div.thumb", { style: { width: "120px", height: "120px", marginRight: "14px" } }, cover),
          h("div", { style: { alignSelf: "center", minWidth: "0" } }, relTitle, relArtist, relMeta)),
        compareCap, compare.el)),
    h("div", { style: { display: "flex", justifyContent: "space-between", alignItems: "flex-end", marginTop: "14px", flex: "none" } },
      h("div", h("div", { style: { display: "flex" } }, chkAlbum.el, chkTitles.el, chkCover.el), h("div", { style: { display: "flex", marginTop: "6px" } }, chkTie.el), status),
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
    status.textContent = `searching ${sourceName()}…`;
    try {
      const list = await call<Result[]>("meta.search", { source, key, artist: artist.value, title: albumBox.value });
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
      const rel = await call<Release>("meta.release", { source, index: r.index });
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
      const failed = await call<number>("meta.apply", { key, source, id: release.id, albumInfo: chkAlbum.box.checked, titles: chkTitles.box.checked, cover: chkCover.box.checked, tie: chkTie.box.checked });
      d.close();
      if (failed > 0) await message(`Saved in Phoenix, but ${failed} file(s) couldn't be updated (in use or read-only).`);
    } catch (e: any) { status.textContent = String(e.message ?? e); applyBtn.disabled = false; }
  }

  showTracks(null);
  if (album.source !== "local") status.textContent = `This album comes from ${album.source}: changes are kept in Phoenix and survive the next sync; the server itself is not changed.`;
  if (!offered.some(s => s.id === source && s.ready)) status.textContent = "Switch on a source in settings → metadata first.";
  else void search();
}

/** Listens to a song and asks AcoustID what it is; offers to use what it finds. */
async function identify(id: number, title: string) {
  shell.setStatus(`listening to “${title}”…`);
  try {
    const found = await call<{ title: string; artist: string; album: string; score: number } | null>("meta.identify", { id });
    shell.setStatus("");
    if (!found) { await message("AcoustID does not know this recording."); return; }
    const sure = Math.round(found.score * 100);
    if (!(await confirm(`This sounds like:\n\n${found.title}\n${found.artist}${found.album ? "\n" + found.album : ""}\n\n(${sure}% sure)\n\nUse these details for the song?`, "use them"))) return;
    const info = await call<any>("tracks.info", { ids: [id] });
    const failed = await call<number>("tracks.save", {
      ids: [id], album: false, title: found.title, trackNo: info.trackNo, artist: found.artist || info.artist,
      albumName: info.album && info.album !== "Unknown Album" ? info.album : found.album, albumArtist: info.albumArtist && info.albumArtist !== info.artist ? info.albumArtist : found.artist || info.albumArtist,
      genre: info.genre, year: info.year,
    });
    shell.setStatus(failed > 0 ? "saved in Phoenix, but the file could not be updated" : `now “${found.title}” by ${found.artist}`);
  } catch (e: any) { shell.setStatus("couldn't identify the song: " + (e.message ?? e)); }
}

export function installDialogs() {
  menuHooks.editSongs = (ids, album) => void editSongs(ids, album);
  menuHooks.discogs = key => void findAlbumInfo(key);
  menuHooks.identify = (id, title) => void identify(id, title);
}
