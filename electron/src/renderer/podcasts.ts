// collection → podcasts: subscribed series on the left, the chosen one's episodes on the right.
import { call, on, time } from "./api";
import { Page } from "./shell";
import { art, ask, h, menuAt, message, textBtn, VList } from "./ui";

interface Podcast { id: number; title: string; author: string; description: string; art: string }
interface Episode { id: number; title: string; date: string; dur: number; played: boolean; description: string }

export function podcastsPage(): Page {
  let pods: Podcast[] = [], podcast: Podcast | null = null, episodes: Episode[] = [];
  const seriesCap = h("div.cap", "SERIES"), epCap = h("div.cap", "EPISODES");
  const title = h("div.h2", { style: { margin: "0 28px 4px 8px" } });
  const desc = h("div.note", { style: { margin: "0 28px 12px 8px", maxHeight: "54px", overflow: "hidden" } });
  const series = new VList<Podcast>({
    rowHeight: 64,
    render: p => h("div", h("div.thumb", { style: { width: "52px", height: "52px", marginRight: "12px" } }, art(p.art, 120)),
      h("div.grow", h("div.ell", { style: { fontSize: "14px" } }, p.title), h("div.ell.dim", { style: { fontSize: "12px" } }, p.author))),
    onSelect: sel => void showPodcast(sel[0] ?? null),
    onContext: (sel, e) => {
      const p = sel[0];
      if (!p) return;
      void showPodcast(p);
      menuAt([
        { label: "Play newest episode", action: () => call("podcasts.play", { podcast: p.id }) },
        { label: "Unsubscribe", action: async () => { await call("podcasts.remove", { id: p.id }); await reload(null); } },
      ], e);
    },
  });
  const list = new VList<Episode>({
    rowHeight: 34,
    render: ep => h("div", { title: ep.description.slice(0, 600), style: { opacity: ep.played ? "0.45" : "1" } },
      h("div.grow.ell", ep.title), h("div.dim", { style: { width: "110px", flex: "none" } }, ep.date),
      h("div.dim", { style: { width: "70px", flex: "none", textAlign: "right", paddingRight: "16px" } }, time(ep.dur))),
    onActivate: ep => play(ep),
    onContext: (sel, e) => {
      const ep = sel[0];
      if (!ep || !podcast) return;
      const pod = podcast;
      menuAt([
        { label: "Play", action: () => play(ep) },
        { label: ep.played ? "Mark as unplayed" : "Mark as played", action: async () => { await call("podcasts.togglePlayed", { podcast: pod.id, episode: ep.id }); await showPodcast(pod, true); } },
      ], e);
    },
  });
  const refreshBtn = textBtn("refresh", refresh);
  const empty = h("div.center-note.hidden", h("div.note", "No podcasts yet. Choose “add” and paste a podcast's RSS feed address."));
  const left = h("div.pane", h("div.split", seriesCap, h("div", { style: { margin: "-4px 4px 6px 0", display: "flex", gap: "12px" } }, textBtn("add", add), refreshBtn)), series.el);
  const right = h("div.pane", { style: { marginLeft: "18px" } }, title, desc, epCap, list.el);
  const el = h("div.page", h("div.cols", { style: { gridTemplateColumns: "320px 1fr" } }, left, right, empty));
  // the add button must stay reachable while the "no podcasts yet" note covers the page
  left.style.zIndex = "1";
  left.style.position = "relative";

  async function reload(selectId: number | null) {
    pods = await call<Podcast[]>("podcasts.list");
    seriesCap.textContent = `SERIES  ${pods.length}`;
    series.setItems(pods, true);
    empty.classList.toggle("hidden", pods.length > 0);
    const sel = pods.find(p => p.id === selectId) ?? pods[0] ?? null;
    series.select(sel ? pods.indexOf(sel) : null);
    await showPodcast(sel);
  }

  async function showPodcast(p: Podcast | null, keep = false) {
    podcast = p;
    title.textContent = p?.title ?? "";
    desc.textContent = p?.description ?? "";
    episodes = p ? await call<Episode[]>("podcasts.episodes", { id: p.id }) : [];
    if (podcast !== p) return;
    epCap.textContent = p ? `EPISODES  ${episodes.length}` : "";
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

  // an episode that finishes is marked as played: show that when the player moves on
  let lastIndex = -2;
  on("player", s => { if (s.index !== lastIndex) { lastIndex = s.index; if (el.isConnected && podcast) void showPodcast(podcast, true); } });
  void reload(null);
  return { el, shown: () => { if (podcast) void showPodcast(podcast, true); } };
}
