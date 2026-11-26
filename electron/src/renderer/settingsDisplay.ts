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
