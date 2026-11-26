// The settings panels brought over from the Windows app: listening (stats, radio, ListenBrainz, Last.fm), the
// collection tabs and visualizer under display, storage (the data folder, backup and restore) and updates.
import { call, host, on, TabsState } from "./api";
import { shell } from "./shell";
import { confirm, h, message, pill } from "./ui";

const note = (text: string, indent = false) => h("div.note", { style: indent ? { margin: "2px 0 0 24px" } : {} }, text);
const label = (text: string, first = false) => h("div.label.caplabel", { style: { margin: first ? "8px 0 3px 0" : "22px 0 3px 0" } }, text);
const status = () => h("div.statusline");
const check = (text: string, checked: boolean, change: (on: boolean) => void | Promise<void>) => {
  const box = h("input", { type: "checkbox", checked });
  box.onchange = () => void change(box.checked);
  return { el: h("label.check", { style: { margin: "4px 0 0 0" } }, box, text), box };
};
const keyRow = (caption: string, input: HTMLElement, ...after: HTMLElement[]) =>
  h("div", { style: { display: "flex", alignItems: "center", marginTop: "8px" } }, h("div.faint", { style: { width: "60px", fontSize: "12px", flex: "none" } }, caption), input, ...after);
const secret = () => h("input", { type: "password", spellcheck: false, style: { width: "300px" } });
const buttons = (...els: HTMLElement[]) => h("div", { style: { marginTop: "10px", display: "flex", flexWrap: "wrap", rowGap: "6px" } }, ...els);

interface Listening {
  lastFm: boolean; listenBrainz: boolean; lastFmReady: boolean; listenBrainzReady: boolean; lastFmUser: string; listenBrainzUser: string; hasLastFmKeys: boolean; result: string;
}

// ---------- listening ----------

export function listeningPanel() {
  let L: Listening | null = null;
  const radio = check("Show radio in the collection", true, async on => {
    try { shell.tabs = await call<TabsState>("tabs.set", { tab: "radio", on }); } catch (e: any) { radio.box.checked = !on; await message(String(e.message ?? e)); }
  });
  const lb = check("Send my listens to ListenBrainz", false, async on => { paint(await call<Listening>("listening.set", { listenBrainz: on })); });
  const lbToken = secret();
  const lbStatus = status();
  const lfm = check("Send my listens to Last.fm", false, async on => { paint(await call<Listening>("listening.set", { lastFm: on })); });
  const lfmKey = secret(), lfmSecret = secret();
  const lfmStatus = status();
  const result = h("div.dim", { style: { marginTop: "14px" } });
  const saveKeys = async () => {
    if (lfmKey.value.trim() === "" && lfmSecret.value.trim() === "") return;
    paint(await call<Listening>("listening.lfmKeys", { key: lfmKey.value, secret: lfmSecret.value }));
    lfmKey.value = lfmSecret.value = "";
  };
  lfmKey.onblur = lfmSecret.onblur = () => void saveKeys();
  const lfmConnect = pill("CONNECT…", async () => {
    await saveKeys();
    lfmConnect.disabled = true;
    try {
      const { token, url } = await call<{ token: string; url: string }>("listening.lfmBegin");
      await host.openExternal(url);
      lfmStatus.textContent = "allow Phoenix in the Last.fm page that just opened in your browser…";
      let name = "";
      for (let i = 0; i < 90 && name === "" && !shell.init.smoke; i++) {
        await new Promise(r => setTimeout(r, 2000));
        const r = await call<{ name: string; state: Listening }>("listening.lfmFinish", { token });
        name = r.name;
        if (name) paint(r.state);
      }
      lfmStatus.textContent = name ? "connected as " + name : "that took too long: choose CONNECT again";
    } catch (e: any) { lfmStatus.textContent = "couldn't connect to Last.fm: " + (e.message ?? e); }
    finally { lfmConnect.disabled = false; }
  });
  const lfmOut = pill("DISCONNECT", async () => { paint(await call<Listening>("listening.lfmSignOut")); lfmStatus.textContent = "disconnected"; });

  function paint(s: Listening) {
    L = s;
    lb.box.checked = s.listenBrainz;
    lb.box.disabled = !s.listenBrainzReady;
    lfm.box.checked = s.lastFm;
    lfm.box.disabled = !s.lastFmReady;
    lbToken.placeholder = s.listenBrainzReady ? "(saved — paste a new one to replace it)" : "user token";
    lfmKey.placeholder = s.hasLastFmKeys ? "(saved)" : "API key";
    lfmSecret.placeholder = s.hasLastFmKeys ? "(saved)" : "shared secret";
    if (!lbStatus.textContent) lbStatus.textContent = s.listenBrainzUser ? "connected as " + s.listenBrainzUser : "";
    if (!lfmStatus.textContent || lfmStatus.textContent.startsWith("connected")) lfmStatus.textContent = s.lastFmReady ? "connected as " + s.lastFmUser : "";
    lfmOut.disabled = !s.lastFmReady;
    result.textContent = s.result;
  }

  const el = h("div", h("div.h2", "listening"),
    note("Phoenix keeps a log of what you listen to, in its own library, for the stats page (quickplay → listening stats). It can also send your listens to ListenBrainz or Last.fm (“scrobbling”). Nothing is sent unless you switch a service on here."),
    h("div", pill("SHOW MY STATS", () => shell.navigate("stats"))),
    label("INTERNET RADIO"), radio.el,
    note("Off: the radio tab and any stations pinned to quickplay are hidden. Your saved stations are kept for when you switch it back on.", true),
    label("LISTENBRAINZ"), lb.el,
    note("Needs your user token, from listenbrainz.org → settings. It is free and needs no application key."),
    keyRow("token", lbToken, pill("CHECK", async () => {
      lbStatus.textContent = "checking…";
      try {
        const r = await call<{ ok: boolean; text: string; state: Listening }>("listening.lbToken", { token: lbToken.value });
        lbStatus.textContent = r.text;
        if (r.ok) lbToken.value = "";
        paint(r.state);
      } catch (e: any) { lbStatus.textContent = "couldn't reach ListenBrainz: " + (e.message ?? e); }
    })), lbStatus,
    label("LAST.FM"), lfm.el,
    note("Needs an API key and its shared secret, from last.fm/api/account/create (free). CONNECT then opens Last.fm in your browser, where you allow Phoenix: you sign in there, not here."),
    keyRow("key", lfmKey), keyRow("secret", lfmSecret),
    buttons(lfmConnect, lfmOut), lfmStatus, result);
  (el.querySelector(".statusline") as HTMLElement).style.marginLeft = "60px";
  lbToken.style.marginRight = "10px";

  return {
    el,
    shown: async () => {
      radio.box.checked = shell.tabs.tabs.find(t => t.key === "radio")?.on ?? true;
      paint(await call<Listening>("listening.state"));
    },
    state: () => L,
  };
}

