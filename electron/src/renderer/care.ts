// Library care, as on Windows (settings → collection → find duplicates and missing files): the same song stored twice,
// found by name and length or by what it sounds like, and songs whose files have gone.
import { call, host, on } from "./api";
import { Page, shell } from "./shell";
import { confirm, h, message, pill, tabs, textBtn } from "./ui";

interface DupTrack { id: number; title: string; artist: string; album: string; path: string; describe: string }
interface Group { index: number; why: string; best: number; tracks: DupTrack[] }
interface Dups { groups: Group[]; canListen: boolean }
interface Missing { tracks: { id: number; title: string; artist: string; album: string; path: string; found: string }[]; found: number }

export interface CarePage extends Page { show(tab: string): Promise<void>; groups(): Group[]; missing(): Missing["tracks"] }

export function carePage(): CarePage {
  let tab = "dups", groups: Group[] = [], canListen = false, missing: Missing = { tracks: [], found: 0 };
  const keep = new Map<number, number>();   // group index -> the copy to keep
  const tabBar = tabs([["dups", "duplicates"], ["missing", "missing files"]], t => void show(t));
  const actions = h("div", { style: { display: "flex", flexWrap: "wrap", rowGap: "6px", margin: "0 0 0 8px" } });
  const status = h("div.statusline", { style: { margin: "10px 28px 6px 8px" } });
  const list = h("div", { style: { margin: "0 28px 20px 8px" } });
  const el = h("div.page", h("div.zunehead", "library care"), tabBar.el, actions, status,
    h("div", { style: { overflowY: "auto", minHeight: "0", flex: "1 1 0" } }, list));
  on("care.progress", (t: string) => { status.textContent = t; });

  async function busy<T>(what: string, work: () => Promise<T>): Promise<T | undefined> {
    status.textContent = what;
    actions.querySelectorAll("button").forEach(b => (b.disabled = true));
    try { return await work(); }
    catch (e: any) { status.textContent = String(e.message ?? e); return undefined; }
    finally { actions.querySelectorAll("button").forEach(b => (b.disabled = false)); }
  }

  /** Files of copies taken out of the library may go to the bin as well. */
  async function bin(files: string[]) {
    let failed = 0;
    for (const f of files) if (!(await host.trash(f))) failed++;
    return failed;
  }

  function paintDups(d: Dups) {
    groups = d.groups;
    canListen = d.canListen;
    keep.clear();
    for (const g of groups) keep.set(g.index, g.best);
    actions.replaceChildren(...[
      pill("FIND BY NAME", () => void busy("looking…", async () => { paintDups(await call<Dups>("care.byName")); status.textContent = `${groups.length} group${groups.length === 1 ? "" : "s"} of songs with the same artist, title and length`; })),
      canListen ? pill("CHECK THESE BY SOUND", () => void busy("listening…", async () => { paintDups(await call<Dups>("care.confirm")); status.textContent = "checked by sound"; })) : null,
      canListen ? pill("COMPARE EVERYTHING BY SOUND", () => void busy("listening…", async () => { paintDups(await call<Dups>("care.bySound")); status.textContent = `${groups.length} groups of the same recording`; })) : null,
      groups.length > 0 ? pill("KEEP THE BEST OF EACH", () => void resolveAll()) : null].filter((b): b is HTMLButtonElement => !!b));
    actions.querySelectorAll<HTMLElement>(".pill").forEach(p => (p.title = ({
      "FIND BY NAME": "Songs with the same artist, title and length",
      "CHECK THESE BY SOUND": "Listens to each group to tell real copies from different versions",
      "COMPARE EVERYTHING BY SOUND": "Listens to every song on this computer; finds copies whose names differ. Slow the first time.",
      "KEEP THE BEST OF EACH": "In every group, keeps the best copy and takes the others out of the library",
    } as Record<string, string>)[p.textContent ?? ""] ?? ""));
    list.replaceChildren(
      ...(canListen ? [] : [h("div.note", "To compare songs by what they sound like, Phoenix needs Chromaprint's fpcalc (settings → metadata).")]),
      ...(groups.length === 0 ? [h("div.dim", { style: { marginTop: "10px" } }, "No duplicates found.")] : groups.map(g => {
        const name = `dup${g.index}`;
        return h("div.dupgroup", { style: { margin: "14px 0", paddingBottom: "10px", borderBottom: "1px solid var(--line)" } },
          h("div", { style: { fontSize: "15px" } }, `${g.tracks[0].artist} – ${g.tracks[0].title}`),
          h("div.faint", { style: { fontSize: "12px", marginBottom: "6px" } }, g.why),
          ...g.tracks.map(t => {
            const radio = h("input", { type: "radio", name, checked: t.id === g.best, title: "Keep this copy" });
            radio.onchange = () => keep.set(g.index, t.id);
            return h("label.check", { style: { margin: "3px 0", alignItems: "flex-start" } }, radio,
              h("div", { style: { minWidth: "0" } }, h("div", `${t.album}  ·  ${t.describe}`), h("div.faint.ell", { style: { fontSize: "11.5px" }, title: t.path }, t.path)));
          }),
          h("div", { style: { display: "flex", gap: "18px", marginTop: "4px" } },
            textBtn("keep marked, remove the rest from the library", () => void resolve(g, false)),
            textBtn(`…and move their files to the ${host.platform === "mac" ? "Trash" : host.platform === "win" ? "Recycle Bin" : "bin"}`, () => void resolve(g, true)),
            textBtn("not duplicates", async () => paintDups(await call<Dups>("care.dismiss", { group: g.index })))));
      })));
  }

  async function resolve(g: Group, toBin: boolean) {
    const k = keep.get(g.index) ?? g.best;
    const others = g.tracks.filter(t => t.id !== k).length;
    if (toBin && !(await confirm(`Take ${others} cop${others === 1 ? "y" : "ies"} out of the library and move ${others === 1 ? "its file" : "their files"} to the bin?\n\nTheir plays, like and playlist places go to the copy you keep.`, "remove"))) return;
    const r = await call<{ files: string[]; state: Dups }>("care.resolve", { group: g.index, keep: k });
    const failed = toBin ? await bin(r.files) : 0;
    paintDups(r.state);
    status.textContent = failed ? `${failed} file(s) couldn't be moved to the bin` : `kept one copy; ${r.files.length} taken out of the library`;
  }

  async function resolveAll() {
    const n = groups.length;
    if (!(await confirm(`In each of the ${n} groups, keep the best copy (lossless first, then the highest bitrate) and take the others out of the library? Their files stay where they are.`, "keep the best"))) return;
    const r = await call<{ files: string[]; groups: number; state: Dups }>("care.resolveAll");
    paintDups(r.state);
    status.textContent = `kept the best copy in ${r.groups} groups; ${r.files.length} taken out of the library`;
  }

  function paintMissing(m: Missing) {
    missing = m;
    actions.replaceChildren(...[
      pill("LOOK FOR MOVED FILES", () => void busy("looking through your music folders…", async () => { paintMissing(await call<Missing>("care.locate")); status.textContent = `${missing.found} found`; })),
      m.tracks.length > 0 ? pill("REMOVE ALL FROM LIBRARY", async () => {
        if (!(await confirm(`Take the ${m.tracks.length} missing songs out of the library?`, "remove"))) return;
        paintMissing(await call<Missing>("care.removeMissing", { ids: [] }));
      }) : null].filter((b): b is HTMLButtonElement => !!b));
    list.replaceChildren(
      ...(m.found > 0 ? [pill(`POINT THE ${m.found} FOUND SONG${m.found === 1 ? "" : "S"} AT THEIR NEW PLACES`, async () => {
        const r = await call<{ done: number; state: Missing }>("care.relinkFound");
        paintMissing(r.state);
        status.textContent = `${r.done} song${r.done === 1 ? "" : "s"} found again; plays, likes and playlists are kept`;
      })] : []),
      ...(m.tracks.length === 0 ? [h("div.dim", { style: { marginTop: "10px" } }, "Every song's file is where it should be.")] : m.tracks.map(t =>
        h("div.missingrow", { style: { margin: "10px 0" } },
          h("div", { style: { fontSize: "14px" } }, `${t.artist} – ${t.title}`),
          h("div.faint", { style: { fontSize: "11.5px" } }, "was at  " + t.path),
          t.found ? h("div", { style: { fontSize: "11.5px", color: "var(--accent)" } }, "found at  " + t.found) : null,
          h("div", { style: { display: "flex", gap: "18px", marginTop: "2px" } },
            textBtn("choose the file…", async () => {
              const file = await host.openFile("Where is " + t.title + "?", [{ name: "Music", extensions: ["mp3", "m4a", "flac", "wav", "ogg", "opus", "aac", "wma", "m4b"] }]);
              if (!file) return;
              try { paintMissing(await call<Missing>("care.relink", { id: t.id, path: file })); } catch (e: any) { await message(String(e.message ?? e)); }
            }),
            textBtn("remove from library", async () => paintMissing(await call<Missing>("care.removeMissing", { ids: [t.id] }))))))));
  }

  async function show(t: string) {
    tab = t === "missing" ? "missing" : "dups";
    tabBar.select(tab);
    status.textContent = "";
    if (tab === "dups") paintDups(groups.length ? { groups, canListen } : await call<Dups>("care.byName"));
    else paintMissing(await call<Missing>("care.missing"));
  }

  void show("dups");
  return { el, shown: () => { shell.setStatus(""); }, show, groups: () => groups, missing: () => missing.tracks };
}
