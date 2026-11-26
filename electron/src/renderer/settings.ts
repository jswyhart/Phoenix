// settings: where the media lives (folders and servers), the look of the app, and the Discogs token.
// Laid out like the Windows app: SOFTWARE / SERVERS across the top, categories down the left, the chosen one on the right.
import { call, host, on, SettingsState } from "./api";
import { debounce, Page, shell } from "./shell";
import { computer, PhoneState } from "./phone";
import { h, pill, tabs, VList } from "./ui";
import { listeningPanel, storagePanel, tabsSection, updatesPanel, visualizerSection } from "./settingsMore";
import { customTabsSection, streamingPanel } from "./settingsWeb";
import { metadataPanel } from "./settingsMeta";
import { loadDisplay, lyricsSection, photosSection, quickplaySection } from "./settingsDisplay";
import { playbackPanel } from "./settingsPlayback";

let measure: CanvasRenderingContext2D | null = null;

/**
 * A folder path shortened, if need be, to fit a width: its start and as many whole trailing folders as fit, with three
 * plain dots between ("/Users/.../Albums/Live").
 */
export function fitPath(path: string, width: number, font = '14px "Selawik"'): string {
  measure ??= document.createElement("canvas").getContext("2d")!;
  measure.font = font;
  const fits = (s: string) => measure!.measureText(s).width <= width + 0.5;
  if (path === "" || fits(path)) return path;
  const sep = path.includes("\\") && !path.includes("/") ? "\\" : "/";
  const parts = path.split(sep);
  // the start of the path: the root and the first folder ("/Users", "C:\Music")
  const headCount = parts[0] === "" || parts[0].endsWith(":") ? 2 : 1;
  if (parts.length > headCount + 1) {
    const head = parts.slice(0, headCount).join(sep);
    for (let tail = parts.length - headCount - 1; tail >= 1; tail--) {
      const s = head + sep + "..." + sep + parts.slice(parts.length - tail).join(sep);
      if (fits(s)) return s;
    }
    // not even the last folder fits beside the start: drop the start
    for (let tail = Math.min(2, parts.length - 1); tail >= 1; tail--) {
      const s = "..." + sep + parts.slice(parts.length - tail).join(sep);
      if (fits(s)) return s;
    }
  }
  // a single name wider than the box: keep as much of its end as fits
  const last = parts[parts.length - 1];
  for (let skip = 1; skip < last.length; skip++) if (fits("..." + last.slice(skip))) return "..." + last.slice(skip);
  return "...";
}

export interface SettingsPage extends Page {
  showCategory(pivot: string, category?: string): void; shown_(): string; refreshFolders(): Promise<void>;
  updates: ReturnType<typeof updatesPanel>; listening: ReturnType<typeof listeningPanel>; meta: ReturnType<typeof metadataPanel>; playback: ReturnType<typeof playbackPanel>;
}

const GROUPS: Record<string, string[]> = {
  software: ["Collection", "Metadata", "Playback", "Listening", "Translation", "Display", "Storage", "Updates"], phone: ["Sync options"], servers: ["Jellyfin", "Plex", "Streaming"],
};

