// What the screens share: the start-up facts, the latest player state, and the way to move between pages.
import type { Init, PlayerState, QueueItem, SettingsState, TabsState } from "./api";

export interface Page {
  el: HTMLElement;
  /** Called each time the page comes into view. */
  shown?: () => void;
}

export const shell = {
  init: null as unknown as Init,
  settings: null as unknown as SettingsState,
  player: null as unknown as PlayerState,
  queue: [] as QueueItem[],
  tabs: { tabs: [], custom: [] } as TabsState,
  page: "",
  navigate: (_page: string, _push = true) => {},
  goBackOr: (_fallback: string) => {},
  setStatus: (_text: string) => {},
  applyTheme: (_theme: string) => {},
  /** The page object, made on first use. */
  get: <T extends Page>(_name: string): T => null as unknown as T,
  showNow: (_show: boolean) => {},
  /** Play / pause: Phoenix's own player, or the streaming page being listened to. */
  toggle: () => {},
  /** AcoustID is set up (a key and fpcalc): songs can be identified by their sound. */
  canIdentify: false,
  /** settings → display's choices the screens follow (quickplay clicks, artist photos, lyrics). */
  display: { quickplayPlays: false, backdrops: false, lyricsAuto: false, showLyrics: false, backdropUsage: "" },
  nowOpen: () => false,
  /** The video drawing surface (self-test and measurement). */
  video: null as unknown as import("./video").VideoView,
  /** The now playing layer (self-test). */
  nowPlaying: null as unknown as import("./nowplaying").NowPlaying,
};

/** Runs a reload at most once per burst of changes. */
export function debounce(fn: () => void, ms = 120): () => void {
  let timer: ReturnType<typeof setTimeout> | undefined;
  return () => { clearTimeout(timer); timer = setTimeout(fn, ms); };
}
