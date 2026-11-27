// The Windows app's editing tools: the tag editor (many songs at once, with fixes that work on many rows), your own
// tags, Smart DJ, and the rules of an auto playlist.
import { call } from "./api";
import { shell } from "./shell";
import { menuHooks } from "./songs";
import { ask, confirm, dialogButtons, h, message, openDialog, pill } from "./ui";

// ---------- the fixes the tag editor can apply (as on Windows) ----------

const SMALL = new Set(["a", "an", "and", "as", "at", "but", "by", "for", "in", "nor", "of", "on", "or", "the", "to", "vs", "via", "with"]);

/** "the end of the road" becomes "The End of the Road"; words already written with capitals inside (McCartney, AC/DC) are left alone. */
export function titleCase(s: string): string {
  const words = s.split(" ");
  for (let i = 0; i < words.length; i++) {
    const w = words[i];
    if (w.length === 0 || [...w.slice(1)].some(c => c !== c.toLowerCase() && c === c.toUpperCase())) continue;
    const first = i === 0 || i === words.length - 1 || words[i - 1].endsWith(":") || words[i - 1].endsWith("-");
    const core = w.replace(/^[()[\]"',.!?]+|[()[\]"',.!?]+$/g, "");
    if (!first && SMALL.has(core.toLowerCase())) { words[i] = w.toLowerCase(); continue; }
    const at = [...w].findIndex(c => /[\p{L}\p{N}]/u.test(c));
    if (at >= 0) words[i] = w.slice(0, at) + w[at].toLocaleUpperCase() + w.slice(at + 1);
  }
  return words.join(" ");
}

/** Underscores to spaces, runs of spaces to one, no spaces at the ends, and a leading track number ("03 - ") removed. */
export function cleanUp(s: string, stripNumber: boolean): string {
  s = s.replace(/_/g, " ").replace(/\s+/g, " ").trim();
  if (stripNumber) s = s.replace(/^\d{1,3}\s*[-.)]\s+|^\d{2,3}\s+(?=\D)/, "");
  return s.trim();
}

export const PATTERNS = ["%n - %t", "%n. %t", "%n %t", "%a - %t", "%n - %a - %t", "%a - %b - %n - %t", "%t"];
/** A pattern as it is shown to you: "%n - %t" reads "number - title". */
export const inWords = (p: string) => "named:  " + p.replace(/%n/g, "number").replace(/%t/g, "title").replace(/%a/g, "artist").replace(/%b/g, "album");

/** Reads fields out of a file name by a pattern: %n track number, %t title, %a artist, %b album. Null if the name does not fit. */
export function fromFileName(name: string, pattern: string): Record<string, string> | null {
  const keys: string[] = [];
  const escaped = pattern.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const regex = "^" + escaped.replace(/%([ntab])/g, (_m, k: string) => { keys.push(k); return k === "n" ? "(\\d{1,3})" : "(.+?)"; }).replace(/ /g, "\\s*") + "$";
  const m = new RegExp(regex).exec(name.replace(/_/g, " ").trim());
  if (!m) return null;
  const found: Record<string, string> = {};
  keys.forEach((k, i) => (found[k] = m[i + 1].trim()));
  return found;
}

// ---------- the tag editor ----------

interface TagRow { id: number; title: string; artist: string; albumArtist: string; album: string; genre: string; year: number; trackNo: number; discNo: number; local: boolean; file: string; name: string }
const COLUMNS: [key: keyof TagRow, label: string, width: number, number: boolean][] = [
  ["trackNo", "#", 44, true], ["title", "Title", 240, false], ["artist", "Artist", 160, false], ["albumArtist", "Album artist", 140, false],
  ["album", "Album", 180, false], ["genre", "Genre", 100, false], ["year", "Year", 58, true], ["discNo", "Disc", 46, true],
];

export interface TagEditor { rows: TagRow[]; numberTracks(): void; tidy(): void; fromFileNames(pattern: string): { done: number; skipped: number }; replace(find: string, by: string): number; save(): Promise<number>; close(): void }
export let openEditor: TagEditor | null = null;

export async function tagEditor(ids: number[]): Promise<TagEditor | null> {
  const original: TagRow[] = await call("tags.rows", { ids });
  if (original.length === 0) return null;
  const rows: TagRow[] = original.map(r => ({ ...r }));
  const selected = new Set<number>();
  const d = openDialog(1180, 660);
  const status = h("div.dim", { style: { fontSize: "12px" } });
  const table = h("div.tagtable");
  const pattern = h("select.box", { style: { width: "220px" }, title: "How your files are named. Choose the layout that matches, then press FROM FILE NAMES to fill in the songs' details from their file names." },
    ...PATTERNS.map((p, i) => h("option", { value: String(i) }, inWords(p))));
  const changed = (r: TagRow, i: number) => COLUMNS.some(([k]) => r[k] !== original[i][k]);
  /** The rows a fix applies to: the ones selected, or all of them when fewer than two are. */
  const chosen = () => (selected.size > 1 ? rows.filter((_, i) => selected.has(i)) : rows);

  function paint() {
    table.replaceChildren(
      h("div.tagrow.head", ...COLUMNS.map(([, label, w]) => h("div", { style: { width: w + "px" } }, label)), h("div.grow", "File")),
      ...rows.map((r, i) => {
        const row = h("div.tagrow", { data: { i: String(i) } },
          ...COLUMNS.map(([k, , w, number]) => {
            const v = r[k] as string | number;
            const box = h("input", { type: "text", value: number ? (Number(v) > 0 ? String(v) : "") : String(v), style: { width: w + "px" }, spellcheck: false });
            box.onchange = () => { (r as any)[k] = number ? Math.max(0, parseInt(box.value, 10) || 0) : box.value; showStatus(); };
            box.onfocus = e => { if (!(e as FocusEvent).relatedTarget || !(e.target as HTMLElement).closest(".tagrow.sel")) pick(i, false, false); };
            return box;
          }),
          h("div.grow.faint.ell", { title: r.file }, r.file));
        row.classList.toggle("sel", selected.has(i));
        row.classList.toggle("changed", changed(r, i));
        row.addEventListener("mousedown", e => { if ((e.target as HTMLElement).tagName !== "INPUT" || e.ctrlKey || e.metaKey || e.shiftKey) pick(i, e.ctrlKey || e.metaKey, e.shiftKey); });
        return row;
      }));
    showStatus();
  }

  let anchor = -1;
  function pick(i: number, add: boolean, range: boolean) {
    if (range && anchor >= 0) { selected.clear(); for (let k = Math.min(i, anchor); k <= Math.max(i, anchor); k++) selected.add(k); }
    else if (add) { if (selected.has(i)) selected.delete(i); else selected.add(i); anchor = i; }
    else { selected.clear(); selected.add(i); anchor = i; }
    table.querySelectorAll<HTMLElement>(".tagrow[data-i]").forEach(el => el.classList.toggle("sel", selected.has(Number(el.dataset.i))));
  }

  function showStatus() {
    const n = rows.filter((r, i) => changed(r, i)).length;
    const servers = rows.filter(r => !r.local).length;
    status.textContent = (n === 0 ? "No changes yet." : `${n} song${n === 1 ? "" : "s"} changed.`)
      + "  Fixes apply to the selected rows, or to every row when fewer than two are selected."
      + (shell.settings.writeTags ? "" : "  Changes are kept in Phoenix only (writing to files is off in settings → metadata).")
      + (servers > 0 ? `  ${servers} come from a server: those changes stay in Phoenix.` : "");
    table.querySelectorAll<HTMLElement>(".tagrow[data-i]").forEach(el => el.classList.toggle("changed", changed(rows[Number(el.dataset.i)], Number(el.dataset.i))));
  }

  const editor: TagEditor = {
    rows,
    numberTracks: () => { let n = 0; for (const r of chosen()) r.trackNo = ++n; paint(); },
    tidy: () => { for (const r of chosen()) { r.title = titleCase(r.title); r.album = titleCase(r.album); } paint(); },
    fromFileNames: p => {
      let done = 0, skipped = 0;
      for (const r of chosen()) {
        const found = r.local ? fromFileName(r.name, p) : null;
        if (!found) { skipped++; continue; }
        if (found.n && /^\d+$/.test(found.n)) r.trackNo = parseInt(found.n, 10);
        if (found.t) r.title = found.t;
        if (found.a) r.artist = found.a;
        if (found.b) r.album = found.b;
        done++;
      }
      paint();
      return { done, skipped };
    },
    replace: (find, by) => {
      let n = 0;
      const swap = (s: string) => { const lower = s.toLowerCase(), f = find.toLowerCase(); if (!f || !lower.includes(f)) return s; n++; let out = "", at = 0, i; while ((i = s.toLowerCase().indexOf(f, at)) >= 0) { out += s.slice(at, i) + by; at = i + f.length; } return out + s.slice(at); };
      for (const r of chosen()) { r.title = swap(r.title); r.artist = swap(r.artist); r.albumArtist = swap(r.albumArtist); r.album = swap(r.album); r.genre = swap(r.genre); }
      paint();
      return n;
    },
    save: async () => {
      const list = rows.filter((r, i) => changed(r, i));
      return list.length === 0 ? 0 : await call<number>("tags.saveRows", { rows: list });
    },
    close: () => d.close(),
  };

  const fix = (label: string, run: () => void, tip: string) => { const b = pill(label, () => { (document.activeElement as HTMLElement | null)?.blur(); run(); }); b.title = tip; b.style.marginBottom = "6px"; return b; };
  d.body.append(
    h("div.h2", `file tags  ·  ${rows.length} song${rows.length === 1 ? "" : "s"}`),
    h("div.note", { style: { margin: "0 0 8px 0" } }, "File tags are saved into the music files themselves, so they go wherever the files go. (Search tags, Phoenix's own #labels, are kept in its library: right-click → Edit search tags.)"),
    h("div", { style: { display: "flex", flexWrap: "wrap", alignItems: "center", flex: "none" } },
      fix("NUMBER TRACKS", editor.numberTracks, "Numbers the songs 1, 2, 3… in the order shown"),
      fix("TIDY CAPITALS", editor.tidy, "Title Case for titles and albums"),
      fix("CLEAN UP", () => { for (const r of chosen()) { r.title = cleanUp(r.title, true); r.artist = cleanUp(r.artist, false); r.albumArtist = cleanUp(r.albumArtist, false); r.album = cleanUp(r.album, false); r.genre = cleanUp(r.genre, false); } paint(); },
        "Removes underscores, extra spaces and a track number at the start of a title"),
      fix("FIND & REPLACE…", async () => {
        const find = await ask("Find and replace", "Find (in titles, artists, albums and genres)", "");
        if (!find) return;
        const by = await ask("Find and replace", `Replace “${find}” with`, "");
        if (by === null) return;
        editor.replace(find, by);
      }, "Replaces text in titles, artists, albums and genres"),
      fix("COPY DOWN", () => {
        const list = chosen();
        if (list.length < 2) return;
        const f = list[0];
        for (const r of list.slice(1)) { r.artist = f.artist; r.albumArtist = f.albumArtist; r.album = f.album; r.genre = f.genre; r.year = f.year; r.discNo = f.discNo; }
        paint();
      }, "Gives every chosen song the first chosen song's artist, album artist, album, genre, year and disc"),
      fix("ALBUM ARTIST = ARTIST", () => { for (const r of chosen()) r.albumArtist = r.artist; paint(); }, "Sets each song's album artist to its artist"),
      pattern,
      fix("FROM FILE NAMES", () => {
        const r = editor.fromFileNames(PATTERNS[Number(pattern.value)]);
        if (r.skipped > 0) shell.setStatus(`${r.done} read from their file names; ${r.skipped} did not fit the pattern`);
      }, "Reads the fields out of each file's name by the pattern on the left"),
      fix("MATCH ALBUM ONLINE…", async () => {
        const key = await call<string | null>("track.albumKey", { id: chosen()[0].id });
        if (!key) return;
        if (rows.some((r, i) => changed(r, i)) && !(await confirm("Looking the album up applies its details straight away. Save your changes first?", "save and look up"))) return;
        await editor.save();
        d.close();
        menuHooks.discogs(key);
      }, "Looks the album up (MusicBrainz and your other sources) and applies its names and cover")),
    h("div", { style: { flex: "1 1 0", minHeight: "0", overflow: "auto", border: "1px solid var(--line)", margin: "8px 0 0 0" } }, table),
    h("div", { style: { display: "flex", alignItems: "center", justifyContent: "space-between", marginTop: "10px", flex: "none" } }, status,
      dialogButtons(d, "SAVE", async () => {
        (document.activeElement as HTMLElement | null)?.blur();
        const failed = await editor.save();
        d.close();
        if (failed > 0) await message(`Saved in Phoenix, but ${failed} file(s) couldn't be updated (in use or read-only).`);
      }, "CANCEL")));
  (d.body.lastElementChild!.lastElementChild as HTMLElement).style.margin = "0 0 0 16px";
  d.onEnter = undefined;
  openEditor = editor;
  void d.closed.then(() => { if (openEditor === editor) openEditor = null; });
  paint();
  return editor;
}

// ---------- your own tags ----------

async function editTags(ids: number[], what: string) {
  const t = await call<{ common: string[]; all: { tag: string; count: number }[] }>("owntags.get", { ids });
  const d = openDialog(560);
  const box = h("input", { type: "text", value: t.common.join(", "), spellcheck: false });
  let hint = `Search tags for ${what}: Phoenix's own labels, kept in its library and not written into the files (for the files' own title, artist, genre and so on, use Edit file tags). Separate them with commas. Search finds them by plain words or as #tag (use #"two words" for a tag with a space).`;
  if (ids.length > 1) hint += " Tags you add go on every selected item; tags you delete from the box come off every selected item.";
  d.body.append(h("div.h2", "search tags"), h("div.note", hint), box,
    h(t.all.length ? "div.cap" : "div.hidden", { style: { margin: "14px 0 6px 0" } }, "YOUR TAGS"),
    h("div", { style: { display: "flex", flexWrap: "wrap", maxHeight: "120px", overflowY: "auto" } }, ...t.all.map(x => {
      const b = pill(`${x.tag}  ${x.count}`, () => {
        const now = box.value.split(",").map(s => s.trim()).filter(s => s);
        if (!now.some(s => s.toLowerCase() === x.tag.toLowerCase())) now.push(x.tag);
        box.value = now.join(", ");
        box.focus();
      });
      b.style.cssText += "padding: 3px 8px; margin: 0 6px 6px 0; font-size: 12px;";
      return b;
    })),
    dialogButtons(d, "save", async () => { await call("owntags.apply", { ids, text: box.value, before: t.common }); d.close(); }));
  box.focus();
}

// ---------- auto playlists made by rules ----------

export interface SmartRule { id: string; name: string; genre: string; artist: string; yearFrom: number; yearTo: number; rating: number; notPlayedDays: number; addedDays: number; minPlays: number; maxPlays: number; sort: number; limit: number }

export async function editSmart(rule: SmartRule | null): Promise<string | null> {
  const choices = await call<{ ratings: string[]; sorts: string[] }>("smart.choices");
  const r: SmartRule = rule ?? { id: "", name: "auto playlist", genre: "", artist: "", yearFrom: 0, yearTo: 0, rating: 0, notPlayedDays: 0, addedDays: 0, minPlays: 0, maxPlays: -1, sort: 0, limit: 50 };
  const d = openDialog(560);
  const num = (n: number) => (n > 0 ? String(n) : "");
  const text = (v: string, short = false) => h(`input${short ? ".short" : ""}` as "input", { type: "text", value: v, spellcheck: false });
  const name = text(r.name), genre = text(r.genre), artist = text(r.artist), from = text(num(r.yearFrom), true), to = text(num(r.yearTo), true);
  const notPlayed = text(num(r.notPlayedDays), true), added = text(num(r.addedDays), true), min = text(num(r.minPlays), true), max = text(r.maxPlays < 0 ? "" : String(r.maxPlays), true), limit = text(num(r.limit), true);
  const select = (items: string[], v: number) => { const s = h("select.box", { style: { width: "200px" } }, ...items.map((x, i) => h("option", { value: String(i) }, x))); s.value = String(v); return s; };
  const rating = select(choices.ratings, r.rating), sort = select(choices.sorts, r.sort);
  const count = h("div.dim", { style: { marginTop: "14px" } });
  const field = (label: string, el: HTMLElement, hint?: string) => h("div.field", h("span", label), hint ? h("div", { style: { display: "flex", alignItems: "center" } }, el, h("span.faint", { style: { fontSize: "12px", marginLeft: "10px" } }, hint)) : el);
  const int = (b: HTMLInputElement, blank = 0) => { const n = parseInt(b.value.trim(), 10); return Number.isFinite(n) && n >= 0 ? n : blank; };
  const read = (): SmartRule => ({
    id: r.id, name: name.value.trim(), genre: genre.value, artist: artist.value, yearFrom: int(from), yearTo: int(to), rating: Number(rating.value),
    notPlayedDays: int(notPlayed), addedDays: int(added), minPlays: int(min), maxPlays: int(max, -1), sort: Number(sort.value), limit: int(limit),
  });
  let saved: string | null = null;
  d.body.append(h("div.h2", rule ? "auto playlist rules" : "new auto playlist"),
    field("Name", name), field("Genre contains", genre), field("Artist contains", artist),
    field("Years", h("div", { style: { display: "flex", alignItems: "center" } }, from, h("span", { style: { margin: "0 10px" } }, "to"), to)),
    field("Rating", rating), field("Not played in", notPlayed, "days (blank: no rule)"), field("Added in the last", added, "days (blank: no rule)"),
    field("Played at least", min, "times"), field("Played at most", max, "times (blank: no limit, 0: never played)"),
    field("Order", sort), field("Keep the first", limit, "songs (blank: all of them)"),
    h("div.faint", { style: { fontSize: "12px", marginTop: "10px" } }, "Several genres or artists can be separated with commas. An auto playlist fills itself: it changes as your collection, ratings and listening do."),
    count,
    dialogButtons(d, "ok", async () => {
      if (name.value.trim() === "") { name.focus(); return; }
      saved = await call<string>("smart.save", read());
      d.close();
    }));
  const recount = async () => { count.textContent = await call<string>("smart.count", read()); };
  d.body.querySelectorAll("input, select").forEach(e => e.addEventListener("input", () => void recount()));
  d.body.querySelectorAll("select").forEach(e => e.addEventListener("change", () => void recount()));
  void recount();
  name.focus();
  name.select();
  await d.closed;
  return saved;
}

export function installTools() {
  menuHooks.tagEditor = ids => void (async () => { await tagEditor(Array.isArray(ids) ? ids : await ids()); })();
  menuHooks.editTags = (ids, what) => void editTags(ids, what);
  menuHooks.smartDj = ids => void call<string>("dj.start", { ids }).then(t => shell.setStatus(t));
}