export function settingsPage(): SettingsPage {
  let S: SettingsState = shell.settings;
  let pivot = "software", chosen = "Collection";
  const input = (placeholder: string, password = false) => h("input", { type: password ? "password" : "text", placeholder, spellcheck: false });
  const field = (label: string, el: HTMLElement) => h("div", h("div.label", label), h("div", { style: { margin: "2px 0 8px 0" } }, el));
  const note = (text: string) => h("div.note", text);
  const row = (...els: (HTMLElement | null)[]) => h("div", { style: { display: "flex", alignItems: "center" } }, ...els);
  const status = () => h("div.note", { style: { marginTop: "8px" } });

  // ---- collection ----
  const boxes = new Map<string, VList<string>>();
  const folderList = (kind: string) => (kind === "music" ? S.musicFolders : kind === "video" ? S.videoFolders : kind === "book" ? S.bookFolders : S.pictureFolders);
  const scanStatus = status();
  const rescanBtn = pill("RESCAN COLLECTION NOW", rescan);

  function folders(kind: string, caption: string, height: number) {
    const list: VList<string> = new VList<string>({
      rowHeight: 30, boxed: true,
      render: (path: string): HTMLElement => h("div.name.pathrow", { title: path, data: { path } }, fitPath(path, list.el.clientWidth - 18)),
    });
    list.el.style.height = height + "px";
    list.el.style.flex = "none";
    new ResizeObserver(debounce(() => list.redraw(), 60)).observe(list.el);
    boxes.set(kind, list);
    return h("div", h("div.cap", { style: { margin: "14px 0 4px 0" } }, caption), list.el,
      h("div", { style: { marginTop: "6px" } },
        pill("ADD FOLDER", async () => {
          const picked = await host.pickFolders(`Choose a ${kind} folder`);
          if (picked.length === 0) return;
          S = shell.settings = await call<SettingsState>("settings.addFolders", { kind, paths: picked });
          foldersChanged(kind);
        }),
        pill("REMOVE", async () => {
          const path = list.selected()[0];
          if (!path) return;
          S = shell.settings = await call<SettingsState>("settings.removeFolder", { kind, path });
          foldersChanged(kind);
        })));
  }

  function paintFolders() {
    for (const [kind, list] of boxes) list.setItems([...folderList(kind)]);
  }

  function foldersChanged(kind: string) {
    paintFolders();
    if (kind === "picture") void (shell.get("pictures") as Page & { reload?: () => Promise<void> }).reload?.();
    else void rescan();
  }

  async function rescan() {
    rescanBtn.disabled = true;
    scanStatus.textContent = "scanning…";
    try { await call("scan"); } catch (e: any) { scanStatus.textContent = String(e.message ?? e); }
    await refreshStatus();
    rescanBtn.disabled = false;
  }


  // ---- display ----
  const light = h("input", { type: "radio", name: "theme", checked: S.theme !== "dark" });
  const dark = h("input", { type: "radio", name: "theme", checked: S.theme === "dark" });
  const setTheme = async (theme: string) => {
    S = shell.settings = await call<SettingsState>("settings.set", { theme });
    shell.applyTheme(theme);
  };
  light.onchange = () => void setTheme("light");
  dark.onchange = () => void setTheme("dark");

  // ---- jellyfin ----
  const jfUrl = input("http://192.168.1.10:8096"), jfUser = input("user name"), jfPass = input("password", true);
  jfUrl.value = S.jellyfinUrl;
  jfUser.value = S.jellyfinUser;
  const jfStatus = status();
  const jfConnect = pill("CONNECT & SYNC", async () => {
    if (jfUrl.value.trim() === "" || jfUser.value.trim() === "") { jfStatus.textContent = "enter the server address and user name"; return; }
    await busy(jfConnect, jfStatus, "couldn't connect: ", async () => {
      await call("jellyfin.connect", { url: jfUrl.value, user: jfUser.value, password: jfPass.value });
      jfPass.value = "";
    });
    jfUrl.value = S.jellyfinUrl || jfUrl.value;
  });
  const jfSync = pill("SYNC NOW", () => busy(jfSync, jfStatus, "sync failed: ", () => call("jellyfin.sync")));
  const jfDisconnect = pill("DISCONNECT", () => busy(jfDisconnect, jfStatus, "", () => call("jellyfin.disconnect")));

  // ---- plex ----
  const plexUrl = input("http://192.168.1.10:32400"), plexToken = input("Plex token", true);
  plexUrl.value = S.plexUrl;
  const plexStatus = status();
  const plexServers = h("select.box.hidden", { style: { marginTop: "8px" } });
  let serverUris: string[] = [];
  plexServers.onchange = () => { plexUrl.value = serverUris[plexServers.selectedIndex] ?? plexUrl.value; plexToken.value = ""; plexToken.placeholder = "(from your Plex sign-in)"; };
  const plexSignIn = pill("SIGN IN WITH PLEX…", async () => {
    plexSignIn.disabled = true;
    try {
      const pin = await call<{ id: number; url: string }>("plex.pin");
      await host.openExternal(pin.url);
      plexStatus.textContent = "approve Phoenix in the browser window that just opened…";
      let found: { servers: { label: string; uri: string }[] } | null = null;
      for (let i = 0; i < 90 && !found; i++) {
        await new Promise(r => setTimeout(r, 2000));
        found = await call("plex.check", { id: pin.id });
      }
      if (!found) { plexStatus.textContent = "sign-in timed out — try again"; return; }
      if (found.servers.length === 0) { plexStatus.textContent = "signed in, but no Plex Media Server was found on this account"; return; }
      serverUris = found.servers.map(s => s.uri);
      plexServers.replaceChildren(...found.servers.map(s => h("option", s.label)));
      plexServers.classList.remove("hidden");
      plexServers.selectedIndex = 0;
      plexServers.onchange!(new Event("change"));
      plexStatus.textContent = "signed in — pick a server connection, then choose “connect & sync”";
    } catch (e: any) { plexStatus.textContent = "sign-in failed: " + (e.message ?? e); }
    finally { plexSignIn.disabled = false; }
  });
  const plexConnect = pill("CONNECT & SYNC", () => busy(plexConnect, plexStatus, "couldn't connect: ", async () => {
    await call("plex.connect", { url: plexUrl.value, token: plexToken.value, server: plexServers.classList.contains("hidden") ? null : plexServers.selectedIndex });
    plexToken.value = "";
  }));
  const plexDisconnect = pill("DISCONNECT", () => busy(plexDisconnect, plexStatus, "", async () => { await call("plex.disconnect"); plexToken.value = ""; }));

  async function busy(button: HTMLButtonElement, statusEl: HTMLElement, prefix: string, work: () => Promise<unknown>) {
    button.disabled = true;
    try { await work(); statusEl.textContent = ""; }
    catch (e: any) { statusEl.textContent = prefix + (e.message ?? e); }
    button.disabled = false;
    await refreshStatus();
  }
  on("server.status", (s: { which: string; text: string }) => { (s.which === "plex" ? plexStatus : jfStatus).textContent = s.text; });

  async function refreshStatus() {
    const st = await call<{ jellyfin: string; plex: string; collection: string; settings: SettingsState }>("servers.status");
    S = shell.settings = st.settings;
    jfSync.disabled = jfDisconnect.disabled = !S.jellyfinConnected;
    plexDisconnect.disabled = !S.plexConnected;
    const idle = (t: string | null) => !t || t.startsWith("connected") || t.startsWith("not connected");
    if (idle(jfStatus.textContent)) jfStatus.textContent = st.jellyfin;
    if (idle(plexStatus.textContent)) plexStatus.textContent = st.plex;
    if (scanStatus.textContent !== "" ) scanStatus.textContent = st.collection;
    if (!plexToken.value && S.hasPlexToken) plexToken.placeholder = "(saved)";
  }

  // ---- phone sync ----
  const syncEnabled = h("input", { type: "checkbox" });
  const syncStatus = h("div.statusline", { style: { userSelect: "text" } });
  const newCode = pill("NEW CODE", async () => paintSync(await call<PhoneState>("phone.newCode")));
  newCode.title = "Replaces the pairing code; phones paired with the old code must enter the new one";
  syncEnabled.onchange = async () => paintSync(await call<PhoneState>("phone.enable", { on: syncEnabled.checked }));
  function paintSync(s: PhoneState) {
    syncEnabled.checked = s.enabled;
    newCode.disabled = !s.enabled;
    if (!s.enabled) { syncStatus.textContent = "off"; return; }
    if (!s.running) { syncStatus.textContent = "couldn't start: " + (s.error || "unknown error"); return; }
    syncStatus.textContent = `pairing code  ${s.code}\n` +
      (s.addresses.length === 0 ? `this ${computer} is not on a network` : `this ${computer}'s address  ` + s.addresses.join("  or  ")) +
      (computer === "Mac" ? "\nIf the phone can't find this Mac, allow Phoenix to accept incoming connections when macOS asks (System Settings → Network → Firewall), and local network access under Privacy & Security."
        : `\nIf the phone can't find this PC, allow TCP and UDP port ${s.port} through the firewall.`);
  }
  const refreshSync = async () => paintSync(await call<PhoneState>("phone.state"));

  // ---- the panels ----
  const listening = listeningPanel(), storage = storagePanel(), updates = updatesPanel(), tabsBox = tabsSection(), viz = visualizerSection();
  const streaming = streamingPanel(), ownTabs = customTabsSection(), meta = metadataPanel();
  const quick = quickplaySection(), photos = photosSection(), lyrics = lyricsSection(), playback = playbackPanel();
  const display = { shown: async () => { await loadDisplay(); quick.shown(); photos.shown(); lyrics.shown(); } };
  const extra: Record<string, { shown: () => void | Promise<void> }[]> = { Listening: [listening], Storage: [storage], Updates: [updates], Display: [tabsBox, ownTabs, display, viz], Streaming: [streaming], Metadata: [meta], Translation: [display], Playback: [playback] };
  const panels: Record<string, HTMLElement> = {
    Collection: h("div", h("div.h2", "collection"),
      note("Phoenix watches these folders, and any servers you connect, for music, videos, pictures and books. Everything is checked for changes each time Phoenix starts. Files stay where they are."),
      folders("music", "MUSIC FOLDERS", 84), folders("video", "VIDEO FOLDERS", 64), folders("picture", "PICTURE FOLDERS", 64), folders("book", "BOOK FOLDERS (AUDIOBOOKS, EBOOKS AND COMICS)", 64),
      h("div", { style: { marginTop: "16px", display: "flex", flexWrap: "wrap", rowGap: "6px" } }, rescanBtn,
        (() => { const b = pill("FIND DUPLICATES AND MISSING FILES", () => shell.navigate("care")); b.title = "The same song stored twice, and songs whose files have gone"; return b; })()),
      scanStatus),
    Metadata: meta.el,
    Display: h("div", h("div.h2", "display"), h("div.label", "BACKGROUND"),
      row(h("label.check", { style: { marginRight: "18px" } }, light, "light"), h("label.check", dark, "dark")), tabsBox.el, ownTabs.el, quick.el, photos.el, viz.el),
    Translation: h("div", h("div.h2", "translation and lyrics"), lyrics.el),
    Playback: playback.el,
    Streaming: streaming.el,
    Listening: listening.el,
    Storage: storage.el,
    Updates: updates.el,
    "Sync options": h("div", h("div.h2", "sync options"),
      note(`The companion app on your Android phone copies music, likes and playlists from this ${computer} while both are on the same Wi-Fi network. On the phone, open settings → sync with PC and enter the code below.`),
      h("label.check", { style: { margin: "8px 0 4px 0" } }, syncEnabled, `Let my phone sync with this ${computer}`),
      syncStatus, h("div", { style: { margin: "8px 0 18px 0" } }, newCode),
      note("Right-click songs, albums, artists or playlists in your collection and choose “Add to phone sync list”. They are copied the next time the phone syncs."),
      h("div", pill("VIEW PHONE SYNC LIST", () => shell.navigate("phone")))),
    Jellyfin: h("div", h("div.h2", "jellyfin"),
      note("Add a Jellyfin server to your collection like a folder: its music, movies, shows, collections, photos and books appear alongside your own and stream from the server."),
      field("SERVER ADDRESS", jfUrl),
      h("div", { style: { display: "grid", gridTemplateColumns: "1fr 1fr", columnGap: "10px" } }, field("USER NAME", jfUser), field("PASSWORD", jfPass)),
      h("div", { style: { marginTop: "10px" } }, jfConnect, jfSync, jfDisconnect), jfStatus),
    Plex: h("div", h("div.h2", "plex"),
      note("Add a Plex Media Server to your collection like a folder. Sign in with Plex to find your servers, or enter an address and token yourself."),
      h("div", plexSignIn), plexServers, field("SERVER ADDRESS", plexUrl), field("TOKEN", plexToken),
      h("div", { style: { marginTop: "10px" } }, plexConnect, plexDisconnect), plexStatus),
  };

  const pivots = tabs([["software", "SOFTWARE"], ["phone", "PHONE"], ["servers", "SERVERS"]], p => showCategory(p), "pivot");
  pivots.el.style.margin = "0 0 18px 0";
  const categories = new VList<string>({
    rowHeight: 34,
    render: name => h("div", { style: { fontSize: "15px" } }, name),
    onSelect: items => { if (items[0]) { chosen = items[0]; showPanel(); } },
  });
  const scroll = h("div", { style: { overflowY: "auto", minHeight: "0" } }, h("div", { style: { maxWidth: "600px", margin: "0 18px 10px 0" } }, ...Object.values(panels)));
  const ok = pill("OK", () => shell.goBackOr("quickplay"));
  ok.title = "Changes are saved as you make them";
  ok.style.minWidth = "96px";
  ok.style.marginRight = "0";
  const el = h("div", { style: { height: "100%", display: "grid", gridTemplateColumns: "230px minmax(0, 1fr)", gridTemplateRows: "auto minmax(0, 1fr) auto", padding: "6px 28px 14px 28px" } },
    h("div", { style: { gridColumn: "1 / span 2" } }, pivots.el),
    h("div", { style: { margin: "0 24px 0 -8px", minHeight: "0" } }, categories.el), scroll,
    h("div", { style: { gridColumn: "1 / span 2", display: "flex", justifyContent: "flex-end", marginTop: "10px" } }, ok));

  function showCategory(p: string, category?: string) {
    pivot = GROUPS[p] ? p : "software";
    pivots.select(pivot);
    const names = GROUPS[pivot];
    categories.setItems(names);
    const i = category ? Math.max(0, names.findIndex(n => n.toLowerCase() === category.toLowerCase())) : 0;
    categories.select(i);
    chosen = names[i];
    showPanel();
  }

  function showPanel() {
    for (const [name, panel] of Object.entries(panels)) panel.classList.toggle("hidden", name !== chosen);
    scroll.scrollTop = 0;
    if (chosen === "Collection") requestAnimationFrame(() => boxes.forEach(b => b.redraw()));
    if (chosen === "Sync options") void refreshSync();
    for (const part of extra[chosen] ?? []) void part.shown();
  }

  async function refreshFolders() {
    S = shell.settings = await call<SettingsState>("settings.get");
    paintFolders();
  }

  on("lib", debounce(() => { if (el.isConnected) void refreshStatus(); }, 400));
  showCategory("software");
  paintFolders();
  void refreshStatus();
  return { el, shown: () => { void refreshFolders(); void refreshStatus(); showPanel(); }, showCategory, shown_: () => chosen, refreshFolders, updates, listening, meta, playback };
}
