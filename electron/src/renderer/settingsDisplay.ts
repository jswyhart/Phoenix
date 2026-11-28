// The parts of settings → display about quickplay and artist photos, and the lyrics part of settings → translation
// and lyrics. As on Windows.
import { call, host, on } from "./api";
import { artUrl } from "./ui";
import { shell } from "./shell";
import { confirm, h } from "./ui";

type Display = typeof shell.display;

const check = (text: string, change: (on: boolean) => void) => {
  const box = h("input", { type: "checkbox" });
  box.onchange = () => change(box.checked);
  return { box, el: h("label.check", { style: { margin: "4px 0 0 0" } }, box, text) };
};

async function set(p: Partial<Display>) { shell.display = await call<Display>("display.set", p); return shell.display; }

export function traySection() {
  const where = host.platform === "mac" ? "the menu bar" : "the system tray";
  const keep = check(`Keep Phoenix running in ${where}`, on => void set({ runInTray: on }).then(d => host.tray(d.runInTray)));
  const el = h("div", h("div.label", { style: { margin: "22px 0 3px 0" } }, "WHEN THE WINDOW IS CLOSED"), keep.el,
    h("div.note", { style: { margin: "2px 0 0 24px" } }, `On: closing the window hides it and the music carries on. Phoenix's icon in ${where} brings it back; its menu plays, pauses, skips or quits. Off: closing the window quits Phoenix.`));
  return { el, shown: () => { keep.box.checked = shell.display.runInTray; } };
}

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

export interface SpeechState {
  models: { id: string; name: string; mb: number; note: string; have: boolean }[]; model: string; recommended: string; ready: boolean;
  gpus: { index: number; name: string; kind: string; memoryMb: number }[]; gpu: number; listen: boolean; translate: boolean; ranOn: string;
  downloading: boolean; writingAll: boolean; allStatus: string; listeningTo: number;
}

