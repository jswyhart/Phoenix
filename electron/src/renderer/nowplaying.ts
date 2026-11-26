// The now-playing layer (always dark): a wall of covers or the visualizer behind the song's details and the queue,
// or the video itself. Double-click or F11 for full screen.
import { call, host, on, PlayerState, QueueItem, time } from "./api";
import { shell } from "./shell";
import { tr } from "./translate";
import { art, artUrl, h, iconBtn, MenuEntry, menuAt, message, setArt, textBtn, VList } from "./ui";
import { VideoView } from "./video";
import { decodeScope, dominantColor, Viz } from "./viz";

export class NowPlaying {
  readonly el: HTMLDivElement;
  open = false;
  full = false;
  readonly viz: Viz;
  /** Where the playing video is drawn. */
  readonly video = h("div.video.hidden");
  readonly videoView = new VideoView(this.video);
  private mosaic = h("div.mosaic");
  private shade = h("div.shade");
  private canvas = h("canvas.viz.hidden");
  private cover = art(null, 500);
  private title = h("div.title.ell");
  private artist = h("div.artist.ell");
  private album = h("div.album.ell");
  private info: HTMLDivElement;
  private queuePane: HTMLDivElement;
  private queue: VList<QueueItem>;
  private vizBtn: HTMLButtonElement;
  private idle: ReturnType<typeof setTimeout> | undefined;
  private lastArt: string | null = null;
  private vizOn = false;
  private mosaicBuilt = false;
  // the side pane shows the queue or the song's lyrics (as on Windows)
  private lyricsPane = h("div.lyrics");
  private lyricsLines = h("div.lyriclines");
  private lyricsNote = h("div.lyricnote");
  private lyrics: { synced: boolean; source: string; lines: { ms: number; text: string }[] } | null = null;
  private lyricsFor: number | null = null;
  private lineNow = -1;
  private paneTabs: { queue: HTMLElement; lyrics: HTMLElement };
  // artist photos behind the song, when switched on in settings → display
  private backdrop = h("div.backdrop.hidden", h("img"), h("img"));
  private photos: string[] = [];
  private photoArtist = "";
  private photoIndex = 0;
  private photoTimer: ReturnType<typeof setInterval> | undefined;

  constructor(private transport: HTMLElement) {
    this.viz = new Viz(this.canvas);
    on("speech.line", (e: { id: number; ms: number; text: string }) => this.heardLine(e));
    this.queue = new VList<QueueItem>({
      rowHeight: 44,
      render: (q, i) => h("div.queuerow",
        h("div.grow", h(`div.ell${i === shell.player.index ? ".current" : ""}` as "div", q.title), h("div.sub.ell", q.subtitle)),
        h("div.faint", { style: { margin: "0 4px 0 8px" } }, time(q.dur))),
      onActivate: (_q, i) => void call("player.playAt", { index: i }),
      onContext: (_items, e) => {
        const i = this.queue.selectedIndex();
        if (i >= 0) menuAt([{ label: "Play", action: () => call("player.playAt", { index: i }) }, { label: "Remove from now playing", action: () => call("player.removeAt", { index: i }) }], e);
      },
    });
    const tab = (label: string, lyrics: boolean) => h("div.h2.panetab", { onclick: () => void this.showLyrics(lyrics) }, label);
    this.paneTabs = { queue: tab("now playing", false), lyrics: tab("lyrics", true) };
    this.lyricsPane.append(this.lyricsLines, this.lyricsNote,
      h("div.lyricbtns",
        textBtn("find on LRCLIB", () => void this.lyricsAction("lyrics.find", "looking on LRCLIB…")),
        this.listenBtn,
        textBtn("from a file…", async () => {
          const file = await host.openFile("Lyrics for this song", [{ name: "Lyrics", extensions: ["lrc", "txt"] }]);
          if (file) await this.lyricsAction("lyrics.import", "", { file });
        }),
        textBtn("paste", async () => {
          let text = "";
          try { text = await navigator.clipboard.readText(); } catch { /* nothing to paste */ }
          if (text.trim() === "") { await message("Copy the lyrics first, then choose paste."); return; }
          await this.lyricsAction("lyrics.import", "", { text });
        }),
        textBtn("remove", () => void this.lyricsAction("lyrics.remove", ""))));
    this.queuePane = h("div.queue", h("div.panetabs", this.paneTabs.queue, this.paneTabs.lyrics), this.queue.el, this.lyricsPane);
    this.info = h("div.info", h("div.words", h("div.cover", this.cover), this.title, this.artist, this.album), this.queuePane);
    this.vizBtn = iconBtn("visualizer", "Visualizer style and colours (V and C cycle them)", () => menuAt(this.vizMenu(), this.vizBtn));
    this.el = h("div.now.hidden", this.mosaic, this.backdrop, this.shade, this.canvas, this.info, this.video,
      h("div.bar",
        iconBtn("back", "Back to collection (Esc)", () => this.show(false)),
        this.vizBtn,
        iconBtn("full", "Full screen (F11, or double-click)", () => this.toggleFullscreen())));
    this.el.addEventListener("dblclick", e => {
      if (!(e.target as HTMLElement).closest(".queue, .bar")) this.toggleFullscreen();
    });
    on("fullscreen", (isFull: boolean) => { if (this.full !== isFull) { this.full = isFull; this.layout(); } });
    on("viz", (f: { bars: number[]; peaks: number[]; scope: string }) => {
      if (this.vizOn) this.viz.feed({ bars: f.bars, peaks: f.peaks, scope: decodeScope(f.scope) });
    });
    document.addEventListener("mousemove", () => { if (this.full) this.wake(); });
    on("tick", (t: { pos: number }) => this.follow(t.pos));
    this.paintPaneTabs();
  }

