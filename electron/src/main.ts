// Phoenix for macOS and Linux: the Electron main process. It opens the window, starts the .NET helper that owns the
// library and the VLC engine, passes the page's requests to it over stdin/stdout, and serves the page and its pictures
// through the private phoenix:// scheme. The page itself has no Node access (see preload.ts).
import { app, BrowserWindow, dialog, ipcMain, Menu, nativeImage, protocol, session, shell } from "electron";
import { ChildProcessWithoutNullStreams, spawn } from "node:child_process";
import { createHash } from "node:crypto";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import * as readline from "node:readline";

const isMac = process.platform === "darwin";
const isWin = process.platform === "win32";

// ---------- command line ----------

const argv = process.argv.slice(1);
const smokeIndex = argv.indexOf("--smoke");
const smokeDir = smokeIndex >= 0 && argv[smokeIndex + 1] ? path.resolve(argv[smokeIndex + 1]) : null;

// ---------- data folder ----------
// PHOENIX_DATA (or the older HALCYON_DATA) wins; otherwise the usual per-user place. A folder left by the Halcyon
// build is carried over once, by copying, so that build keeps working alongside this one.

function defaultDataDir(name: { mac: string; linux: string; win: string }): string {
  if (isMac) return path.join(os.homedir(), "Library", "Application Support", name.mac);
  if (isWin) return path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local"), name.win);
  return path.join(process.env.XDG_DATA_HOME || path.join(os.homedir(), ".local", "share"), name.linux);
}

function resolveDataDir(): string {
  const fromEnv = process.env.PHOENIX_DATA || process.env.HALCYON_DATA;
  if (fromEnv) return path.resolve(fromEnv);
  const dir = defaultDataDir({ mac: "Phoenix", linux: "phoenix", win: "PhoenixDesktop" });
  const old = defaultDataDir({ mac: "Halcyon", linux: "halcyon", win: "HalcyonDesktop" });
  if (!fs.existsSync(dir) && fs.existsSync(path.join(old, "library.db"))) {
    try {
      // caches are left behind: they are rebuilt as needed
      const skip = new Set(["imgcache", "podcast-cache", "books", "vlc.log", "error.log"]);
      fs.mkdirSync(dir, { recursive: true });
      for (const entry of fs.readdirSync(old))
        if (!skip.has(entry)) fs.cpSync(path.join(old, entry), path.join(dir, entry), { recursive: true });
    } catch (e) {
      console.error("Phoenix: could not carry over the Halcyon data folder:", e);
    }
  }
  return dir;
}

if (smokeDir && !process.env.PHOENIX_DATA && !process.env.HALCYON_DATA) {
  // the self-test must never touch a real library
  console.error("Phoenix: set PHOENIX_DATA to a scratch folder before running --smoke.");
  process.exit(64);
}
const dataDir = resolveDataDir();
fs.mkdirSync(dataDir, { recursive: true });
// the browser engine's own files (cache, window state) live inside the data folder too
app.setPath("userData", path.join(dataDir, "electron"));

protocol.registerSchemesAsPrivileged([
  { scheme: "phoenix", privileges: { standard: true, secure: true, supportFetchAPI: true, stream: true } },
]);

// ---------- the helper ----------

type Pending = { resolve: (v: unknown) => void; reject: (e: Error) => void };

class Backend {
  proc: ChildProcessWithoutNullStreams | null = null;
  pending = new Map<number, Pending>();
  nextId = 1;
  ready: Promise<void>;
  onEvent: (name: string, data: unknown) => void = () => {};
  exited = false;
  fatal: string | null = null;

