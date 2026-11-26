// collection → podcasts, in two tabs as on Windows: "my podcasts" (subscribed series on the left, the chosen one's
// episodes on the right) and "sources" (Apple Podcasts with its genres, fyyd, and OPML lists of your own, to browse and
// subscribe from). Video podcasts play in now playing like any video.
import { call, host, on, time } from "./api";
import { Page, shell } from "./shell";
import { art, ask, confirm, h, iconBtn, menuAt, message, showMenu, tabs, textBtn, ViewBar, VList } from "./ui";
import { attachServiceTabs } from "./web";

interface Podcast { id: number; title: string; author: string; description: string; art: string; unplayed: number }
interface Episode { id: number; title: string; date: string; dur: number; played: boolean; description: string; isVideo: boolean; downloaded: boolean; manual: boolean }
export interface Hit { title: string; author: string; art: string; feedUrl: string; detail: string; appleId: string; subtitle: string; have: boolean }
interface Source { id: string; name: string; note: string; hasGenres: boolean; yours: boolean }
interface Sources { sources: Source[]; genres: { name: string; id: number }[] }

export interface PodcastsPage extends Page {
  showTab(tab: string): void; tab(): string; podcasts(): Podcast[]; episodes(): Episode[]; caption(): string;
  sources(): Source[]; hits(): Hit[]; showHits(hits: Hit[], title: string): void; ready(): Promise<void>; select(id: number): Promise<void>;
}