// ---------- collection tabs (under display) ----------

export function tabsSection() {
  const boxes = h("div", { style: { display: "flex", flexWrap: "wrap", columnGap: "22px", rowGap: "6px", marginTop: "6px" } });
  const paint = (t: TabsState) => {
    boxes.replaceChildren(...t.tabs.map(tab => {
      const c = check(tab.key, tab.on, async on => {
        try { shell.tabs = await call<TabsState>("tabs.set", { tab: tab.key, on }); }
        catch (e: any) { c.box.checked = !on; shell.setStatus(String(e.message ?? e)); }
      });
      c.el.dataset.tab = tab.key;
      return c.el;
    }));
  };
  on("tabs", paint);
  const el = h("div", label("COLLECTION TABS"),
    note("Choose which tabs your collection shows. A tab that is off is only hidden: nothing in it is removed, and it comes back as it was."), boxes);
  return { el, shown: () => paint(shell.tabs) };
}

// ---------- the visualizer (under display) ----------

export function visualizerSection() {
  const select = (names: string[], value: number, change: (i: number) => void) => {
    const s = h("select.box", { style: { width: "260px" } }, ...names.map((n, i) => h("option", { value: String(i) }, n)));
    s.value = String(value);
    s.onchange = () => change(Number(s.value));
    return s;
  };
  const el = h("div");
  const shown = () => {
    const S = shell.settings, init = shell.init;
    const set = async (p: Record<string, number>) => { shell.settings = await call("settings.set", p); };
    el.replaceChildren(
      label("NOW PLAYING VISUALIZER"), select(init.vizStyles, S.visualizer, i => void set({ visualizer: i })),
      label("VISUALIZER COLOURS", true), select(init.vizPalettes, S.visualizerColor, i => void set({ visualizerColor: i })),
      label("VISUALIZER SENSITIVITY", true), select(init.vizSensitivities, S.visualizerSensitivity, i => void set({ visualizerSensitivity: i })));
  };
  return { el, shown };
}

