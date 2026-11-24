// What the page may ask of the rest of the app (see preload.ts), plus a few shared shapes and formatters.

export interface Host {
  platform: "mac" | "linux" | "win";
  call(method: string, params?: unknown): Promise<unknown>;
  on(listener: (name: string, data: unknown) => void): void;
  pickFolders(title: string): Promise<string[]>;
  reveal(path: string): Promise<void>;
  openPath(path: string): Promise<string>;
  openExternal(url: string): Promise<void>;
  setFullscreen(on: boolean): Promise<void>;
  videoSocket(): Promise<string>;
  smoke: string | null;
  smokeShot(name: string): Promise<void>;
  smokeDone(log: string, fails: number): Promise<void>;
}

declare global { interface Window { phoenix: Host } }

export const host: Host = window.phoenix;
export const isMac = host.platform === "mac";

export function call<T = any>(method: string, params?: unknown): Promise<T> {
  return host.call(method, params) as Promise<T>;
}

const listeners = new Map<string, Set<(data: any) => void>>();
host.on((name, data) => listeners.get(name)?.forEach(fn => { try { fn(data); } catch (e) { console.error(e); } }));

/** Listens for an event from the helper: player, queue, lib, ratings, status, tick, art, fullscreen. */
export function on(name: string, fn: (data: any) => void) {
  let set = listeners.get(name);
  if (!set) listeners.set(name, (set = new Set()));
  set.add(fn);
}

export interface Song { id: number; title: string; artist: string; album: string; no: number; dur: number; rating: number; tags: string; local: boolean }
export interface TileData { key: string; title: string; caption: string; art: string }
export interface PlayerState {
  current: null | { title: string; subtitle: string; album: string; art: string; isVideo: boolean; trackId: number | null; rating: number; isBook: boolean; bookId: number | null };
  index: number; playing: boolean; shuffle: boolean; repeat: number; volume: number; muted: boolean; pos: number; dur: number;
}
export interface QueueItem { title: string; subtitle: string; dur: number }
export interface SettingsState {
  theme: "light" | "dark"; musicFolders: string[]; videoFolders: string[]; pictureFolders: string[]; bookFolders: string[];
  writeTags: boolean; visualizer: number; visualizerColor: number; visualizerSensitivity: number; readerFontSize: number; comicRightToLeft: boolean;
  jellyfinUrl: string; jellyfinUser: string; jellyfinConnected: boolean; plexUrl: string; plexConnected: boolean; hasPlexToken: boolean; hasDiscogsToken: boolean;
}
export interface Init {
  platform: string; smoke: boolean; bench: boolean; settings: SettingsState; counts: any; player: PlayerState; queue: QueueItem[];
  vizStyles: string[]; vizPalettes: string[]; vizSensitivities: string[]; sortKeys: { key: string; label: string }[]; autoPlaylists: string[];
}

/** "3:07" or "1:02:45"; empty for nothing. */
export function time(ms: number): string {
  if (!(ms > 0)) return "";
  const s = Math.floor(ms / 1000), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  const two = (n: number) => String(n).padStart(2, "0");
  return h >= 1 ? `${h}:${two(m)}:${two(sec)}` : `${m}:${two(sec)}`;
}
export const clock = (ms: number) => (ms > 0 ? time(ms) : "0:00");
export const join = (...parts: string[]) => parts.filter(p => p !== "").join("  ·  ");
export const plural = (n: number, noun: string) => (n === 0 ? "" : `${n} ${noun}${n === 1 ? "" : "s"}`);