  constructor() {
    this.ready = new Promise((resolve, reject) => {
      const exe = backendPath();
      if (!fs.existsSync(exe)) { reject(new Error(`The Phoenix helper is missing:\n${exe}`)); return; }
      const args = ["--data", dataDir, "--parent", String(process.pid)];
      if (smokeDir) args.push("--smoke");
      const proc = spawn(exe, args, { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
      this.proc = proc;
      proc.stderr.on("data", d => process.stderr.write(d));
      proc.on("error", e => reject(e));
      proc.on("exit", code => {
        this.exited = true;
        for (const p of this.pending.values()) p.reject(new Error("the Phoenix helper stopped"));
        this.pending.clear();
        reject(new Error(this.fatal ?? `The Phoenix helper stopped unexpectedly (code ${code}).`));
        this.onEvent("helper-exit", this.fatal ?? `The Phoenix helper stopped unexpectedly (code ${code}).`);
      });
      readline.createInterface({ input: proc.stdout }).on("line", line => {
        let msg: any;
        try { msg = JSON.parse(line); } catch { return; }
        if (msg.event) {
          if (msg.event === "ready") resolve();
          else if (msg.event === "fatal") this.fatal = String(msg.data);
          else this.onEvent(msg.event, msg.data);
        } else if (typeof msg.id === "number") {
          const p = this.pending.get(msg.id);
          if (!p) return;
          this.pending.delete(msg.id);
          if (msg.error !== undefined) p.reject(new Error(String(msg.error))); else p.resolve(msg.result);
        }
      });
    });
  }

  async call(method: string, params?: unknown): Promise<unknown> {
    await this.ready;
    if (this.exited || !this.proc) throw new Error("the Phoenix helper stopped");
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.proc!.stdin.write(JSON.stringify({ id, method, params: params ?? {} }) + "\n");
    });
  }

  /** Closing its input asks the helper to save and leave; it is killed if it lingers. */
  stop(): Promise<void> {
    return new Promise(resolve => {
      const proc = this.proc;
      if (!proc || this.exited) { resolve(); return; }
      const timer = setTimeout(() => { try { proc.kill(); } catch { /* gone */ } resolve(); }, 4000);
      proc.once("exit", () => { clearTimeout(timer); resolve(); });
      try { proc.stdin.end(); } catch { /* gone */ }
    });
  }
}

function backendPath(): string {
  const name = isWin ? "Phoenix.Backend.exe" : "Phoenix.Backend";
  if (process.env.PHOENIX_BACKEND) return process.env.PHOENIX_BACKEND;
  // packaged: the helper sits beside the app in Electron's resources folder
  const bundled = path.join(process.resourcesPath, "backend", name);
  if (app.isPackaged || fs.existsSync(bundled)) return bundled;
  return path.join(app.getAppPath(), "backend", "bin", "Debug", "net10.0", name);
}

let backend: Backend;
let win: BrowserWindow | null = null;

// ---------- pictures: phoenix://app/_art?p=<artwork>&s=<width> ----------
// Artwork is a local picture, a web address, or "thumb:<video>"; the helper turns the last two into files. Pictures
// are scaled down once and kept in the image cache, so long lists of covers and photo folders stay light.

const webRoot = path.join(__dirname, "web");
const thumbDir = path.join(dataDir, "imgcache");
const needsHelper = /^(https?:|thumb:)|\.(heic|heif|tiff?|dng)$/i;

function sniff(head: Buffer, file: string): string {
  if (head[0] === 0xff && head[1] === 0xd8) return "image/jpeg";
  if (head[0] === 0x89 && head[1] === 0x50) return "image/png";
  if (head.subarray(0, 3).toString("latin1") === "GIF") return "image/gif";
  if (head.subarray(0, 2).toString("latin1") === "BM") return "image/bmp";
  if (head.subarray(8, 12).toString("latin1") === "WEBP") return "image/webp";
  if (/\.svg$/i.test(file)) return "image/svg+xml";
  return "application/octet-stream";
}

function fileResponse(file: string, type?: string): Response {
  const bytes = fs.readFileSync(file);
  return new Response(new Uint8Array(bytes), { headers: { "content-type": type ?? sniff(bytes, file), "cache-control": "no-cache" } });
}

const notFound = () => new Response("", { status: 404 });