// ---------- storage: the data folder, backup and restore ----------

export function storagePanel() {
  const folder = h("input", { type: "text", readOnly: true });
  const used = h("div.dim", { style: { marginTop: "8px" } });
  const backupStatus = status(), moveStatus = status();
  let standard = "";
  const standardBtn = pill("BACK TO THE STANDARD FOLDER", () => void move(standard));
  async function move(folder: string) {
    moveStatus.textContent = "copying…";
    try {
      const r = await call<{ adopted: boolean; failed: number }>("storage.move", { folder });
      moveStatus.textContent = r.failed ? `${r.failed} file(s) couldn't be copied (a sign-in may need doing again)` : "";
      if (await confirm((r.adopted ? "That folder already holds a Phoenix library: Phoenix will use it." : "Everything has been copied.") + "\n\nPhoenix closes and starts again from the new folder.", "start again")) await host.relaunch();
    } catch (e: any) { moveStatus.textContent = String(e.message ?? e); }
  }
  on("storage.progress", (t: string) => { moveStatus.textContent = t; });
  const el = h("div", h("div.h2", "storage"),
    note("This folder holds Phoenix's library database, settings, album covers, picture and artwork caches and podcast downloads. Your music, videos, pictures and books stay where they are."),
    label("DATA FOLDER", true), folder, used,
    buttons(
      pill("MOVE TO ANOTHER FOLDER", async () => {
        const picked = await host.pickFolders("Choose where Phoenix keeps its data");
        if (picked[0]) await move(picked[0]);
      }),
      standardBtn,
      pill("OPEN", async () => { const r = await call<{ folder: string }>("storage.state"); void host.openPath(r.folder); })),
    note("Moving copies everything to the new folder, starts Phoenix again from there and then removes the old copies. If the folder you pick already holds a Phoenix library, that library is used instead and nothing is copied."),
    moveStatus,
    label("BACKUP AND RESTORE"),
    note("A backup is one file holding your playlists, likes, play counts and the play log, bookmarks and reading progress, podcasts, radio stations, pins, tags and your settings. Your music, videos, pictures and books themselves are not in it. Sign-ins and keys in a backup only work again on the same computer and account."),
    buttons(
      pill("BACK UP NOW…", async () => {
        const file = await host.saveFile("Back up Phoenix", await call<string>("backup.name"), [{ name: "Phoenix backup", extensions: ["phoenixbackup"] }]);
        if (!file) return;
        backupStatus.textContent = "backing up…";
        try { backupStatus.textContent = await call<string>("backup.write", { file }); } catch (e: any) { backupStatus.textContent = "the backup failed: " + (e.message ?? e); }
      }),
      pill("RESTORE FROM A BACKUP…", async () => {
        const file = await host.openFile("Restore Phoenix from a backup", [{ name: "Phoenix backup", extensions: ["phoenixbackup"] }]);
        if (!file) return;
        try {
          const m = await call<{ created: string; device: string; songs: number; playlists: number }>("backup.stage", { file });
          const when = new Date(m.created).toLocaleString();
          if (!(await confirm(`Restore the backup made ${when} on ${m.device} (${m.songs} songs, ${m.playlists} playlists)?\n\nIt replaces this computer's playlists, likes, play counts, podcasts, stations and settings. Phoenix closes and starts again to do it; the library as it is now is kept as library.before-restore.db in the data folder.`, "restore"))) {
            await call("backup.discard");
            return;
          }
          backupStatus.textContent = "restoring: Phoenix will start again…";
          await host.relaunch();
        } catch (e: any) { backupStatus.textContent = "that backup can't be restored: " + (e.message ?? e); }
      })),
    backupStatus);
  return {
    el,
    shown: async () => {
      const r = await call<{ folder: string; used: string; standard: string; isStandard: boolean }>("storage.state");
      standard = r.standard;
      standardBtn.classList.toggle("hidden", r.isStandard || !r.standard);
      folder.value = r.folder;
      used.textContent = r.used;
    },
  };
}

// ---------- updates ----------

interface UpdateState {
  version: string; check: boolean; auto: boolean; keepPrevious: boolean; canInstall: boolean; installFolder: string; waiting: string | null; kept: string | null;
  latest: null | { version: string; name: string; notes: string; page: string; newer: boolean; hasFile: boolean; skipped: boolean };
}

