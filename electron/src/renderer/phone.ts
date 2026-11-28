// The phone page (reached from the phone icon in the lower left): whether phone sync is ready, the pairing code, and
// the "Phone sync" list — the songs the Android app copies the next time it syncs over Wi-Fi.
import { call, isMac, on, Song } from "./api";
import { debounce, Page, shell } from "./shell";
import { menuHooks, SongHeader, songMenu, songRow } from "./songs";
import { confirm, h, menuAt, pill, VList } from "./ui";
import type { SettingsPage } from "./settings";

export interface PhoneState {
  enabled: boolean; running: boolean; error: string; code: string; addresses: string[]; name: string; port: number;
  qr: string; qrUrl: string; phones: { id: string; name: string; when: string }[];
}
export const computer = isMac ? "Mac" : "PC";

/** "Add to phone sync list" for the song, album, artist and playlist menus. */
export function installPhoneMenu() {
  // a phone asks to pair (the window has been brought forward): allow it or not
  on("phone.ask", async (r: { id: string; name: string; address: string }) => {
    const allow = await confirm(`“${r.name}” (${r.address}) wants to pair with this ${computer}: to copy music from it, bring likes and playlists across, and work it as a remote.

Allow it?`, "allow");
    await call("phone.decide", { id: r.id, allow, name: r.name });
  });
  menuHooks.addToPhone = ids => ({
    label: "Add to phone sync list",
    action: async () => {
      const r = await call<{ added: number; skipped: number }>("phone.add", { ids: await ids() });
      const songs = (n: number) => `${n} song${n === 1 ? "" : "s"}`;
      shell.setStatus(r.added === 0 && r.skipped > 0 ? `only music stored on this ${computer} can be copied to the phone`
        : r.added === 0 ? "already on the phone sync list"
        : `${songs(r.added)} added to the phone sync list` + (r.skipped > 0 ? ` (${r.skipped} on a server left out)` : ""));
    },
  });
}

export function phonePage(): Page {
  let songs: Song[] = [], dirty = false, seq = 0;
  const status = h("div", { style: { color: "var(--accent)" } });
  const detail = h("div.note", { style: { marginTop: "8px", userSelect: "text" } });
  const listCap = h("div.cap", { style: { margin: "0 0 6px 8px" } }, "PHONE SYNC LIST");
  const header = new SongHeader();
  header.onChange = () => void load(false);
  const list = new VList<Song>({
    rowHeight: 30, multi: true,
    render: (s, i) => songRow(s, i, true),
    onActivate: s => void call("play.tracks", { ids: songs.map(x => x.id), start: s.id }),
    onContext: (chosen, e) => menuAt(songMenu(chosen, chosen.length === 1 ? songs : null), e),
  });
  const empty = h("div.center-note.hidden", { style: { background: "none", pointerEvents: "none" } },
    h("div.faint", { style: { maxWidth: "460px" } }, "Nothing is chosen here yet. Right-click songs, albums, artists or playlists in your collection and choose “Add to phone sync list”. The phone can also choose its own playlists, artists and albums."));
  // the phone itself: an outline with the accent-coloured screen
  const picture = h("div", { style: { width: "120px", height: "200px", margin: "6px 0 18px 0", position: "relative", border: "3px solid var(--fg-faint)", borderRadius: "14px", background: "var(--bg-alt)" } },
    h("div", { style: { position: "absolute", inset: "19px 8px 27px 8px", background: "var(--grad)" } }),
    h("div", { style: { position: "absolute", left: "51px", bottom: "6px", width: "12px", height: "12px", border: "2px solid var(--fg-faint)", borderRadius: "6px" } }));
  const turnOn = pill("TURN ON PHONE SYNC", async () => {
    const r = await call<PhoneState>("phone.enable", { on: true });
    shell.setStatus(r.running ? "phone sync is on: pair a phone under sync options" : "phone sync couldn't start");
    void load(true);
  });
  const options = pill("SYNC OPTIONS", () => { shell.navigate("settings"); shell.get<SettingsPage>("settings").showCategory("phone"); });
  const left = h("div", { style: { marginRight: "30px" } },
    h("div.h2", { style: { fontSize: "34px" } }, "phone"), picture, status, detail,
    h("div", { style: { marginTop: "18px", display: "flex", flexWrap: "wrap", rowGap: "8px" } }, turnOn, options));
  const right = h("div.pane", listCap, header.el, h("div", { style: { flex: "1 1 0", minHeight: "0", position: "relative" } }, list.el, empty),
    h("div", { style: { marginTop: "10px", flex: "none" } },
      pill("REMOVE", async () => { const ids = list.selected().map(s => s.id); if (ids.length) await call("phone.remove", { ids }); }),
      pill("CLEAR", async () => {
        if (songs.length > 1 && !await confirm(`Take all ${songs.length} songs off the phone sync list? The phone removes its copies the next time it syncs (unless it chose them itself).`, "clear")) return;
        await call("phone.clear");
      })));
  const el = h("div", { style: { height: "100%", display: "grid", gridTemplateColumns: "300px minmax(0, 1fr)", padding: "8px 28px 10px 28px" } }, left, right);

  async function load(keep: boolean) {
    const mine = ++seq;
    const v = await call<{ state: PhoneState; songs: Song[]; total: string }>("phone.view", { sortKey: header.key, sortDesc: header.desc });
    if (mine !== seq) return;
    songs = v.songs;
    list.setItems(songs, keep);
    empty.classList.toggle("hidden", songs.length > 0);
    listCap.textContent = `PHONE SYNC LIST  ${songs.length}   ·   ${v.total}`;
    const s = v.state;
    turnOn.style.display = s.enabled ? "none" : "";
    options.textContent = "SYNC OPTIONS";
    if (!s.enabled) {
      status.textContent = "PHONE SYNC IS OFF";
      detail.textContent = "Turn it on to copy music, likes and playlists to Phoenix on your Android phone over Wi-Fi, and to use the phone as a remote.";
    } else if (!s.running) {
      status.textContent = "PHONE SYNC COULD NOT START";
      detail.textContent = s.error;
    } else {
      status.textContent = "READY TO SYNC OVER WI-FI";
      if (s.phones.length === 0) {
        detail.textContent = "No phone is paired yet. Choose PAIR A PHONE and point the phone's camera at the QR code, or in Phoenix on the phone tap settings → sync with PC → pair with my pc.";
        options.textContent = "PAIR A PHONE";
      } else
        detail.textContent = s.phones.map(p => `${p.name}\n${p.when}`).join("\n\n") + "\n\nThe phone copies this list, and keeps it up to date by itself whenever Phoenix is opened on it.";
    }
  }

  const changed = debounce(() => { if (el.isConnected) void load(true); else dirty = true; });
  on("lib", changed);
  on("ratings", changed);
  on("phone", changed);
  void load(false);
  return { el, shown: () => { dirty = false; void load(true); } };
}
