// The mini player, as on Windows: a small window that stays on top, with the cover, the song, play / previous / next,
// the heart and a thin progress line. "Back to full view" closes it and brings the main window back.
import { call, host, on, PlayerState } from "./api";
import { loadTheme } from "./themes";
import { artUrl, h, icon, iconBtn, setIcon } from "./ui";

export async function startMini() {
  await loadTheme();
  document.body.classList.add("minibody");
  const img = h("img", { draggable: false, alt: "" });
  const title = h("div.ell", { style: { fontSize: "14px" } }, "nothing playing");
  const artist = h("div.ell.dim", { style: { fontSize: "12px" } });
  const progress = h("div.miniprogress");
  let state: PlayerState | null = null;
  const play = iconBtn("play", "Play / pause", () => void call("player.toggle"), "filled");
  const heart = iconBtn("heart", "Like / dislike this song", () => { const id = state?.current?.trackId; if (id != null) void call("rate.cycle", { id }); });
  const root = h("div.mini",
    h("div.miniart", icon("note", 22), img),
    h("div.minimain",
      h("div.minititles", h("div", { style: { minWidth: "0", flex: "1 1 0" } }, title, artist),
        iconBtn("full", "Back to full view", () => void host.mini(false), "small nodrag")),
      h("div.minibtns",
        iconBtn("prev", "Previous", () => void call("player.prev"), "filled"), play,
        iconBtn("next", "Next", () => void call("player.next"), "filled"), heart)),
    progress);
  document.getElementById("root")!.replaceWith(root);

  function paint(s: PlayerState) {
    state = s;
    const cur = s.current;
    title.textContent = cur?.title ?? "nothing playing";
    artist.textContent = cur?.subtitle ?? "";
    if (cur?.art) { img.src = artUrl(cur.art, 200); img.classList.remove("art-missing"); } else { img.removeAttribute("src"); img.classList.add("art-missing"); }
    setIcon(play, s.playing ? "pause" : "play");
    heart.disabled = cur?.trackId == null;
    heart.classList.toggle("heartfull", (cur?.rating ?? 0) > 0);
    progress.style.width = s.dur > 0 ? `${Math.min(100, (100 * s.pos) / s.dur)}%` : "0";
  }
  on("player", paint);
  on("tick", (t: { pos: number; dur: number }) => { if (state) { state.pos = t.pos; state.dur = t.dur; progress.style.width = t.dur > 0 ? `${Math.min(100, (100 * t.pos) / t.dur)}%` : "0"; } });
  on("ratings", async () => paint(await call<PlayerState>("player.state")));
  paint(await call<PlayerState>("player.state"));
}