  // ---------- lyrics ----------

  private showingLyrics() { return shell.display.showLyrics; }

  async showLyrics(on: boolean) {
    const d = await call<typeof shell.display>("display.set", { showLyrics: on });
    shell.display = d;
    this.paintPaneTabs();
    if (on) await this.loadLyrics(true);
  }

  private paintPaneTabs() {
    const lyrics = this.showingLyrics();
    this.paneTabs.queue.classList.toggle("on", !lyrics);
    this.paneTabs.lyrics.classList.toggle("on", lyrics);
    this.queue.el.classList.toggle("hidden", lyrics);
    this.lyricsPane.classList.toggle("hidden", !lyrics);
  }

  /** The lyrics of the song playing (looked up online by itself only if that is switched on in settings). */
  async loadLyrics(force = false) {
    const id = shell.player.current?.trackId ?? null;
    if (!force && id === this.lyricsFor) return;
    this.lyricsFor = id;
    if (id == null) { this.paintLyrics(null, shell.player.current ? "Lyrics are for songs in your collection." : "Nothing is playing."); return; }
    this.paintLyrics(null, "");
    const l = await call<typeof this.lyrics>("lyrics.get", { id, auto: true });
    if (this.lyricsFor !== id) return;
    this.paintLyrics(l, l ? "" : "No lyrics for this song yet. Find them on LRCLIB, let the speech model listen, or take them from a file or the clipboard.");
    if (!l && this.listening == null) {
      const sp = await call<{ listen: boolean; model: string; models: { id: string; have: boolean }[] }>("speech.state");
      if (this.lyricsFor === id && sp.listen && sp.models.some(m => m.id === sp.model && m.have)) void this.listen(true);
    }
  }

  /** The speech model writes the lyrics of the song playing; each line shows as it is written. */
  private listening: number | null = null;
  private heard: { ms: number; text: string }[] = [];
  readonly listenBtn = textBtn("listen", () => void this.listen(false));

  private async listen(quiet: boolean) {
    const id = shell.player.current?.trackId;
    if (id == null) return;
    if (this.listening === id) { await call("lyrics.listen", { id }); return; }   // asking again stops it
    this.listening = id;
    this.heard = [];
    this.listenBtn.textContent = "stop listening";
    this.lyricsNote.textContent = "the speech model is listening…";
    try {
      const l = await call<typeof this.lyrics>("lyrics.listen", { id });
      if (this.lyricsFor === id) this.paintLyrics(l, l ? "" : "No lyrics for this song.");
    } catch (e: any) {
      if (this.lyricsFor === id && (!quiet || !this.lyrics)) this.lyricsNote.textContent = String(e.message ?? e);
    } finally {
      if (this.listening === id) this.listening = null;
      this.listenBtn.textContent = "listen";
    }
  }

  private heardLine(e: { id: number; ms: number; text: string }) {
    if (e.id !== this.listening || e.id !== this.lyricsFor) return;
    this.heard.push({ ms: e.ms, text: e.text });
    this.paintLyrics({ synced: true, source: "the speech model is listening…", lines: [...this.heard] } as any, "the speech model is listening…");
  }