async function artResponse(url: URL): Promise<Response> {
  let source = url.searchParams.get("p") ?? "";
  const size = Math.min(4000, Math.max(0, parseInt(url.searchParams.get("s") ?? "0", 10) || 0));
  if (!source) return notFound();
  if (needsHelper.test(source)) {
    const r = (await backend.call("art.resolve", { path: source, size: size || 2400 })) as { file: string | null };
    if (!r.file) return notFound();
    source = r.file;
  }
  let stat: fs.Stats;
  try { stat = fs.statSync(source); } catch { return notFound(); }
  if (!stat.isFile()) return notFound();
  if (size === 0) return fileResponse(source);   // full size, for the picture viewer and the readers

  const key = createHash("sha1").update(`${source}|${stat.mtimeMs}|${size}`).digest("hex");
  const cached = path.join(thumbDir, `t-${key}.jpg`);
  if (fs.existsSync(cached)) return fileResponse(cached, "image/jpeg");

  let image = nativeImage.createEmpty();
  if (isMac) {
    // the system's own thumbnailer: off the main thread, and it reads formats the browser engine does not
    try { image = await nativeImage.createThumbnailFromPath(source, { width: size, height: size * 2 }); } catch { /* fall through */ }
  }
  if (image.isEmpty()) image = nativeImage.createFromPath(source);
  if (image.isEmpty()) {
    // not something the decoder here reads (GIF, WebP, BMP): hand the file to the page as it is
    const head = Buffer.alloc(16);
    try { const fd = fs.openSync(source, "r"); fs.readSync(fd, head, 0, 16, 0); fs.closeSync(fd); } catch { return notFound(); }
    return sniff(head, source) === "application/octet-stream" ? notFound() : fileResponse(source);
  }
  const { width } = image.getSize();
  if (width > size) image = image.resize({ width: size, quality: "good" });
  const jpeg = image.toJPEG(88);
  try { fs.mkdirSync(thumbDir, { recursive: true }); fs.writeFileSync(cached, jpeg); } catch { /* cache is optional */ }
  return new Response(new Uint8Array(jpeg), { headers: { "content-type": "image/jpeg", "cache-control": "no-cache" } });
}

const CSP = "default-src 'none'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self' phoenix: data: blob:; media-src 'self' data: blob:; connect-src 'self' ws://127.0.0.1:*; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
const mime: Record<string, string> = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8",
  ".svg": "image/svg+xml", ".png": "image/png", ".ttf": "font/ttf", ".txt": "text/plain; charset=utf-8",
};

function appResponse(url: URL): Response {
  const rel = decodeURIComponent(url.pathname).replace(/^\/+/, "") || "index.html";
  const file = path.normalize(path.join(webRoot, rel));
  if (!file.startsWith(webRoot + path.sep) || !fs.existsSync(file)) return notFound();
  return new Response(new Uint8Array(fs.readFileSync(file)), {
    headers: { "content-type": mime[path.extname(file).toLowerCase()] ?? "application/octet-stream", "content-security-policy": CSP, "x-content-type-options": "nosniff" },
  });
}

// ---------- window ----------

let mini: BrowserWindow | null = null;

function fromOurPage(e: Electron.IpcMainInvokeEvent): boolean {
  const ours = [win, mini].find(w => !!w && e.sender === w.webContents);
  return !!ours && e.senderFrame === ours.webContents.mainFrame && (e.senderFrame?.url ?? "").startsWith("phoenix://app/");
}

/** The mini player: a small window that stays on top; the main window is hidden while it is open (as on Windows). */
function showMini(on: boolean) {
  if (on && !mini) {
    mini = new BrowserWindow({
      width: 380, height: 96, resizable: false, frame: false, alwaysOnTop: true, skipTaskbar: false, title: "Phoenix", show: false,
      backgroundColor: "#f3f3f3", fullscreenable: false, maximizable: false,
      webPreferences: { preload: path.join(__dirname, "preload.js"), contextIsolation: true, nodeIntegration: false, sandbox: true, webSecurity: true, spellcheck: false,
        additionalArguments: [`--phoenix-platform=${isMac ? "mac" : isWin ? "win" : "linux"}`] },
    });
    mini.webContents.on("will-navigate", e => e.preventDefault());
    mini.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
    mini.once("ready-to-show", () => { mini?.show(); mini?.setAlwaysOnTop(true, "floating"); win?.hide(); });
    mini.on("closed", () => { mini = null; if (win && !win.isVisible()) win.show(); });
    void mini.loadURL("phoenix://app/index.html#mini");
  } else if (!on && mini) {
    const m = mini;
    win?.show();
    win?.focus();
    m.close();
  }
}

function handle(channel: string, fn: (...args: any[]) => unknown) {
  ipcMain.handle(channel, (e, ...args) => {
    if (!fromOurPage(e)) throw new Error("not allowed");
    return fn(...args);
  });
}

