// Translated names, as on Windows: when switched on (settings → translation and lyrics), a song, artist or album name
// written in another language is shown with its translation beside it in a quieter colour ("夜に駆ける  Racing into
// the Night"). Each name is translated once and remembered; the helper decides which names need it.
import { call, on } from "./api";
import { h, setTileText } from "./ui";

export const translation = { on: false };
const known = new Map<string, string>();   // name -> translation ("" = nothing to show)
const waiting = new Map<string, Set<HTMLElement>>();
let timer: ReturnType<typeof setTimeout> | undefined;

/** Puts a name in an element, with its translation beside it once it is known. */
export function tr(el: HTMLElement, text: string): HTMLElement {
  el.textContent = text;
  el.dataset.tr = text;
  if (!translation.on || !text || !/[^\u0000-ɏ]/.test(text) && !latin) return el;
  const t = known.get(text);
  if (t !== undefined) { show(el, text, t); return el; }
  let set = waiting.get(text);
  if (!set) waiting.set(text, (set = new Set()));
  set.add(el);
  clearTimeout(timer);
  timer = setTimeout(ask, 60);
  return el;
}

let latin = false;

function show(el: HTMLElement, text: string, t: string) {
  if (el.dataset.tr !== text || !t) return;
  el.replaceChildren(text, h("span.trside", "   " + t));
  if (!el.title) el.title = `${text}\n${t}`;
}

async function ask() {
  const texts = [...waiting.keys()].slice(0, 200);
  if (texts.length === 0) return;
  let found: Record<string, string> = {};
  try { found = await call<Record<string, string>>("translate.names", { texts }); } catch { /* nothing to show */ }
  for (const text of texts) {
    if (text in found) {
      known.set(text, found[text]);
      for (const el of waiting.get(text) ?? []) show(el, text, found[text]);
    }
    waiting.delete(text);   // still being asked: asked again the next time it is drawn
  }
  if (waiting.size) timer = setTimeout(ask, 60);
}

/** The settings changed: forget what is known and look again. */
export async function loadTranslation() {
  const s = await call<{ on: boolean; latin: boolean }>("translate.state");
  translation.on = s.on;
  latin = s.latin;
  known.clear();
}

on("translate", () => { void loadTranslation(); });

setTileText(tr);
