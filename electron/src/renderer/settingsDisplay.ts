// The parts of settings → display about quickplay and artist photos, and the lyrics part of settings → translation
// and lyrics. As on Windows.
import { call } from "./api";
import { shell } from "./shell";
import { h } from "./ui";

type Display = typeof shell.display;

const check = (text: string, change: (on: boolean) => void) => {
  const box = h("input", { type: "checkbox" });
  box.onchange = () => change(box.checked);
  return { box, el: h("label.check", { style: { margin: "4px 0 0 0" } }, box, text) };
};

async function set(p: Partial<Display>) { shell.display = await call<Display>("display.set", p); return shell.display; }

export function quickplaySection() {
  const plays = check("Clicking an album in quickplay plays it straight away", on => void set({ quickplayPlays: on }));
  const el = h("div", h("div.label", { style: { margin: "22px 0 3px 0" } }, "QUICKPLAY"), plays.el,
    h("div.note", { style: { margin: "2px 0 0 24px" } }, "Off: a click opens the album in your collection; right-click a tile to play it."));
  return { el, shown: () => { plays.box.checked = shell.display.quickplayPlays; } };
}

export function photosSection() {
  const usage = h("span.dim");
  const photos = check("Show photos of the artist behind now playing", on => void set({ backdrops: on }));
  const clear = h("button.textbtn", { style: { marginLeft: "16px" }, onclick: async () => { shell.display = await call<Display>("backdrops.clear"); usage.textContent = shell.display.backdropUsage; } }, "remove kept photos");
  const el = h("div", h("div.label", { style: { margin: "22px 0 3px 0" } }, "ARTIST PHOTOS"), photos.el,
    h("div.note", { style: { margin: "2px 0 0 24px" } }, "The artist's name is sent to find them: to fanart.tv and TheAudioDB if you entered their keys under metadata (wide photos), otherwise to Deezer (a portrait). Photos are kept in Phoenix's data folder."),
    h("div", { style: { margin: "8px 0 0 24px", display: "flex", alignItems: "center" } }, usage, clear));
  return { el, shown: () => { photos.box.checked = shell.display.backdrops; usage.textContent = shell.display.backdropUsage; } };
}

export function lyricsSection() {
  const auto = check("Look lyrics up on LRCLIB by itself when a song has none", on => void set({ lyricsAuto: on }));
  const el = h("div", h("div.label", { style: { margin: "22px 0 3px 0" } }, "LYRICS"),
    h("div.note", "In now playing, the lyrics tab can find lyrics on LRCLIB (a free, open lyrics database, often with timing), take them from a .lrc or .txt file, or from the clipboard. Lyrics you import are kept in Phoenix's data folder, not in your music folders."),
    auto.el);
  return { el, shown: () => { auto.box.checked = shell.display.lyricsAuto; } };
}

/** Reads the display choices (once at start, and whenever settings is opened). */
export async function loadDisplay() { shell.display = await call<Display>("display.state"); }

interface Translate {
  on: boolean; to: string; provider: string; latin: boolean; libreUrl: string; hasDeepLKey: boolean; hasLibreKey: boolean; trouble: string;
  languages: { code: string; name: string }[]; providers: { id: string; name: string; keyHelp: string | null }[];
}

export function translationSection() {
  let T: Translate | null = null;
  const set = async (p: Record<string, unknown>) => paint(await call<Translate>("translate.set", p));
  const on = check("Show translated names beside the originals", v => void set({ on: v }));
  const latin = check("Also translate names written in Latin letters", v => void set({ latin: v }));
  const lang = h("select.box", { style: { width: "260px" } });
  lang.onchange = () => void set({ to: lang.value });
  const provider = h("select.box", { style: { width: "420px" } });
  provider.onchange = () => void set({ provider: provider.value });
  const key = h("input", { type: "password", spellcheck: false, style: { width: "300px" } });
  key.onchange = () => { if (T) void set(T.provider === "deepl" ? { deeplKey: key.value } : { libreKey: key.value }); key.value = ""; };
  const server = h("input", { type: "text", spellcheck: false, placeholder: "https://translate.example.org", style: { width: "300px" } });
  server.onchange = () => void set({ libreUrl: server.value });
  const keyRow = h("div", { style: { display: "flex", alignItems: "center", marginTop: "8px" } }, h("span.faint", { style: { width: "60px", fontSize: "12px" } }, "key"), key);
  const serverRow = h("div", { style: { display: "flex", alignItems: "center", marginTop: "8px" } }, h("span.faint", { style: { width: "60px", fontSize: "12px" } }, "server"), server);
  const help = h("div.faint", { style: { fontSize: "11px", marginTop: "4px" } });
  const status = h("div.statusline");

  function paint(t: Translate) {
    T = t;
    on.box.checked = t.on;
    latin.box.checked = t.latin;
    if (!lang.options.length) lang.replaceChildren(...t.languages.map(l => h("option", { value: l.code }, l.name)));
    if (!provider.options.length) provider.replaceChildren(...t.providers.map(x => h("option", { value: x.id }, x.name)));
    lang.value = t.to;
    provider.value = t.provider;
    keyRow.classList.toggle("hidden", t.provider === "mymemory");
    serverRow.classList.toggle("hidden", t.provider !== "libre");
    server.value = t.libreUrl;
    key.placeholder = (t.provider === "deepl" ? t.hasDeepLKey : t.hasLibreKey) ? "(saved)" : "key";
    help.textContent = t.providers.find(x => x.id === t.provider)?.keyHelp ?? "";
    status.textContent = t.trouble;
  }

  const el = h("div",
    h("div.note", "Phoenix can show a translation beside song, artist and album names that are written in another language, in a quieter colour after the original. Each name is translated once and remembered. The names are sent to the service you choose."),
    on.el, h("div.label", "TRANSLATE INTO"), lang, h("div.label", "SERVICE"), provider, keyRow, serverRow, help,
    h("div.note", { style: { marginTop: "6px" } }, "The Windows app can also translate offline, on the computer itself; that is not in this version yet."),
    latin.el,
    h("div.note", { style: { margin: "2px 0 0 24px" } }, "Off: only names in other scripts (Japanese, Korean, Russian, Arabic…) are sent. On: every name is sent, which uses a free service's daily limit quickly."),
    status);
  return { el, shown: async () => paint(await call<Translate>("translate.state")) };
}