function createWindow() {
  win = new BrowserWindow({
    width: 1320, height: smokeDir ? 820 : 840, minWidth: 1080, minHeight: 620,
    useContentSize: !!smokeDir,
    title: "Phoenix",
    backgroundColor: "#ffffff",
    show: false,
    icon: isMac ? undefined : path.join(webRoot, "phoenix-512.png"),
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
      spellcheck: false,
      // streaming services are shown in <webview>s; what they may do is set in "will-attach-webview" below
      webviewTag: true,
      additionalArguments: [`--phoenix-platform=${isMac ? "mac" : isWin ? "win" : "linux"}`, ...(smokeDir ? [`--phoenix-smoke=${smokeDir}`] : [])],
    },
  });
  // the page never leaves itself: no navigation, no new windows
  win.webContents.on("will-navigate", e => e.preventDefault());
  win.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  win.once("ready-to-show", () => win?.show());
  win.on("closed", () => { win = null; });
  win.on("enter-full-screen", () => win?.webContents.send("phoenix:event", "fullscreen", true));
  win.on("leave-full-screen", () => win?.webContents.send("phoenix:event", "fullscreen", false));
  win.loadURL("phoenix://app/index.html");
  if (process.env.PHOENIX_DEVTOOLS) win.webContents.openDevTools({ mode: "detach" });
}

function fail(message: string) {
  if (smokeDir) {
    try { fs.mkdirSync(smokeDir, { recursive: true }); fs.writeFileSync(path.join(smokeDir, "smoke.log"), `FAIL  app starts  ${message}\n1 FAILED\n`); } catch { /* nothing more to do */ }
    app.exit(1);
    return;
  }
  dialog.showErrorBox("Phoenix", message);
  app.exit(1);
}

let quitting = false;
async function quit(code = 0) {
  if (quitting) return;
  quitting = true;
  await backend.stop();
  app.exit(code);
}

