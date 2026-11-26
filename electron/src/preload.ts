// The only bridge between the page and the rest of the app. The page gets no Node and no Electron: just these calls.
import { contextBridge, ipcRenderer } from "electron";

const smokeArg = process.argv.find(a => a.startsWith("--phoenix-smoke="));
const platformArg = process.argv.find(a => a.startsWith("--phoenix-platform="));

contextBridge.exposeInMainWorld("phoenix", {
  platform: platformArg ? platformArg.slice("--phoenix-platform=".length) : "linux",
  /** Asks the helper (library, player, servers) to do something; see backend/Api*.cs for the methods. */
  call: (method: string, params?: unknown) => ipcRenderer.invoke("phoenix:call", method, params),
  /** Events the helper raises: player, queue, lib, ratings, status, tick, art. */
  on: (listener: (name: string, data: unknown) => void) => {
    ipcRenderer.on("phoenix:event", (_e, name: string, data: unknown) => listener(name, data));
  },
  pickFolders: (title: string): Promise<string[]> => ipcRenderer.invoke("phoenix:pickFolders", title),
  reveal: (path: string): Promise<void> => ipcRenderer.invoke("phoenix:reveal", path),
  openPath: (path: string): Promise<string> => ipcRenderer.invoke("phoenix:openPath", path),
  openExternal: (url: string): Promise<void> => ipcRenderer.invoke("phoenix:openExternal", url),
  setFullscreen: (on: boolean): Promise<void> => ipcRenderer.invoke("phoenix:fullscreen", on),
  /** Asks where to save a file (backups, OPML lists); null when cancelled. */
  saveFile: (title: string, name: string, filters: unknown): Promise<string | null> => ipcRenderer.invoke("phoenix:saveFile", title, name, filters),
  openFile: (title: string, filters: unknown): Promise<string | null> => ipcRenderer.invoke("phoenix:openFile", title, filters),
  /** Closes Phoenix and starts it again (after choosing a backup to restore, or to finish an update). */
  relaunch: (): Promise<void> => ipcRenderer.invoke("phoenix:relaunch"),
  /** Moves a file to the Trash / Recycle Bin; false if it couldn't be. */
  trash: (path: string): Promise<boolean> => ipcRenderer.invoke("phoenix:trash", path),
  /** Closes Phoenix (an update or going back is put in place once it has closed). */
  quit: (): Promise<void> => ipcRenderer.invoke("phoenix:quit"),
  /** Opens (true) or closes (false) the mini player. */
  mini: (on: boolean): Promise<void> => ipcRenderer.invoke("phoenix:mini", on),
  /** Keeps Phoenix running in the tray when its window is closed (true), or not; says whether the icon is shown. */
  tray: (on: boolean): Promise<boolean> => ipcRenderer.invoke("phoenix:tray", on),
  /** Tells the system whether the theme is light or dark, so the title bar it draws matches. */
  themeBase: (base: string, background: string): Promise<void> => ipcRenderer.invoke("phoenix:themeBase", base, background),
  miniState: (): Promise<unknown> => ipcRenderer.invoke("phoenix:miniState"),
  videoSocket: (): Promise<string> => ipcRenderer.invoke("phoenix:videoSocket"),
  /** Self-test only (started with --smoke): where to write results; null otherwise. */
  smoke: smokeArg ? smokeArg.slice("--phoenix-smoke=".length) : null,
  smokeShot: (name: string): Promise<void> => ipcRenderer.invoke("phoenix:smokeShot", name),
  smokeDone: (log: string, fails: number): Promise<void> => ipcRenderer.invoke("phoenix:smokeDone", log, fails),
  smokeLog: (line: string): Promise<void> => ipcRenderer.invoke("phoenix:smokeLog", line),
});