  private async lyricsAction(method: string, busy: string, extra: Record<string, string> = {}) {
    const id = shell.player.current?.trackId;
    if (id == null) return;
    if (busy) this.lyricsNote.textContent = busy;
    try {
      const l = await call<typeof this.lyrics>(method, { id, ...extra });
      this.paintLyrics(l, l ? "" : "No lyrics for this song.");
    } catch (e: any) { this.lyricsNote.textContent = String(e.message ?? e); }
  }

  private paintLyrics(l: typeof this.lyrics, note: string) {
    this.lyrics = l;
    this.lineNow = -1;
    this.lyricsLines.replaceChildren(...(l?.lines ?? []).map(x => h("div.lyricline", x.text || "\u00a0")));
    this.lyricsLines.classList.toggle("synced", !!l?.synced);
    this.lyricsNote.textContent = note || (l ? (l.synced ? "" : "") + l.source : "");
    this.follow(shell.player.pos);
  }

  /** Timed lyrics follow the song: the line being sung is lit and kept in view. */
  private follow(ms: number) {
    const l = this.lyrics;
    if (!l?.synced || !this.open) return;
    let found = -1;
    for (let i = 0; i < l.lines.length; i++) { if (l.lines[i].ms <= ms) found = i; else break; }
    if (found === this.lineNow) return;
    this.lyricsLines.children[this.lineNow]?.classList.remove("sung");
    this.lineNow = found;
    const line = this.lyricsLines.children[found] as HTMLElement | undefined;
    if (!line) return;
    line.classList.add("sung");
    this.lyricsLines.scrollTo({ top: line.offsetTop - this.lyricsLines.clientHeight / 2 + line.offsetHeight / 2, behavior: "smooth" });
  }

  // ---------- artist photos ----------

  private async loadPhotos() {
    const artist = shell.player.current?.artist ?? "";
    const on = shell.display.backdrops;
    if (!on || !artist || shell.player.current?.isVideo) { this.photos = []; this.photoArtist = ""; this.paintPhotos(); return; }
    if (artist === this.photoArtist) return;
    this.photoArtist = artist;
    const found = await call<string[]>("backdrops.for", { artist });
    if (this.photoArtist !== artist) return;
    this.photos = found;
    this.photoIndex = 0;
    this.paintPhotos();
  }

  private paintPhotos() {
    clearInterval(this.photoTimer);
    const has = this.photos.length > 0;
    this.backdrop.classList.toggle("hidden", !has);
    this.layout();
    if (!has) return;
    const [a, b] = [...this.backdrop.querySelectorAll("img")] as HTMLImageElement[];
    const showNext = () => {
      const front = this.photoIndex % 2 === 0 ? a : b, back = front === a ? b : a;
      setArt(front, this.photos[this.photoIndex % this.photos.length], 0);
      front.classList.add("on");
      back.classList.remove("on");
      this.photoIndex++;
    };
    showNext();
    if (this.photos.length > 1) this.photoTimer = setInterval(showNext, 12000);
  }

  /** For the self-test. */
  get lyricsShown() { return { lines: this.lyrics?.lines.length ?? 0, synced: this.lyrics?.synced ?? false, now: this.lineNow, note: this.lyricsNote.textContent ?? "" }; }
  get photosShown() { return this.photos.length; }

  show(show: boolean) {
    if (!show && this.full) this.toggleFullscreen();
    this.open = show;
    this.el.classList.toggle("hidden", !show);
    if (show) {
      void this.buildMosaic(); this.queueChanged(); this.queue.reveal(shell.player.index);
      void call("display.state").then(d => { shell.display = d; this.paintPaneTabs(); if (this.showingLyrics()) void this.loadLyrics(); void this.loadPhotos(); });
    }
    this.layout();
  }

  toggleFullscreen() {
    this.full = !this.full;
    void host.setFullscreen(this.full);
    this.layout();
  }

  /** In full screen the controls and pointer get out of the way until the mouse moves again. */
  private wake() {
    document.body.classList.remove("idle");
    this.transport.classList.remove("hidden");
    this.el.querySelector(".bar")?.classList.remove("hidden");
    clearTimeout(this.idle);
    if (!this.full) return;
    this.idle = setTimeout(() => {
      if (!this.full || this.transport.matches(":hover") || document.querySelector(".menu")) return;
      this.transport.classList.add("hidden");
      this.el.querySelector(".bar")?.classList.add("hidden");
      document.body.classList.add("idle");
    }, 3000);
  }