export function lyricsSection() {
  const auto = check("Look lyrics up on LRCLIB by itself when a song has none", on => void set({ lyricsAuto: on }));
  // the speech model that writes lyrics, as on Windows
  const modelBox = h("select.box.speechmodel", { style: { width: "360px" } });
  const gpuBox = h("select.box.speechgpu", { style: { width: "360px" }, title: "Automatic uses the separate graphics card if there is one, otherwise the built-in graphics" });
  const status = h("div.statusline");
  let st: SpeechState | null = null, progress = "";
  const picked = () => st?.models.find(m => m.id === modelBox.value);
  const download = h("button.pill", { style: { marginLeft: "10px" }, onclick: async () => { if (picked()) paint(await call<SpeechState>("speech.download", { id: modelBox.value })); } }, "DOWNLOAD");
  const del = h("button.textbtn", { style: { marginLeft: "8px" }, title: "Remove the downloaded model to free its space", onclick: async () => {
    const m = picked();
    if (m && (await confirm(`Delete the ${m.name} speech model (${m.mb >= 1000 ? `${(m.mb / 1024).toFixed(1)} GB` : `${m.mb} MB`})? Lyrics it already wrote are kept.`, "delete"))) paint(await call<SpeechState>("speech.delete", { id: m.id }));
  } }, "delete");
  const listen = check("Write lyrics with the speech model as songs play, for songs that have no lyrics", on => void call<SpeechState>("speech.set", { listen: on }).then(paint));
  const translateLines = check("Translate what the speech model writes, line by line as it writes it", on => void call<SpeechState>("speech.set", { translate: on }).then(paint));
  const all = h("button.pill", { onclick: async () => paint(await call<SpeechState>("speech.writeAll")) }, "WRITE LYRICS FOR SONGS THAT HAVE NONE");
  modelBox.onchange = async () => { const m = picked(); if (m?.have) paint(await call<SpeechState>("speech.set", { model: m.id })); else paint(st!); };
  gpuBox.onchange = async () => paint(await call<SpeechState>("speech.set", { gpu: Number(gpuBox.value) }));
  function paint(s: SpeechState) {
    const keep = modelBox.value;
    st = s;
    const size = (mb: number) => mb >= 1000 ? `${(mb / 1024).toFixed(1)} GB` : `${mb} MB`;
    modelBox.replaceChildren(...s.models.map(m => h("option", { value: m.id }, `${m.name}   (${size(m.mb)}${m.have ? ", downloaded" : ""}${m.id === s.recommended ? ", recommended here" : ""}): ${m.note}`)));
    modelBox.value = keep && s.models.some(m => m.id === keep) ? keep : s.model || s.recommended;
    gpuBox.replaceChildren(h("option", { value: "-2" }, "automatic"),
      ...s.gpus.map(g => h("option", { value: String(g.index) }, `${g.name}   (${g.kind}${g.kind === "separate" && g.memoryMb > 0 ? `, ${Math.round(g.memoryMb / 1024)} GB` : ""})`)),
      h("option", { value: "-1" }, "the processor only"));
    gpuBox.value = String(s.gpu);
    if (gpuBox.selectedIndex < 0) gpuBox.value = "-2";
    const m = picked();
    download.textContent = s.downloading ? "STOP" : "DOWNLOAD";
    download.style.display = m?.have && !s.downloading ? "none" : "";
    del.style.display = m?.have ? "" : "none";
    listen.box.checked = s.listen;
    translateLines.box.checked = s.translate;
    listen.box.disabled = !m?.have;
    all.textContent = s.writingAll ? "STOP" : "WRITE LYRICS FOR SONGS THAT HAVE NONE";
    (all as HTMLButtonElement).disabled = !m?.have && !s.writingAll;
    status.textContent = s.downloading ? progress : s.writingAll || s.allStatus ? s.allStatus
      : m?.have ? (s.ranOn ? `ready; it last ran on ${s.ranOn}` : "ready") : `download the model to use it (${size(m?.mb ?? 0)})`;
  }
  on("speech.progress", (p: { id: string; done: number }) => { progress = `downloading: ${Math.round(p.done * 100)}%`; if (st?.downloading) status.textContent = progress; });
  on("speech", (e: { state: SpeechState; error: string | null }) => { paint(e.state); if (e.error) status.textContent = "couldn't download the model: " + e.error; });
  const row = (label: string, ...rest: HTMLElement[]) => h("div", { style: { display: "flex", alignItems: "center", marginTop: "8px" } },
    h("span", { style: { width: "110px", flex: "none", color: "var(--fg-dim)" } }, label), ...rest);
  const el = h("div", h("div.label", { style: { margin: "22px 0 3px 0" } }, "LYRICS"),
    h("div.note", "In now playing, the lyrics tab can find lyrics on LRCLIB (a free, open lyrics database, often with timing), take them from a .lrc or .txt file, or from the clipboard. Lyrics you import are kept in Phoenix's data folder, not in your music folders."),
    auto.el,
    h("div.label", { style: { margin: "18px 0 3px 0" } }, "WRITING LYRICS WITH A SPEECH MODEL"),
    h("div.note", `A speech model (whisper.cpp) can listen to a song and write down what is sung, with timing, on this computer: nothing is sent anywhere. It works best on clear vocals and can mishear words, especially under loud music. ${host.platform === "mac" ? "It runs on the graphics of Macs with Apple chips (Metal) and on the processor of Intel Macs." : "It runs on the graphics card when it can (AMD, Intel or NVIDIA, built in or separate) and on the processor otherwise."} Models are downloaded once from whisper.cpp's page on Hugging Face.`),
    row("model", modelBox, download, del),
    host.platform === "mac" ? h("div") : row("runs on", gpuBox),
    h("div", { style: { marginTop: "6px" } }, listen.el),
    h("div", { style: { marginTop: "6px" } }, translateLines.el),
    h("div.note", { style: { margin: "2px 0 0 24px" } }, "Each line of the “generated” tab gets its translation under it, into the language chosen under translation, by the service chosen there. Lines already in that language are left as they are. Lyrics are many words: MyMemory's free daily allowance runs out after a few songs."),
    h("div.note", { style: { margin: "2px 0 0 24px" } }, "What the model writes is shown on a tab of its own in now playing, “generated”, next to lyrics. The tab is there while a model is downloaded and chosen."),
    h("div", { style: { marginTop: "10px" } }, all),
    status);
  return { el, shown: async () => { auto.box.checked = shell.display.lyricsAuto; paint(await call<SpeechState>("speech.state")); } };
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

// ---------- a background picture of your own (as on Windows) ----------

let backEl: HTMLElement | null = null;

/** Shows the picture of your own behind the window, or none. */
export function applyBackground() {
  if (!backEl) { backEl = h("div.backpic.hidden"); document.body.prepend(backEl); }
  const d = shell.display;
  backEl.classList.toggle("hidden", !d.backgroundPicture);
  if (d.backgroundPicture) {
    backEl.style.backgroundImage = `url("${artUrl(d.backgroundPicture, 2400)}")`;
    backEl.style.opacity = String(d.backgroundStrength / 100);
  }
}

export function backgroundSection() {
  const note = h("div.note", { style: { marginTop: "6px" } });
  const strength = h("input.backstrength", { type: "range", min: "5", max: "90", step: "5", style: { width: "220px" } });
  const strengthText = h("span.dim", { style: { marginLeft: "10px" } });
  const remove = h("button.textbtn", { style: { marginLeft: "8px" }, onclick: async () => { await set({ backgroundClear: true } as any); paint(); } }, "remove");
  const row = h("div", { style: { display: "flex", alignItems: "center", marginTop: "10px" } },
    h("span", { style: { width: "130px", flex: "none", color: "var(--fg-dim)" } }, "how much it shows"), strength, strengthText);
  const choose = h("button.pill", { onclick: async () => {
    const file = await host.openFile("A picture behind Phoenix's window", [{ name: "Pictures", extensions: ["jpg", "jpeg", "png", "bmp", "gif", "webp"] }]);
    if (!file) return;
    try { await set({ backgroundFile: file } as any); } catch (e: any) { note.textContent = String(e.message ?? e); return; }
    paint();
  } }, "CHOOSE PICTURE");
  strength.oninput = () => { strengthText.textContent = `${strength.value}%`; if (backEl) backEl.style.opacity = String(Number(strength.value) / 100); };
  strength.onchange = async () => { await set({ backgroundStrength: Number(strength.value) }); paint(); };
  function paint() {
    const d = shell.display, have = !!d.backgroundPicture;
    applyBackground();
    remove.style.display = row.style.display = have ? "" : "none";
    strength.value = String(d.backgroundStrength);
    strengthText.textContent = `${d.backgroundStrength}%`;
    note.textContent = have
      ? "The picture shows through the theme's background everywhere but now playing. Phoenix keeps its own copy in the data folder."
      : "Choose a picture to show behind Phoenix's window, through the theme's background.";
  }
  const el = h("div", h("div.label", { style: { margin: "22px 0 3px 0" } }, "BACKGROUND PICTURE"),
    h("div", { style: { display: "flex", alignItems: "center", marginTop: "4px" } }, choose, remove), row, note);
  return { el, shown: paint };
}