export function updatesPanel() {
  let U: UpdateState | null = null;
  const version = h("div", { style: { fontSize: "15px" } });
  const checkOn = check("Look for a newer Phoenix once a day", true, async on => paint(await call<UpdateState>("updates.set", { check: on })));
  const autoOn = check("Update by itself", false, async on => paint(await call<UpdateState>("updates.set", { auto: on })));
  const keepOn = check("Keep the version before an update, so the update can be undone", true, async on => paint(await call<UpdateState>("updates.set", { keepPrevious: on })));
  const updateStatus = status(), revertStatus = status();
  const notes = h("div.dim", { style: { marginTop: "12px", whiteSpace: "pre-wrap", userSelect: "text" } });
  const checkBtn = pill("CHECK NOW", async () => {
    checkBtn.disabled = true;
    updateStatus.textContent = "asking GitHub…";
    try { const r = await call<{ text: string; state: UpdateState }>("updates.check"); updateStatus.textContent = r.text; paint(r.state); }
    catch (e: any) { updateStatus.textContent = "couldn't reach GitHub: " + (e.message ?? e); }
    finally { checkBtn.disabled = false; }
  });
  const installBtn = pill("DOWNLOAD AND INSTALL", async () => {
    installBtn.disabled = true;
    try {
      await call("updates.install");
      if (await confirm("The new version is ready. Phoenix closes, the new version is put in place, and Phoenix starts again.", "close and update")) await host.quit();
    } catch (e: any) { updateStatus.textContent = String(e.message ?? e); }
    finally { installBtn.disabled = false; }
  });
  const pageBtn = pill("OPEN THE RELEASE PAGE", () => { if (U?.latest) void host.openExternal(U.latest.page); });
  const skipBtn = pill("SKIP THIS VERSION", async () => { paint(await call<UpdateState>("updates.skip")); updateStatus.textContent = "this version won't be offered again"; });
  const revertBtn = pill("GO BACK ONE VERSION", async () => {
    const kept = U?.kept;
    if (!(await confirm(kept ? `Go back to Phoenix ${kept}? Your library and settings are not changed.` : "Go back to the version before this one? It is downloaded from GitHub. Your library and settings are not changed.", "go back"))) return;
    revertBtn.disabled = true;
    try {
      const v = await call<string>("updates.revert");
      revertStatus.textContent = `going back to Phoenix ${v}…`;
      await host.quit();
    } catch (e: any) { revertStatus.textContent = String(e.message ?? e); }
    finally { revertBtn.disabled = false; }
  });
  on("update.status", (t: string) => { updateStatus.textContent = t; });

  function paint(s: UpdateState) {
    U = s;
    version.textContent = `This is Phoenix ${s.version}.`;
    checkOn.box.checked = s.check;
    autoOn.box.checked = s.auto;
    keepOn.box.checked = s.keepPrevious;
    autoOn.box.disabled = !s.canInstall;
    const l = s.latest;
    const newer = !!l && l.newer;
    installBtn.classList.toggle("hidden", !newer || !l!.hasFile || !s.canInstall);
    pageBtn.classList.toggle("hidden", !l);
    skipBtn.classList.toggle("hidden", !newer || l!.skipped);
    notes.textContent = newer ? l!.notes : "";
    if (s.waiting && !updateStatus.textContent) updateStatus.textContent = `Phoenix ${s.waiting} will be put in place when you close Phoenix`;
    revertBtn.disabled = !s.canInstall;
    if (!s.canInstall) revertStatus.textContent = "This copy of Phoenix is in a folder it can't change, so it can't update itself or go back: use the release page.";
    else if (s.kept) revertStatus.textContent = `Phoenix ${s.kept} was kept and can be put back.`;
  }

  const el = h("div", h("div.h2", "updates"), version,
    label("NEW VERSIONS"), checkOn.el,
    note("Asks GitHub for Phoenix's public list of releases. Nothing about you or your library is sent.", true),
    autoOn.el,
    note("On: a new version is downloaded when it is found and put in place when you close Phoenix, without asking; the next start is the new version. Off: Phoenix only tells you, and you choose when to install.", true),
    buttons(checkBtn, installBtn, pageBtn, skipBtn), updateStatus, notes,
    label("GOING BACK"), keepOn.el,
    note("On: before an update, the version you have is kept in Phoenix's data folder (only one is kept). Off: nothing is kept, and going back downloads the earlier version from GitHub when you ask. Your library and settings are not changed by going back.", true),
    buttons(revertBtn), revertStatus);
  return { el, shown: async () => paint(await call<UpdateState>("updates.state")), state: () => U };
}