  /** Decides what fills the layer: the video, the visualizer, or the wall of covers. */
  private layout() {
    const S = shell.settings;
    const video = shell.player.current?.isVideo === true;
    const viz = this.open && !video && S.visualizer > 0;
    this.el.classList.toggle("full", this.full);
    this.video.classList.toggle("hidden", !(video && this.open));
    this.info.classList.toggle("hidden", video);
    this.shade.classList.toggle("hidden", video);
    this.queuePane.classList.toggle("hidden", this.full);
    const photos = this.photos.length > 0 && !video && !viz;
    this.mosaic.classList.toggle("hidden", video || viz || photos);
    this.backdrop.classList.toggle("hidden", !photos);
    this.vizBtn.classList.toggle("hidden", video);
    this.canvas.classList.toggle("hidden", !viz);
    this.videoView.setActive(video);
    this.viz.mode = S.visualizer;
    this.viz.palette = S.visualizerColor;
    if (viz !== this.vizOn) {   // only analyses while it is on screen
      this.vizOn = viz;
      void call("viz.enable", { on: viz });
      if (!viz) this.viz.clear();
    }
    this.wake();
  }

  private async buildMosaic() {
    if (this.mosaicBuilt && this.mosaic.childElementCount > 0) return;
    this.mosaicBuilt = true;
    const arts = await call<string[]>("now.mosaic");
    this.mosaic.replaceChildren(...(arts.length ? Array.from({ length: 40 }, (_, i) => art(arts[i % arts.length], 240)) : []));
  }

  private vizMenu(): MenuEntry[] {
    const S = shell.settings, init = shell.init;
    const items: MenuEntry[] = [];
    init.vizStyles.forEach((name, i) => {
      items.push({ label: name, checked: S.visualizer === i, action: () => this.setVisualizer(i, S.visualizerColor) });
      if (i === 0) items.push("-");
    });
    items.push("-",
      { label: "Colours", sub: init.vizPalettes.map((name, i) => ({ label: name, checked: S.visualizerColor === i, action: () => this.setVisualizer(S.visualizer, i) })) },
      { label: "Sensitivity", sub: init.vizSensitivities.map((name, i) => ({ label: name, checked: S.visualizerSensitivity === i, action: async () => { shell.settings = await call("settings.set", { visualizerSensitivity: i }); this.layout(); } })) });
    return items;
  }

  async setVisualizer(style: number, palette: number) {
    shell.settings = await call("settings.set", { visualizer: style, visualizerColor: palette });
    this.layout();
  }

  playerChanged(s: PlayerState, itemChanged: boolean) {
    const cur = s.current;
    tr(this.title, cur?.title ?? "");
    tr(this.artist, cur?.subtitle ?? "");
    tr(this.album, cur?.album ?? "");
    if ((cur?.art ?? null) !== this.lastArt) {
      this.lastArt = cur?.art ?? null;
      setArt(this.cover, this.lastArt, 500);
      const wanted = this.lastArt;
      void dominantColor(wanted ? artUrl(wanted, 24) : null).then(c => { if (this.lastArt === wanted) this.viz.coverColor = c; });
    }
    if (itemChanged) {
      if (this.open && this.showingLyrics()) void this.loadLyrics();
      if (this.open) void this.loadPhotos();
      if (cur?.isVideo && !this.open) this.show(true); else this.layout();
      if (this.open) { this.queue.redraw(); this.queue.reveal(s.index); }
    }
  }

  queueChanged() {
    if (this.open) this.queue.setItems(shell.queue, true);
  }

  /** V and C cycle the visualizer's style and colours while music is showing. */
  key(e: KeyboardEvent) {
    if (shell.player.current?.isVideo || e.ctrlKey || e.metaKey || e.altKey) return;
    const S = shell.settings, k = e.key.toLowerCase();
    if (k === "v") void this.setVisualizer((S.visualizer + 1) % shell.init.vizStyles.length, S.visualizerColor);
    else if (k === "c") void this.setVisualizer(S.visualizer, (S.visualizerColor + 1) % shell.init.vizPalettes.length);
    else return;
    e.preventDefault();
  }
}
