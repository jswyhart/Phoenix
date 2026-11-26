// settings → playback, as on Windows: crossfade, volume levelling and the equalizer.
import { call } from "./api";
import { h, pill } from "./ui";

interface Playback {
  crossfade: number; eqEnabled: boolean; eqPreset: string; eqGains: number[]; presets: string[]; bands: number[];
  levelling: boolean; levelByAlbum: boolean; levelMeasure: boolean;
}

export function playbackPanel() {
  let P: Playback | null = null;
  const set = async (p: Record<string, unknown>) => paint(await call<Playback>("playback.set", p));
  const check = (text: string, key: string, indent = false) => {
    const box = h("input", { type: "checkbox" });
    box.onchange = () => void set({ [key]: box.checked });
    return { box, el: h("label.check", { style: { margin: indent ? "8px 0 0 24px" : "4px 0 0 0" } }, box, text) };
  };
  const crossfade = h("input", { type: "range", min: "0", max: "12", step: "1", style: { width: "260px" } });
  const crossfadeText = h("span.dim", { style: { marginLeft: "14px" } });
  crossfade.oninput = () => { crossfadeText.textContent = fadeWords(Number(crossfade.value)); };
  crossfade.onchange = () => void set({ crossfade: Number(crossfade.value) });
  const level = check("Play every song at about the same loudness", "levelling");
  const byAlbum = check("Level whole albums (an album's quiet and loud songs stay as they are to each other)", "levelByAlbum", true);
  const measure = check("Even out songs that carry no ReplayGain tags as they play", "levelMeasure", true);
  const eqOn = check("Use the equalizer", "eqEnabled");
  const preset = h("select.box", { style: { width: "200px" } });
  preset.onchange = () => void set({ eqPreset: preset.value });
  const reset = pill("RESET", () => void set({ eqPreset: "flat" }));
  reset.style.marginLeft = "10px";
  const bands = h("div.eqbands");
  const sliders: HTMLInputElement[] = [], values: HTMLElement[] = [];

  function fadeWords(n: number) {
    return n < 0.5 ? "off: songs play back to back" : `${n} second${n >= 1.5 ? "s" : ""}: the end of one song blends into the start of the next`;
  }

  function build(p: Playback) {
    if (sliders.length) return;
    preset.replaceChildren(...p.presets.map(n => h("option", { value: n }, n)));
    p.bands.forEach((f, i) => {
      // a slider turned on its end: up is louder
      const s = h("input", { type: "range", min: "-12", max: "12", step: "0.5", className: "eqband" });
      const v = h("div.dim", { style: { fontSize: "11px", marginBottom: "6px" } });
      s.oninput = () => { v.textContent = (Number(s.value) > 0 ? "+" : "") + Number(s.value); };
      s.onchange = () => void set({ band: i, gain: Number(s.value) });
      sliders.push(s); values.push(v);
      bands.append(h("div.eqcol", v, s, h("div.faint", { style: { fontSize: "11px", marginTop: "6px" } }, f >= 1000 ? `${f / 1000}k` : String(f))));
    });
  }

  function paint(p: Playback) {
    P = p;
    build(p);
    crossfade.value = String(p.crossfade);
    crossfadeText.textContent = fadeWords(p.crossfade);
    level.box.checked = p.levelling;
    byAlbum.box.checked = p.levelByAlbum;
    measure.box.checked = p.levelMeasure;
    byAlbum.box.disabled = measure.box.disabled = !p.levelling;
    eqOn.box.checked = p.eqEnabled;
    preset.value = p.presets.includes(p.eqPreset) ? p.eqPreset : "custom";
    sliders.forEach((s, i) => { s.value = String(p.eqGains[i] ?? 0); s.oninput!(new Event("input")); s.disabled = !p.eqEnabled; });
    preset.disabled = reset.disabled = !p.eqEnabled;
    bands.style.opacity = p.eqEnabled ? "1" : "0.4";
  }

  const el = h("div", h("div.h2", "playback"),
    h("div.note", "Music plays through VLC's engine, so one song runs into the next with no gap, songs can blend into each other, and the equalizer can shape the sound. Videos, podcasts and audiobooks are not crossfaded."),
    h("div.label", "CROSSFADE"), h("div", { style: { display: "flex", alignItems: "center", marginTop: "4px" } }, crossfade, crossfadeText),
    h("div.label", { style: { margin: "22px 0 3px 0" } }, "VOLUME LEVELLING"), level.el, byAlbum.el, measure.el,
    h("div.note", { style: { margin: "8px 0 0 24px" } }, "Uses the ReplayGain tags in your files where they have them. Songs without them are evened out as they play when the second choice is on, or otherwise given an average level; your files are not changed."),
    h("div.label", { style: { margin: "22px 0 3px 0" } }, "EQUALIZER"), eqOn.el,
    h("div", { style: { display: "flex", alignItems: "center", marginTop: "8px" } }, preset, reset), bands);
  return { el, shown: async () => paint(await call<Playback>("playback.state")), state: () => P };
}
