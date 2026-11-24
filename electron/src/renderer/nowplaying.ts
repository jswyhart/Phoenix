// The now-playing layer (always dark): a wall of covers or the visualizer behind the song's details and the queue,
// or the video itself. Double-click or F11 for full screen.
import { call, host, on, PlayerState, QueueItem, time } from "./api";
import { shell } from "./shell";
import { art, artUrl, h, iconBtn, MenuEntry, menuAt, setArt, VList } from "./ui";
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

  constructor(private transport: HTMLElement) {
    this.viz = new Viz(this.canvas);
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
    this.queuePane = h("div.queue", h("div.h2", "now playing"), this.queue.el);
    this.info = h("div.info", h("div.words", h("div.cover", this.cover), this.title, this.artist, this.album), this.queuePane);
    this.vizBtn = iconBtn("visualizer", "Visualizer style and colours (V and C cycle them)", () => menuAt(this.vizMenu(), this.vizBtn));
    this.el = h("div.now.hidden", this.mosaic, this.shade, this.canvas, this.info, this.video,
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
  }

  show(show: boolean) {
    if (!show && this.full) this.toggleFullscreen();
    this.open = show;
    this.el.classList.toggle("hidden", !show);
    if (show) { void this.buildMosaic(); this.queueChanged(); this.queue.reveal(shell.player.index); }
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
    this.mosaic.classList.toggle("hidden", video || viz);
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
    this.title.textContent = cur?.title ?? "";
    this.artist.textContent = cur?.subtitle ?? "";
    this.album.textContent = cur?.album ?? "";
    if ((cur?.art ?? null) !== this.lastArt) {
      this.lastArt = cur?.art ?? null;
      setArt(this.cover, this.lastArt, 500);
      const wanted = this.lastArt;
      void dominantColor(wanted ? artUrl(wanted, 24) : null).then(c => { if (this.lastArt === wanted) this.viz.coverColor = c; });
    }
    if (itemChanged) {
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
