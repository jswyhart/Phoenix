// settings → podcasts, as on Windows: looking for new episodes, how many to keep downloaded, removing played ones,
// and where downloads go.
import { call, host } from "./api";
import { confirm, h, pill } from "./ui";

interface PodcastSettings { auto: boolean; keep: number; removePlayed: boolean; folder: string; ownFolder: boolean; choices: { n: number; text: string }[]; usage: string }

export function podcastsPanel() {
  let P: PodcastSettings | null = null;
  const set = async (p: Record<string, unknown>) => paint(await call<PodcastSettings>("podcastSettings.set", p));
  const auto = h("input", { type: "checkbox" });
  auto.onchange = () => void set({ auto: auto.checked });
  const removePlayed = h("input", { type: "checkbox" });
  removePlayed.onchange = () => void set({ removePlayed: removePlayed.checked });
  const keep = h("select.box", { style: { width: "380px" } });
  keep.onchange = () => void set({ keep: Number(keep.value) });
  const folder = h("input", { type: "text", readOnly: true });
  const usage = h("div.dim", { style: { marginTop: "10px" } });
  const browse = pill("BROWSE", async () => { const picked = await host.pickFolders("Choose a folder for podcast downloads"); if (picked[0]) await set({ folder: picked[0] }); });
  const standard = pill("DEFAULT", () => void set({ folder: "" }));
  standard.title = "Back to the folder inside Phoenix's own data";
  browse.style.margin = "0 0 0 10px";
  standard.style.margin = "0 0 0 6px";

  function paint(s: PodcastSettings) {
    P = s;
    auto.checked = s.auto;
    removePlayed.checked = s.removePlayed;
    if (keep.options.length === 0) keep.replaceChildren(...s.choices.map(c => h("option", { value: String(c.n) }, c.text)));
    keep.value = String(s.keep);
    folder.value = s.folder;
    usage.textContent = s.usage;
  }

  const el = h("div", h("div.h2", "podcasts"),
    h("div.note", "Subscribe to a podcast from collection → podcasts. New episodes are counted in the series list until you play them."),
    h("label.check", { style: { margin: "8px 0 4px 0" } }, auto, "Look for new episodes when Phoenix starts and every hour"),
    h("div.label", "KEEP DOWNLOADED"), keep,
    h("div.note", { style: { marginTop: "8px" } }, "This applies to every series; right-click a series on the podcasts page to give it its own choice, to download all its unplayed episodes, or to remove its downloads. An episode you download yourself is kept until you play it or remove it."),
    h("label.check", { style: { margin: "10px 0 4px 0" } }, removePlayed, "Remove a download once its episode has been played"),
    h("div.label", "DOWNLOAD FOLDER"),
    h("div", { style: { display: "flex", alignItems: "center", marginTop: "4px" } }, folder, browse, standard),
    h("div.note", { style: { marginTop: "8px" } }, "Each series gets its own folder there, with episodes named by date and title, so other players and devices can use them too. Changing the folder affects new downloads; episodes already downloaded stay where they are."),
    usage,
    h("div", { style: { marginTop: "8px" } },
      pill("OPEN FOLDER", () => { if (P) void host.openPath(P.folder); }),
      pill("REMOVE ALL DOWNLOADS", async () => {
        if (!P || P.usage.startsWith("No episodes")) return;
        if (!(await confirm("Remove every downloaded episode? Your subscriptions and what you have played are kept, and episodes can be downloaded again.", "remove"))) return;
        paint(await call<PodcastSettings>("podcastSettings.removeAll"));
      })));
  return { el, shown: async () => paint(await call<PodcastSettings>("podcastSettings.state")), state: () => P };
}