export function podcastsPage(): PodcastsPage {
  let tab = "mine", pods: Podcast[] = [], podcast: Podcast | null = null, episodes: Episode[] = [];
  let pending: Promise<void> = Promise.resolve();
  const bar = new ViewBar("podcasts", call);
  bar.onChange = () => void reload(podcast?.id ?? null);
  const tabBar = tabs([["mine", "my podcasts"], ["sources", "sources"]], t => showTab(t));
  tabBar.el.append(bar.el);
  attachServiceTabs(tabBar.el, "podcasts");

  // ---- my podcasts ----
  const seriesCap = h("div.cap", "SERIES"), epCap = h("div.cap", "EPISODES");
  const title = h("div.h2", { style: { margin: "0 28px 4px 8px" } });
  const desc = h("div.note", { style: { margin: "0 28px 12px 8px", maxHeight: "54px", overflow: "hidden" } });
  const series = new VList<Podcast>({
    rowHeight: 64,
    render: p => h("div", h("div.thumb", { style: { width: "52px", height: "52px", marginRight: "12px" } }, art(p.art, 120)),
      h("div.grow", h("div.ell", { style: { fontSize: "14px" } }, p.title), h("div.ell.dim", { style: { fontSize: "12px" } }, p.author)),
      p.unplayed > 0 ? h("div", { style: { color: "var(--accent)", fontSize: "12px", margin: "0 10px 0 6px" }, title: `${p.unplayed} not played yet` }, String(p.unplayed)) : null),
    onSelect: sel => void showPodcast(sel[0] ?? null),
    onContext: (sel, e) => {
      const p = sel[0];
      if (!p) return;
      void showPodcast(p);
      void (async () => {
        const k = await call<{ keep: number; own: boolean; choices: { n: number; text: string }[] }>("podcast.keepOf", { id: p.id });
        const x = e.clientX, y = e.clientY;
        showMenu([
          { label: "Play newest episode", action: () => call("podcasts.play", { podcast: p.id }) },
          "-",
          { label: "Keep downloaded", sub: [
            { label: "As in settings", checked: !k.own, action: () => call("podcast.keep", { id: p.id }) },
            "-",
            ...k.choices.map(c => ({ label: c.text, checked: k.own && k.keep === c.n, action: () => call("podcast.keep", { id: p.id, keep: c.n }) })),
          ] },
          { label: "Download all unplayed episodes", action: async () => { const n = await call<number>("podcast.downloadAll", { id: p.id }); shell.setStatus(`${n} episode${n === 1 ? "" : "s"} downloaded`); await showPodcast(p, true); } },
          { label: "Remove its downloads", action: async () => { await call("podcast.removeDownloads", { id: p.id }); await showPodcast(p, true); } },
          "-",
          { label: "Unsubscribe", action: async () => { if (await confirm(`Unsubscribe from “${p.title}”?`, "unsubscribe")) { await call("podcasts.remove", { id: p.id }); await reload(null); } } },
        ], x, y);
      })();
    },
  });
  const list = new VList<Episode>({
    rowHeight: 34,
    render: ep => h("div", { title: ep.description.slice(0, 600), style: { opacity: ep.played ? "0.45" : "1" } },
      h("div.grow.ell", ep.title),
      ep.isVideo ? h("div.faint", { style: { width: "46px", flex: "none", fontSize: "11px" } }, "video") : null,
      ep.downloaded ? h("div.faint", { style: { width: "78px", flex: "none", fontSize: "11px" }, title: "Downloaded: plays without the internet" }, "downloaded") : null,
      h("div.dim", { style: { width: "110px", flex: "none" } }, ep.date),
      h("div.dim", { style: { width: "70px", flex: "none", textAlign: "right", paddingRight: "16px" } }, time(ep.dur))),
    onActivate: ep => play(ep),
    onContext: (sel, e) => {
      const ep = sel[0];
      if (!ep || !podcast) return;
      const pod = podcast;
      menuAt([
        { label: "Play", action: () => play(ep) },
        { label: ep.played ? "Mark as unplayed" : "Mark as played", action: async () => { await call("podcasts.togglePlayed", { podcast: pod.id, episode: ep.id }); await showPodcast(pod, true); } },
        "-",
        ep.downloaded
          ? { label: "Remove download", action: async () => { await call("episode.removeDownload", { podcast: pod.id, episode: ep.id }); await showPodcast(pod, true); } }
          : { label: "Download", action: async () => {
            try { await call("episode.download", { podcast: pod.id, episode: ep.id }); } catch (err: any) { await message("Couldn't download it.\n\n" + (err.message ?? err)); }
            await showPodcast(pod, true);
          } },
      ], e);
    },
  });
  const refreshBtn = textBtn("refresh", refresh);
  const empty = h("div.center-note.hidden", h("div.note", "No podcasts yet. Find some under “sources”, or choose “add” and paste a podcast's RSS feed address."));
  const left = h("div.pane", h("div.split", seriesCap, h("div", { style: { margin: "-4px 4px 6px 0", display: "flex", gap: "12px" } }, textBtn("add", add), refreshBtn)), series.el);
  const right = h("div.pane", { style: { marginLeft: "18px" } }, title, desc, epCap, list.el);
  const minePane = h("div.cols", { style: { gridTemplateColumns: "320px 1fr" } }, left, right, empty);
  // the add button must stay reachable while the "no podcasts yet" note covers the page
  left.style.zIndex = "1";
  left.style.position = "relative";

  // ---- sources ----
  let sources: Source[] = [], genres: { name: string; id: number }[] = [], source = "apple", genre = 0, hits: Hit[] = [], asked = 0;
  const sourceList = new VList<Source>({
    rowHeight: 50,
    render: s => h("div", { style: { flexDirection: "column", alignItems: "flex-start", justifyContent: "center" } },
      h("div.ell", { style: { fontSize: "14px", width: "100%" } }, s.name), h("div.ell.faint", { style: { fontSize: "11.5px", width: "100%" } }, s.yours ? "your list  ·  " + s.note : s.note)),
    onSelect: sel => { if (sel[0]) choose(sel[0].id); },
    onContext: (sel, e) => {
      const s = sel[0];
      if (!s?.yours) return;
      menuAt([{ label: "Remove this source", action: async () => {
        if (!(await confirm(`Remove “${s.name}” from your sources? Podcasts you subscribed to from it stay.`, "remove"))) return;
        paintSources(await call<Sources>("pdir.removeSource", { id: s.id }));
        if (source === s.id) choose("apple");
      } }], e);
    },
  });
  sourceList.el.style.maxHeight = "260px";
  sourceList.el.style.flex = "none";
  const genreCap = h("div.cap", { style: { marginTop: "14px" } }, "GENRES");
  const genreList = new VList<{ name: string; id: number }>({
    rowHeight: 28,
    render: g => h("div.name", h("div.grow.ell", g.name)),
    onSelect: sel => { if (sel[0]) { genre = sel[0].id; void browse(); } },
  });
  const foundCap = h("div.cap", { style: { margin: "0 16px 0 8px" } }, "PODCASTS");
  const searchBox = h("input", { type: "text", spellcheck: false, placeholder: "a podcast's name", title: "A podcast's name (what you type is sent to the source chosen on the left)", style: { maxWidth: "320px" } });
  searchBox.addEventListener("keydown", e => { if (e.key === "Enter") void search(searchBox.value); });
  const note = h("div.note", { style: { margin: "6px 28px 6px 8px", fontSize: "12px" } });
  const found = new VList<Hit>({
    rowHeight: 58,
    render: x => h("div", { title: x.feedUrl },
      h("div.thumb", { style: { width: "46px", height: "46px", marginRight: "12px" } }, art(x.art, 100)),
      h("div.grow", h("div.ell", { style: { fontSize: "14px" } }, x.title), h("div.ell.faint", { style: { fontSize: "11.5px" } }, x.subtitle)),
      subscribeButton(x)),
    onActivate: x => void subscribe(x),
    onContext: (sel, e) => { if (sel[0]) menuAt([{ label: sel[0].have ? "Already in my podcasts" : "Subscribe", disabled: sel[0].have, action: () => void subscribe(sel[0]) }], e); },
  });
  const sourcesLeft = h("div.pane",
    h("div.split", h("div.cap", "SOURCES"), h("div", { style: { margin: "-4px 4px 6px 0" } }, textBtn("add your own", addOwn))),
    sourceList.el, genreCap, genreList.el,
    h("div", { style: { margin: "8px 0 6px 8px", display: "flex", gap: "14px" } },
      textBtn("save my podcasts as a list…", exportOpml), textBtn("subscribe from a list…", importOpml)));
  const sourcesRight = h("div.pane", { style: { marginLeft: "18px" } },
    h("div", { style: { display: "flex", alignItems: "center", margin: "-4px 28px 4px 0" } }, foundCap, searchBox, textBtn("search", () => void search(searchBox.value))),
    note, found.el);
  const sourcesPane = h("div.cols", { style: { gridTemplateColumns: "320px 1fr" } }, sourcesLeft, sourcesRight);

  const el = h("div.page", tabBar.el, h("div.cols", minePane, sourcesPane));

  function subscribeButton(x: Hit) {
    const b = iconBtn(x.have ? "heart" : "plus", x.have ? "In my podcasts" : "Subscribe", () => void subscribe(x), "small");
    b.disabled = x.have;
    if (x.have) b.classList.add("heartfull");
    b.style.marginRight = "22px";
    b.dataset.noactivate = "1";
    b.dataset.noselect = "1";
    return b;
  }

  function showTab(t: string) {
    tab = t === "sources" ? "sources" : "mine";
    tabBar.select(tab);
    bar.el.classList.toggle("hidden", tab !== "mine");
    minePane.classList.toggle("hidden", tab !== "mine");
    sourcesPane.classList.toggle("hidden", tab !== "sources");
    if (tab === "sources") void (async () => {
      paintSources(await call<Sources>("pdir.sources"));
      if (hits.length === 0 && asked === 0 && !shell.init.smoke) void browse();
    })();
  }

  // ---- my podcasts ----

  async function reload(selectId: number | null) {
    pending = (async () => {
      pods = await call<Podcast[]>("podcasts.list", { view: bar.value });
      seriesCap.textContent = `SERIES  ${pods.length}`;
      series.setItems(pods, true);
      empty.classList.toggle("hidden", pods.length > 0);
      const sel = pods.find(p => p.id === selectId) ?? pods[0] ?? null;
      series.select(sel ? pods.indexOf(sel) : null);
      await showPodcast(sel);
    })();
    return pending;
  }

  async function showPodcast(p: Podcast | null, keep = false) {
    podcast = p;
    title.textContent = p?.title ?? "";
    desc.textContent = p?.description ?? "";
    const r = p ? await call<{ total: number; video: string; episodes: Episode[] }>("podcasts.episodes", { id: p.id, view: bar.value }) : { total: 0, video: "", episodes: [] };
    if (podcast !== p) return;
    episodes = r.episodes;
    epCap.textContent = !p ? "" : (episodes.length === r.total ? `EPISODES  ${r.total}` : `EPISODES  ${episodes.length} of ${r.total}`) + (r.video ? `   ·   ${r.video}` : "");
    list.setItems(episodes, keep);
  }

  function play(ep: Episode) {
    if (podcast) void call("podcasts.play", { podcast: podcast.id, episode: ep.id });
  }

  async function add() {
    const url = await ask("Add a podcast", "Podcast RSS feed address", "https://");
    if (!url || url.trim() === "" || url.trim() === "https://") return;
    try { await reload(await call<number>("podcasts.add", { url })); }
    catch (e: any) { await message("Couldn't add that podcast.\n\n" + (e.message ?? e)); }
  }

  async function refresh() {
    refreshBtn.disabled = true;
    try { await call("podcasts.refresh"); } finally { refreshBtn.disabled = false; }
    await reload(podcast?.id ?? null);
  }

  // ---- sources ----

  function paintSources(s: Sources) {
    sources = s.sources;
    genres = s.genres;
    if (!sources.some(x => x.id === source)) source = "apple";
    sourceList.el.style.height = Math.min(260, sources.length * 50 + 2) + "px";
    sourceList.setItems(sources, true);
    sourceList.select(sources.findIndex(x => x.id === source));
    const current = sources.find(x => x.id === source);
    genreCap.classList.toggle("hidden", !current?.hasGenres);
    genreList.el.classList.toggle("hidden", !current?.hasGenres);
    const g = [{ name: "top podcasts", id: 0 }, ...genres];
    genreList.setItems(current?.hasGenres ? g : [], true);
    genreList.select(g.findIndex(x => x.id === genre));
  }

  function choose(id: string) {
    source = id;
    genre = 0;
    paintSources({ sources, genres });
    void browse();
  }

  const sourceName = () => sources.find(s => s.id === source)?.name ?? "Apple Podcasts";

  async function browse() {
    const g = genres.find(x => x.id === genre);
    await load(g ? g.name.toUpperCase() : source === "apple" ? "TOP PODCASTS" : sourceName().toUpperCase(), () => call("pdir.top", { source, genre }));
  }

  async function search(text: string) {
    if (text.trim() === "") return;
    await load(`“${text.trim().toUpperCase()}”`, () => call("pdir.search", { source, text }));
  }

  async function load(what: string, fetch: () => Promise<Hit[]>) {
    const mine = ++asked;
    foundCap.textContent = what;
    note.textContent = `asking ${sourceName()}…`;
    try {
      const r = await fetch();
      if (mine !== asked) return;
      showHits(r, what);
    } catch (e: any) { if (mine === asked) note.textContent = `${sourceName()} can't be read: ${e.message ?? e}`; }
  }

  function showHits(r: Hit[], what: string) {
    hits = r;
    foundCap.textContent = what;
    found.setItems(hits);
    note.textContent = hits.length === 0 ? "No podcasts found." : `${hits.length} podcasts. Double-click one, or press its +, to subscribe.`;
  }

  async function subscribe(x: Hit) {
    if (x.have) { shell.setStatus("already in my podcasts"); return; }
    try {
      const r = await call<{ id: number; title: string; video: boolean }>("pdir.subscribe", x);
      shell.setStatus(`subscribed to ${r.title}` + (r.video ? " (a video podcast)" : ""));
      hits = hits.map(h0 => (h0 === x ? { ...h0, have: true } : h0));
      found.setItems(hits, true);
      await reload(r.id);
    } catch (e: any) { await message(`Couldn't subscribe to ${x.title}.\n\n${e.message ?? e}`); }
  }

  async function addOwn() {
    const url = await ask("Add a source of your own", "Address of an OPML list of podcasts", "https://");
    if (!url || url.trim() === "" || url.trim() === "https://") return;
    const name = (await ask("Add a source of your own", "A name for it", "")) ?? "";
    try {
      const r = await call<{ id: string; all: Sources }>("pdir.addSource", { url, name });
      paintSources(r.all);
      choose(r.id);
    } catch (e: any) { await message(String(e.message ?? e)); }
  }

  async function exportOpml() {
    const file = await host.saveFile("Save my podcasts as a list", "Phoenix podcasts.opml", [{ name: "OPML list", extensions: ["opml", "xml"] }]);
    if (!file) return;
    const n = await call<number>("podcasts.exportOpml", { file });
    shell.setStatus(`saved ${n} podcast${n === 1 ? "" : "s"} to ${file}`);
  }

  async function importOpml() {
    const file = await host.openFile("Subscribe from a list of podcasts", [{ name: "OPML list", extensions: ["opml", "xml"] }]);
    if (!file) return;
    try {
      const r = await call<{ added: number; failed: number; already: number }>("podcasts.importOpml", { file });
      await reload(null);
      await message([`Subscribed to ${r.added} podcast${r.added === 1 ? "" : "s"}.`, r.already ? `${r.already} were already in my podcasts.` : "", r.failed ? `${r.failed} could not be read.` : ""].filter(x => x).join("\n"));
    } catch (e: any) { await message(String(e.message ?? e)); }
  }

  // new episodes found by the hourly look, or downloads that came or went
  on("podcasts", () => { if (el.isConnected) void reload(podcast?.id ?? null); });
  // an episode that finishes is marked as played: show that when the player moves on
  let lastIndex = -2;
  on("player", s => { if (s.index !== lastIndex) { lastIndex = s.index; if (el.isConnected && podcast) void showPodcast(podcast, true); } });
  showTab("mine");
  void reload(null);
  return {
    el, shown: () => { if (podcast) void showPodcast(podcast, true); },
    showTab, tab: () => tab, podcasts: () => pods, episodes: () => episodes, caption: () => epCap.textContent ?? "",
    sources: () => sources, hits: () => hits, showHits, ready: () => pending,
    select: async id => { const p = pods.find(x => x.id === id); if (p) { series.select(pods.indexOf(p)); await showPodcast(p); } },
  };
}
