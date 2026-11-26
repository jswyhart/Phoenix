// settings → metadata, as on Windows: every source album details and covers can come from, switched on or off with a
// weight (the order they are asked in), their keys, writing changes into the files, filling in what albums lack, and
// AcoustID for identifying songs by their sound.
import { call, host, on } from "./api";
import { shell } from "./shell";
import { confirm, h, pill } from "./ui";

interface Source { id: string; name: string; gives: string; keyHelp: string | null; ready: boolean; enabled: boolean; weight: number; hasKey: boolean }
export interface MetaState { sources: Source[]; order: string[]; writeTags: boolean; hasAcoustKey: boolean; fpcalc: string; fpcalcFound: string; canIdentify: boolean }

export function metadataPanel() {
  let M: MetaState | null = null, filling = false;
  const list = h("div", { style: { marginTop: "6px" } });
  const order = h("div.dim", { style: { margin: "0 0 6px 0" } });
  const status = h("div.statusline");
  const writeTags = h("input", { type: "checkbox" });
  writeTags.onchange = async () => { shell.settings = await call("settings.set", { writeTags: writeTags.checked }); };
  const acoustKey = h("input", { type: "password", spellcheck: false, style: { width: "260px" } });
  const fpcalc = h("input", { type: "text", spellcheck: false });
  const fillBtn = pill("fill in albums missing info", () => void fill());
  fillBtn.title = "Asks your sources, by weight, about every album that has no cover, year or genre";
  on("meta.progress", (t: string) => { if (filling) status.textContent = t; });

  const saveAcoust = async () => {
    const p: Record<string, string> = {};
    if (acoustKey.value.trim() !== "") p.key = acoustKey.value;
    if (fpcalc.value.trim() !== (M?.fpcalc ?? "")) p.fpcalc = fpcalc.value;
    if (Object.keys(p).length === 0) return;
    paint(await call<MetaState>("meta.acoustid", p));
    acoustKey.value = "";
    status.textContent = M!.canIdentify ? "ready: right-click a song and choose “Identify by sound…”"
      : !M!.hasAcoustKey ? "AcoustID needs its key" : "fpcalc was not found at that place";
  };
  acoustKey.onblur = fpcalc.onchange = () => void saveAcoust();

  function paint(s: MetaState) {
    M = s;
    shell.canIdentify = s.canIdentify;
    writeTags.checked = s.writeTags;
    acoustKey.placeholder = s.hasAcoustKey ? "(saved)" : "application key";
    fpcalc.value = s.fpcalc;
    fpcalc.placeholder = s.fpcalcFound ? "found: " + s.fpcalcFound : "where fpcalc is";
    order.textContent = s.order.length === 0 ? "No source is switched on and ready." : "Asked in this order: " + s.order.join("  →  ") + ".";
    list.replaceChildren(...s.sources.map(src => {
      const box = h("input", { type: "checkbox", checked: src.enabled });
      box.onchange = async () => paint(await call<MetaState>("meta.setSource", { id: src.id, enabled: box.checked }));
      const weight = h("input", { type: "range", min: "0", max: "10", step: "1", value: String(src.weight), style: { width: "150px" } });
      const weightValue = h("span", { style: { width: "26px", marginLeft: "10px" } }, String(src.weight));
      weight.oninput = () => { weightValue.textContent = weight.value; };
      weight.onchange = async () => paint(await call<MetaState>("meta.setSource", { id: src.id, weight: Number(weight.value) }));
      const line = h("div", { style: { display: "flex", alignItems: "center", margin: "2px 0 0 24px" } },
        h("span.faint", { style: { fontSize: "12px", marginRight: "10px" } }, "weight"), weight, weightValue);
      if (src.keyHelp) {
        const key = h("input", { type: "password", spellcheck: false, placeholder: src.hasKey ? "(saved)" : "key", title: src.keyHelp, style: { width: "230px" } });
        key.onblur = async () => {
          if (key.value.trim() === "") return;
          paint(await call<MetaState>("meta.setSource", { id: src.id, key: key.value }));
          status.textContent = `${src.name} key saved`;
        };
        line.append(h("span.faint", { style: { fontSize: "12px", margin: "0 10px 0 18px" } }, "key"), key);
        if (!src.ready) line.append(h("span", { style: { color: "var(--accent)", fontSize: "12px", marginLeft: "12px" } }, "no key"));
      }
      return h("div.metasource", { style: { marginBottom: "14px" }, data: { source: src.id } },
        h("label.check", { style: { fontSize: "14px" } }, box, src.name),
        h("div.dim", { style: { fontSize: "12px", margin: "0 0 4px 24px" } }, src.gives),
        line,
        src.keyHelp ? h("div.faint", { style: { fontSize: "11px", margin: "4px 0 0 24px" } }, src.keyHelp) : null);
    }));
  }

  async function fill() {
    if (filling) { await call("meta.stop"); return; }
    if (!M || M.order.length === 0) { status.textContent = "switch on at least one source first (a source with a key needs its key)"; return; }
    const n = await call<number>("meta.fillCount");
    if (n === 0) { status.textContent = "every album already has a cover, year and genre"; return; }
    if (!(await confirm(`${n} albums are missing a cover, year or genre.\n\nPhoenix will ask your sources in order of weight (${M.order.join(", ")}) and fill in only what is missing${M.writeTags ? ", and write it into your music files" : ""}. Albums tied to one source from their right-click menu use only that source. This takes a few seconds per album.`, "fill in"))) return;
    filling = true;
    fillBtn.textContent = "stop";
    try { status.textContent = await call<string>("meta.fill"); }
    catch (e: any) { status.textContent = String(e.message ?? e); }
    finally { filling = false; fillBtn.textContent = "fill in albums missing info"; }
  }

  const el = h("div", h("div.h2", "metadata"),
    h("div.note", "Album details, track names and cover art can come from several places. Switch on the ones you want and give each a weight: when Phoenix fills in missing info it asks the highest weight first and moves down the list for whatever is still missing. Right-click an album to tie it to one source, or to look it up yourself. Every source except the folder pictures is sent the artist and album names."),
    h("div.label", "SOURCES"), list, order,
    h("label.check", { style: { margin: "10px 0 4px 0" } }, writeTags, "Also write changed info and cover art into my music files"),
    h("div", { style: { marginTop: "8px" } }, fillBtn), status,
    h("div.label", { style: { margin: "22px 0 3px 0" } }, "IDENTIFY SONGS BY SOUND (ACOUSTID)"),
    h("div.note", "For songs with missing or wrong names: Phoenix listens to the file and looks it up. This needs a free application key from acoustid.org and the fpcalc program from acoustid.org/chromaprint" + (host.platform === "mac" ? " (or: brew install chromaprint)" : host.platform === "linux" ? " (or your package manager's chromaprint, libchromaprint-tools or chromaprint-tools)" : "") + ". Then right-click a song and choose “Identify by sound…”."),
    h("div", { style: { display: "grid", gridTemplateColumns: "80px 1fr auto", alignItems: "center", rowGap: "8px" } },
      h("span.faint", { style: { fontSize: "12px" } }, "key"), acoustKey, h("span"),
      h("span.faint", { style: { fontSize: "12px" } }, "fpcalc"), fpcalc,
      (() => {
        const b = pill("BROWSE", async () => {
          const file = await host.openFile("Where is fpcalc?", []);
          if (!file) return;
          fpcalc.value = file;
          await saveAcoust();
        });
        b.style.margin = "0 0 0 10px";
        return b;
      })()));
  return { el, shown: async () => paint(await call<MetaState>("meta.state")), state: () => M };
}
