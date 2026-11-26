// settings → servers → streaming: which services are on, where each is shown, and tabs of your own. As on Windows.
import { call, host } from "./api";
import { confirm, h, pill } from "./ui";
import { loadWeb, onWeb, Service, webState } from "./web";

const note = (text: string) => h("div.note", text);
const label = (text: string, first = false) => h("div.label", { style: { margin: first ? "8px 0 3px 0" : "22px 0 3px 0" } }, text);

export function streamingPanel() {
  const musicRows = h("div"), videoRows = h("div"), customRows = h("div");
  const nameBox = h("input", { type: "text", placeholder: "name", spellcheck: false, style: { width: "150px", flex: "none" } });
  const urlBox = h("input", { type: "text", placeholder: "web address, or an HTML file on this computer", spellcheck: false, style: { marginLeft: "8px" } });
  const kindBox = h("select.box", { style: { width: "90px", flex: "none", marginLeft: "8px" } }, h("option", { value: "music" }, "music"), h("option", { value: "video" }, "video"));
  const webStatus = h("div.statusline");
  const addBtn = pill("ADD", async () => {
    try {
      await call("web.addCustom", { name: nameBox.value, url: urlBox.value, video: kindBox.value === "video" });
      nameBox.value = urlBox.value = "";
      webStatus.textContent = "";
    } catch (e: any) { webStatus.textContent = String(e.message ?? e); }
  });
  addBtn.style.margin = "0 0 0 8px";

  const placeBox = (s: Service) => {
    const box = h("select.box.small", { title: "Where it is shown", style: { marginLeft: "8px" } },
      ...webState.value.places.map(p => h("option", { value: p.place }, p.name)));
    box.value = s.place;
    box.disabled = !s.on;
    box.onchange = () => void call("web.place", { id: s.id, place: box.value });
    return box;
  };
  const row = (s: Service) => {
    const box = h("input", { type: "checkbox", checked: s.on, disabled: s.custom });
    box.onchange = () => void call("web.set", { id: s.id, on: box.checked });
    const r = h("div.webrow", { style: { display: "flex", alignItems: "center", height: "32px" }, data: { service: s.id } },
      h("label.check", { style: { minWidth: "160px" } }, box, s.name),
      h("span.faint", { style: { fontSize: "12px" } }, "show in"), placeBox(s));
    if (s.custom) {
      r.append(h("span.faint.ell", { style: { fontSize: "11.5px", margin: "0 0 0 12px", maxWidth: "220px" }, title: s.url }, s.url));
      r.append(h("button.textbtn", { style: { marginLeft: "12px" }, onclick: async () => { if (await confirm(`Remove “${s.name}”?`, "remove")) await call("web.removeCustom", { id: s.id }); } }, "remove"));
    }
    return r;
  };
  const paint = () => {
    const all = webState.value.services;
    musicRows.replaceChildren(...all.filter(s => !s.video && !s.custom).map(row));
    videoRows.replaceChildren(...all.filter(s => s.video && !s.custom).map(row));
    customRows.replaceChildren(...all.filter(s => s.custom).map(row));
  };
  onWeb(paint);
  const example = host.platform === "mac" ? "/Users/me/player/index.html" : host.platform === "win" ? "C:\\apps\\player\\index.html" : "/home/me/player/index.html";

  const el = h("div", h("div.h2", "streaming services"),
    note("Phoenix can show a streaming service's own web player in its window. Choose where each one goes with “show in”: a collection tab of its own, under any part of your collection (music services start on a tab of their own, video services under videos), or in a tab you made yourself (below). You sign in on the service's own page; Phoenix does not see your password. Their music and videos stay in their players: they are not added to your library, playlists or search, and the visualizer does not apply. While a service is playing, the player bar's controls work it, showing only the controls that service supports."),
    label("MUSIC", true), musicRows,
    label("VIDEO"), videoRows,
    label("YOUR OWN"), note(`Any web address, or an HTML file on this computer (for example ${example}).`), customRows,
    h("div", { style: { display: "flex", alignItems: "center", marginTop: "8px" } }, nameBox, urlBox, kindBox, addBtn),
    webStatus,
    h("div.note", { style: { marginTop: "14px" } }, "Pages are shown with the browser engine built into Phoenix (Chromium). It can't play streams protected with Widevine, so services that need it (Spotify, Netflix, Prime Video and others) may refuse to play here; YouTube, YouTube Music and services without that protection work. Some services limit picture quality in browsers, or may refuse an embedded browser."));
  return { el, shown: () => void loadWeb().then(paint) };
}

export function customTabsSection() {
  const rows = h("div");
  const nameBox = h("input", { type: "text", placeholder: "a name for the new tab", spellcheck: false, style: { width: "220px" } });
  const paint = () => {
    rows.replaceChildren(...webState.value.custom.map(t => {
      const name = h("input", { type: "text", value: t.name, spellcheck: false, style: { width: "220px" }, title: "Rename the tab" });
      name.onchange = () => void call("tabs.renameCustom", { id: t.id, name: name.value });
      const inside = webState.value.services.filter(s => s.on && s.place === t.page).map(s => s.name).join(", ");
      return h("div.customtab", { style: { display: "flex", alignItems: "center", marginTop: "6px" }, data: { tab: t.id } }, name,
        h("span.faint", { style: { fontSize: "12px", margin: "0 0 0 12px" } }, inside ? "holds " + inside : "empty"),
        h("button.textbtn", { style: { marginLeft: "12px" }, onclick: async () => {
          if (await confirm(`Remove the tab “${t.name}”? The services in it go back to where they were before.`, "remove")) await call("tabs.removeCustom", { id: t.id });
        } }, "remove"));
    }));
  };
  onWeb(paint);
  const add = async () => {
    if (nameBox.value.trim() === "") return;
    await call("tabs.addCustom", { name: nameBox.value });
    nameBox.value = "";
  };
  nameBox.addEventListener("keydown", e => { if (e.key === "Enter") void add(); });
  const addBtn = pill("ADD TAB", () => void add());
  addBtn.style.marginLeft = "8px";
  const el = h("div", label("TABS OF YOUR OWN"),
    note("A tab of your own sits with the others in your collection and holds the streaming services you put in it (choose it under “show in” above)."),
    rows, h("div", { style: { display: "flex", alignItems: "center", marginTop: "6px" } }, nameBox, addBtn));
  return { el, shown: paint };
}
