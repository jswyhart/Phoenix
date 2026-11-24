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
  videoSocket: (): Promise<string> => ipcRenderer.invoke("phoenix:videoSocket"),
  /** Self-test only (started with --smoke): where to write results; null otherwise. */
  smoke: smokeArg ? smokeArg.slice("--phoenix-smoke=".length) : null,
  smokeShot: (name: string): Promise<void> => ipcRenderer.invoke("phoenix:smokeShot", name),
  smokeDone: (log: string, fails: number): Promise<void> => ipcRenderer.invoke("phoenix:smokeDone", log, fails),
});