if (!smokeDir && !app.requestSingleInstanceLock()) app.quit();
else {
  app.on("second-instance", () => { if (win) { if (win.isMinimized()) win.restore(); win.focus(); } });
  app.on("window-all-closed", () => { void quit(); });
  app.on("before-quit", e => { if (!quitting) { e.preventDefault(); void quit(); } });

  // ---------- streaming services: each in a <webview> with its own sign-in (partition) ----------
  // A service's page gets no Node, a sandbox, and the hook (webguest.js) that lets Phoenix's player bar work it. It
  // may go full screen; everything else it asks for (camera, location, notifications…) is refused. New windows open
  // in the same view, as on Windows. It is shown as an ordinary browser, not as Electron, which some services refuse.
  const plainAgent = () => app.userAgentFallback.replace(/ Electron\/\S+/, "").replace(/ phoenix\/\S+/i, "");
  app.on("web-contents-created", (_e, contents) => {
    contents.on("will-attach-webview", (e, prefs, params) => {
      const src = String(params.src ?? "");
      if (!/^(https?|file):/i.test(src) || !String(params.partition ?? "").startsWith("persist:webview-")) { e.preventDefault(); return; }
      delete (prefs as any).preloadURL;
      prefs.preload = path.join(__dirname, "webguest.js");
      prefs.nodeIntegration = false;
      prefs.nodeIntegrationInSubFrames = false;
      prefs.contextIsolation = true;
      prefs.sandbox = true;
      prefs.webSecurity = true;
    });
    if (contents.getType() !== "webview") return;
    contents.setUserAgent(plainAgent());
    contents.setWindowOpenHandler(({ url }) => {
      if (/^https?:/i.test(url)) void contents.loadURL(url);
      return { action: "deny" };
    });
    contents.session.setPermissionRequestHandler((_wc, permission, callback) => callback(permission === "fullscreen" || permission === "mediaKeySystem"));
  });

  app.whenReady().then(async () => {
    // nothing in the page needs the camera, location, notifications and so on
    session.defaultSession.setPermissionRequestHandler((_wc, _permission, callback) => callback(false));
    if (isMac) {
      Menu.setApplicationMenu(Menu.buildFromTemplate([
        { role: "appMenu" }, { role: "editMenu" },
        { label: "View", submenu: [{ role: "togglefullscreen" }] }, { role: "windowMenu" },
      ]));
    } else Menu.setApplicationMenu(null);

    backend = new Backend();
    backend.onEvent = (name, data) => {
      if (name === "helper-exit") { if (!quitting) fail(String(data)); return; }
      win?.webContents.send("phoenix:event", name, data);
      mini?.webContents.send("phoenix:event", name, data);
    };
    try { await backend.ready; }
    catch (e) { fail(backend.fatal ?? (e as Error).message); return; }

    protocol.handle("phoenix", async request => {
      try {
        const url = new URL(request.url);
        if (url.hostname !== "app") return notFound();
        return url.pathname === "/_art" ? await artResponse(url) : appResponse(url);
      } catch (e) { console.error("phoenix://", e); }
      return notFound();
    });

    handle("phoenix:call", (method: unknown, params: unknown) => {
      if (typeof method !== "string") throw new Error("bad request");
      return backend.call(method, params);
    });
    handle("phoenix:pickFolders", async (title: unknown) => {
      const r = await dialog.showOpenDialog(win!, { title: String(title ?? "Choose a folder"), properties: ["openDirectory", "multiSelections"] });
      return r.canceled ? [] : r.filePaths;
    });
    handle("phoenix:reveal", (file: unknown) => { if (typeof file === "string" && path.isAbsolute(file)) shell.showItemInFolder(file); });
    handle("phoenix:openPath", (file: unknown) => (typeof file === "string" && path.isAbsolute(file) ? shell.openPath(file) : "not a file"));
    handle("phoenix:openExternal", async (url: unknown) => {
      if (typeof url === "string" && /^https?:\/\//i.test(url)) await shell.openExternal(url);
    });
    handle("phoenix:fullscreen", (on: unknown) => { win?.setFullScreen(!!on); });
    const filterList = (f: unknown) => (Array.isArray(f) ? f.filter(x => x && typeof x.name === "string" && Array.isArray(x.extensions)).map(x => ({ name: String(x.name), extensions: x.extensions.map(String) })) : []);
    handle("phoenix:saveFile", async (title: unknown, name: unknown, filters: unknown) => {
      const r = await dialog.showSaveDialog(win!, { title: String(title ?? "Save"), defaultPath: path.join(app.getPath("documents"), path.basename(String(name ?? ""))), filters: filterList(filters) });
      return r.canceled || !r.filePath ? null : r.filePath;
    });
    handle("phoenix:openFile", async (title: unknown, filters: unknown) => {
      const r = await dialog.showOpenDialog(win!, { title: String(title ?? "Open"), properties: ["openFile"], filters: filterList(filters) });
      return r.canceled || r.filePaths.length === 0 ? null : r.filePaths[0];
    });
    handle("phoenix:quit", async () => { if (!smokeDir) await quit(0); });
    handle("phoenix:mini", (on: unknown) => showMini(!!on));
    // self-test: whether the mini player is open, and what it shows
    handle("phoenix:miniState", async () => mini ? { open: true, visible: mini.isVisible(), onTop: mini.isAlwaysOnTop(), text: String(await mini.webContents.executeJavaScript("document.body.innerText")) } : { open: false });
    handle("phoenix:trash", async (file: unknown) => {
      if (typeof file !== "string" || !path.isAbsolute(file) || !fs.existsSync(file)) return false;
      try { await shell.trashItem(file); return true; } catch { return false; }
    });
    handle("phoenix:relaunch", async () => {
      if (smokeDir) return;
      app.relaunch();
      await quit(0);
    });
    handle("phoenix:videoSocket", () => backend.call("video.socket"));

    // self-test: screenshots and the verdict are written by this process, the page only asks
    handle("phoenix:smokeShot", async (name: unknown) => {
      if (!smokeDir || !win) return;
      const image = await win.webContents.capturePage();
      fs.mkdirSync(smokeDir, { recursive: true });
      fs.writeFileSync(path.join(smokeDir, String(name).replace(/[^\w.-]/g, "_") + ".png"), image.toPNG());
    });
    handle("phoenix:smokeDone", (log: unknown, fails: unknown) => {
      if (!smokeDir) return;
      fs.mkdirSync(smokeDir, { recursive: true });
      fs.writeFileSync(path.join(smokeDir, "smoke.log"), String(log));
      void quit(Number(fails) === 0 ? 0 : 1);
    });
    if (smokeDir) setTimeout(() => fail("the self-test did not finish within eight minutes"), 480_000);

    createWindow();
  });
}
