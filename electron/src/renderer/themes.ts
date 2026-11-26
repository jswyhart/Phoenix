// Themes, as on Windows and in the same files: the colours and fonts the window is drawn with, chosen and designed in
// settings → display. A theme's file edited by hand shows as soon as it is saved.
import { call, host, on } from "./api";
import { shell } from "./shell";
import { confirm, dialogButtons, h, message, openDialog, pill } from "./ui";

export interface ThemeShape { id: string; name: string; base: string; builtIn: boolean; textFont: string; headFont: string; own: Record<string, string>; colours: Record<string, string> }
interface ThemeKey { key: string; group: string; label: string; tip: string }

/** "#RRGGBB" or "#AARRGGBB" (as WPF writes them) to a CSS colour. */
export function cssColour(hex: string): string {
  let h0 = hex.trim().replace(/^#/, "");
  if (h0.length === 3 || h0.length === 4) h0 = [...h0].map(c => c + c).join("");
  if (h0.length === 8) {
    const a = parseInt(h0.slice(0, 2), 16) / 255, r = parseInt(h0.slice(2, 4), 16), g = parseInt(h0.slice(4, 6), 16), b = parseInt(h0.slice(6, 8), 16);
    return `rgba(${r}, ${g}, ${b}, ${Math.round(a * 1000) / 1000})`;
  }
  return "#" + h0;
}

const VARS: Record<string, string> = {
  Bg: "--bg", BgAlt: "--bg-alt", Fg: "--fg", FgDim: "--fg-dim", FgFaint: "--fg-faint", Line: "--line", Hover: "--hover", Selected: "--selected", Accent: "--accent",
  NowBg: "--now-bg", NowBgAlt: "--now-bg-alt", NowFg: "--now-fg", NowFgDim: "--now-fg-dim", NowFgFaint: "--now-fg-faint", NowLine: "--now-line",
  NowHover: "--now-hover", NowSelected: "--now-selected", NowHighlight: "--now-highlight",
};

export let currentTheme: ThemeShape | null = null;

const systemDark = matchMedia("(prefers-color-scheme: dark)");
let following = false;

/** Draws the window in a theme. "system" draws light or dark as the operating system is set, and keeps following it. */
export function applyTheme(t: ThemeShape) {
  if (t.id === "system") {
    following = true;
    void host.themeBase("system", "").catch(() => {});
    void call<ThemeShape>("themes.get", { id: systemDark.matches ? "dark" : "light" }).then(base => { if (following) draw({ ...base, id: "system", name: "system", builtIn: true }); });
    return;
  }
  following = false;
  draw(t);
}

systemDark.addEventListener("change", () => { if (following && currentTheme?.id === "system") applyTheme(currentTheme); });

function draw(t: ThemeShape) {
  currentTheme = t;
  const root = document.documentElement;
  root.dataset.theme = t.base === "dark" ? "dark" : "light";
  const bg = t.colours.Bg.replace(/^#/, "");
  // a system theme leaves the system's own choice in charge of the title bar
  if (!following) void host.themeBase(t.base, "#" + (bg.length === 8 ? bg.slice(2) : bg)).catch(() => {});
  for (const [key, v] of Object.entries(VARS)) root.style.setProperty(v, cssColour(t.colours[key]));
  root.style.setProperty("--grad", `linear-gradient(90deg, ${cssColour(t.colours.AccentStart)}, ${cssColour(t.colours.AccentEnd)})`);
  const font = (f: string) => (f.trim() ? `"${f.trim().replace(/"/g, "")}"` : `"Selawik"`);
  root.style.setProperty("--text-font", font(t.textFont));
  root.style.setProperty("--head-font", font(t.headFont));
}

export async function loadTheme() {
  applyTheme(await call<ThemeShape>("themes.current"));
  on("theme", (t: ThemeShape) => { if (!designing) applyTheme(t); });
}

// ---------- contrast (as on Windows) ----------

function rgba(hex: string): [number, number, number, number] {
  let h0 = hex.replace(/^#/, "");
  if (h0.length === 6) h0 = "FF" + h0;
  return [parseInt(h0.slice(2, 4), 16), parseInt(h0.slice(4, 6), 16), parseInt(h0.slice(6, 8), 16), parseInt(h0.slice(0, 2), 16) / 255];
}
const over = (c: [number, number, number, number], under: [number, number, number, number]): [number, number, number, number] =>
  [0, 1, 2].map(i => Math.round(c[i] * c[3] + under[i] * (1 - c[3]))).concat(1) as [number, number, number, number];
const luminance = (c: number[]) => { const l = (v: number) => { const s = v / 255; return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4); }; return 0.2126 * l(c[0]) + 0.7152 * l(c[1]) + 0.0722 * l(c[2]); };
export const contrast = (a: number[], b: number[]) => { const x = luminance(a), y = luminance(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
const PAIRS: [string, string, number, string][] = [
  ["Fg", "Bg", 4.5, "text on the page"], ["Fg", "BgAlt", 4.5, "text on panels"], ["FgDim", "Bg", 3.0, "quieter text on the page"], ["FgFaint", "Bg", 1.8, "faint text on the page"],
  ["Accent", "Bg", 2.5, "the accent on the page"], ["Accent", "BgAlt", 2.5, "the accent on panels"], ["NowFg", "NowBg", 4.5, "now playing text"],
  ["NowFgDim", "NowBg", 3.0, "now playing quieter text"], ["NowHighlight", "NowBg", 2.5, "the playing song in the queue"],
];
/** Pairs in a theme that are hard to read, worst first. */
export function problems(colours: Record<string, string>): string[] {
  const black: [number, number, number, number] = [0, 0, 0, 1];
  return PAIRS.map(([fore, back, need, what]) => {
    let bg = rgba(colours[back]);
    if (back === "BgAlt") bg = over(bg, rgba(colours.Bg));
    const ratio = contrast(over(rgba(colours[fore]), over(bg, black)), over(bg, black));
    return { what, ratio, need };
  }).filter(p => p.ratio < p.need).sort((a, b) => a.ratio / a.need - b.ratio / b.need).map(p => `${p.what} is hard to read (${p.ratio.toFixed(1)} : 1, wants ${p.need})`);
}

// ---------- the designer ----------

let designing = false;

/** Changes a theme's colours and fonts, or makes a new theme from it. Shows each change at once. */
export async function designTheme(id: string): Promise<ThemeShape | null> {
  // "system" is light or dark as the system is set: a new theme starts from whichever is showing
  if (id === "system") id = systemDark.matches ? "dark" : "light";
  const [start, keys] = await Promise.all([call<ThemeShape>("themes.get", { id }), call<ThemeKey[]>("themes.keys")]);
  const before = currentTheme;
  let own: Record<string, string> = { ...start.own }, base = start.base;
  const d = openDialog(720, 680);
  designing = true;
  const name = h("input", { type: "text", value: start.builtIn ? `my ${start.name}` : start.name, spellcheck: false });
  const baseBox = h("select.box", { style: { width: "140px" } }, h("option", { value: "light" }, "light"), h("option", { value: "dark" }, "dark"));
  baseBox.value = base;
  const textFont = h("input", { type: "text", value: start.textFont, placeholder: "Selawik (built in)", spellcheck: false });
  const headFont = h("input", { type: "text", value: start.headFont, placeholder: "Selawik (built in)", spellcheck: false });
  const grid = h("div.themekeys");
  const warnings = h("div.themeproblems");
  let shown: ThemeShape = start;

  const preview = async () => {
    shown = await call<ThemeShape>("themes.preview", { id: start.id, name: name.value, base, own, textFont: textFont.value, headFont: headFont.value });
    applyTheme(shown);
    warnings.textContent = problems(shown.colours).join("\n");
    paint();
  };

  function paint() {
    let group = "";
    grid.replaceChildren();
    for (const k of keys) {
      if (k.group !== group) { group = k.group; grid.append(h("div.group", group.toUpperCase())); }
      const hex = shown.colours[k.key].replace(/^#/, "");
      const alpha = hex.length === 8 ? Math.round((parseInt(hex.slice(0, 2), 16) / 255) * 100) : 100;
      const rgb = "#" + (hex.length === 8 ? hex.slice(2) : hex).toLowerCase();
      const colour = h("input", { type: "color", value: rgb, title: k.tip });
      const opacity = h("input.alpha", { type: "number", min: "0", max: "100", value: String(alpha), title: "How solid (100: not see-through)" });
      const set = () => {
        const a = Math.max(0, Math.min(100, Number(opacity.value) || 0));
        const aa = Math.round((a / 100) * 255).toString(16).padStart(2, "0").toUpperCase();
        own[k.key] = "#" + (a >= 100 ? "" : aa) + colour.value.slice(1).toUpperCase();
        void preview();
      };
      colour.onchange = set;
      opacity.onchange = set;
      const reset = h("button.textbtn", { title: "Back to the base's colour", onclick: () => { delete own[k.key]; void preview(); } }, k.key in own ? "reset" : "");
      grid.append(h("div", { title: k.tip }, k.label), h("div.faint", { style: { fontSize: "11px" } }, k.tip), colour, opacity, reset);
    }
  }

  baseBox.onchange = () => { base = baseBox.value; void preview(); };
  textFont.onchange = headFont.onchange = () => void preview();
  let saved: ThemeShape | null = null;
  const save = async (asNew: boolean) => {
    if (name.value.trim() === "") { name.focus(); return; }
    saved = await call<ThemeShape>("themes.save", { id: start.id, asNew, name: name.value, base, own, textFont: textFont.value, headFont: headFont.value });
    d.close();
  };
  const field = (label: string, el: HTMLElement) => h("div.field", h("span", label), el);
  d.body.append(h("div.h2", "customise theme"),
    field("Name", name), field("Base", baseBox), field("Text font", textFont), field("Heading font", headFont),
    h("div", { style: { flex: "1 1 0", minHeight: "0", overflowY: "auto", marginTop: "8px" } }, grid),
    warnings,
    h("div", { style: { display: "flex", justifyContent: "space-between", alignItems: "center", marginTop: "12px", flex: "none" } },
      h("div.faint", { style: { fontSize: "12px" } }, "Changes show as you make them. Your themes are small text files you can also edit by hand."),
      h("div", { style: { display: "flex" } },
        start.builtIn ? null : pill("save", () => void save(false)),
        pill(start.builtIn ? "save as a new theme" : "save as new", () => void save(true)),
        pill("cancel", d.close))));
  (d.body.lastElementChild!.lastElementChild!.lastElementChild as HTMLElement).style.marginRight = "0";
  d.onEnter = undefined;
  await preview();
  await d.closed;
  designing = false;
  if (saved) applyTheme(saved); else if (before) applyTheme(before);
  return saved;
}

// ---------- settings → display → theme ----------

export function themeSection() {
  const box = h("select.box", { style: { width: "260px" } });
  const noteEl = h("div.note", { style: { marginTop: "6px" } });
  let folder = "";
  async function refresh() {
    const l = await call<{ current: string; themes: { id: string; name: string; base: string }[]; folder: string }>("themes.list");
    folder = l.folder;
    box.replaceChildren(...l.themes.map(t => h("option", { value: t.id }, t.name)));
    box.value = l.current;
    const t = currentTheme;
    exportBtn.disabled = deleteBtn.disabled = !t || t.builtIn;
    const p = t ? problems(t.colours) : [];
    noteEl.textContent = p.length ? p.join("\n") : t && !t.builtIn ? "This theme is a file in the themes folder: edit it there and Phoenix shows the change as soon as you save." : "";
  }
  box.onchange = async () => {
    const t = await call<ThemeShape>("themes.use", { id: box.value });
    applyTheme(t);
    shell.settings = await call("settings.get");
    await refresh();
  };
  const customise = pill("CUSTOMISE", async () => { if (await designTheme(box.value)) await refresh(); });
  customise.title = "Change this theme's colours and fonts, or make a new theme from it";
  customise.style.marginLeft = "10px";
  const text = (label: string, tip: string, run: () => void) => h("button.textbtn", { title: tip, onclick: run, style: { marginRight: "16px" } }, label);
  const exportBtn = text("export", "Save this theme as a file to share", async () => {
    const file = await host.saveFile("Export theme", `${currentTheme?.name ?? "theme"}.json`, [{ name: "Phoenix theme", extensions: ["json"] }]);
    if (file) await call("themes.export", { id: box.value, file });
  });
  const deleteBtn = text("delete", "Remove this theme (Phoenix goes back to its base, light or dark)", async () => {
    if (!(await confirm(`Delete the theme “${currentTheme?.name}”?`, "delete"))) return;
    applyTheme(await call<ThemeShape>("themes.delete", { id: box.value }));
    await refresh();
  });
  const el = h("div", h("div.label", "THEME"),
    h("div", { style: { display: "flex", alignItems: "center", marginTop: "4px" } }, box, customise),
    h("div", { style: { display: "flex", marginTop: "8px" } },
      text("import", "Add a theme file someone gave you", async () => {
        const file = await host.openFile("Import a theme", [{ name: "Phoenix theme", extensions: ["json"] }]);
        if (!file) return;
        try { applyTheme(await call<ThemeShape>("themes.import", { file })); await refresh(); } catch (e: any) { await message(String(e.message ?? e)); }
      }),
      exportBtn, deleteBtn,
      text("open themes folder", "Your themes are small text files you can also edit by hand: Phoenix shows a change as soon as you save it", () => void host.openPath(folder))),
    noteEl);
  on("theme", () => void refresh());
  return { el, shown: refresh };
}
