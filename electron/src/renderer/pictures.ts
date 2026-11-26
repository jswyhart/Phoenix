// collection → pictures: folders on the left, thumbnails on the right; one click opens a picture, arrows move on.
import { call } from "./api";
import { Page } from "./shell";
import { artUrl, h, textBtn, TileGrid, ViewBar, VList } from "./ui";

interface Folder { name: string; count: number }
interface Pic { name: string; file: string; art: string }

export interface PicturesPage extends Page {
  reload(): Promise<void>;
  openAt(index: number): void; next(): void; close(): void;
  viewerOpen(): boolean; caption(): string; showing(): boolean;
}

export function picturesPage(): PicturesPage {
  let pics: Pic[] = [], index = 0, loaded = false, slide: ReturnType<typeof setInterval> | undefined, showing = false, folder = -1;
  const bar = new ViewBar("pictures", call);
  bar.onChange = () => void showFolder(folder);
  const folderCap = h("div.cap", "FOLDERS"), picCap = h("div.cap", "PICTURES");
  const folders = new VList<Folder>({
    rowHeight: 30,
    render: f => h("div", h("div.grow.ell", { style: { fontSize: "14px" } }, f.name), h("div.faint", { style: { marginLeft: "8px" } }, String(f.count))),
    onSelect: () => void showFolder(folders.selectedIndex()),
  });
  const grid = new TileGrid<Pic>("picture");
  // one click opens a picture, as in the Windows app
  grid.onClick = t => openViewer(pics.indexOf(t.item), false);
  const empty = h("div.center-note.hidden", h("div.note", "No pictures found. Add picture folders in settings."));
  const browse = h("div.cols", { style: { gridTemplateColumns: "260px 1fr" } },
    h("div.pane", folderCap, folders.el),
    h("div.pane.gap", h("div.split", picCap, h("div", { style: { margin: "-6px 28px 6px 0", display: "flex", alignItems: "center", gap: "18px" } }, bar.el, textBtn("play slideshow", () => openViewer(0, true)))), grid.el),
    empty);

  const image = h("img", { draggable: false, alt: "", style: { position: "absolute", inset: "40px 60px", width: "calc(100% - 120px)", height: "calc(100% - 80px)", objectFit: "contain" } });
  const caption = h("div", { style: { position: "absolute", left: "0", right: "0", bottom: "12px", textAlign: "center", color: "silver" } });
  const navBtn = (text: string, side: "left" | "right", click: () => void) =>
    h("button.textbtn.big.viewnav", { onclick: (e: MouseEvent) => { e.stopPropagation(); click(); }, tabIndex: -1,
      style: { position: "absolute", top: "50%", [side]: "12px", transform: "translateY(-50%)", fontSize: "34px", color: "#fff" } }, text);
  const nextBtn = navBtn("›", "right", () => manual(1));
  const closeBtn = h("button.textbtn.big", { onclick: closeViewer, tabIndex: -1, style: { position: "absolute", top: "12px", right: "16px", color: "#fff" } }, "close");
  const viewer = h("div.hidden", { style: { position: "absolute", inset: "0", background: "#000", zIndex: "10" } }, image, caption, navBtn("‹", "left", () => manual(-1)), nextBtn, closeBtn);
  // double-clicking the picture closes the viewer, but quick clicks on the arrows or close button must not
  viewer.addEventListener("dblclick", e => { if (!(e.target as HTMLElement).closest("button")) closeViewer(); });
  const el = h("div.page", { style: { position: "relative" } }, browse, viewer);

  async function reload() {
    loaded = true;
    const list = await call<Folder[]>("pictures.folders");
    folderCap.textContent = `FOLDERS  ${list.length}`;
    folders.setItems(list);
    empty.classList.toggle("hidden", list.length > 0);
    if (list.length > 0) { folders.select(0); await showFolder(0); }
    else { pics = []; grid.setItems([]); picCap.textContent = "PICTURES"; }
  }

  async function showFolder(i: number) {
    if (i < 0) return;
    folder = i;
    pics = await call<Pic[]>("pictures.items", { index: i, view: bar.value });
    picCap.textContent = `PICTURES  ${pics.length}`;
    grid.setItems(pics.map((p, n) => ({ key: String(n), title: p.name, caption: "", art: p.art, item: p })));
  }

  function openViewer(i: number, slideshow: boolean) {
    if (pics.length === 0) return;
    index = Math.max(0, i);
    viewer.classList.remove("hidden");
    showCurrent();
    clearInterval(slide);
    if (slideshow) slide = setInterval(() => step(1), 4000);
  }

  // the neighbours are fetched ahead, so the arrows answer at once
  const preload = (p: Pic) => { new Image().src = artUrl(p.file, 0); };

  function showCurrent() {
    const pic = pics[index];
    const label = `${pic.name}   ·   ${index + 1} of ${pics.length}`;
    showing = false;
    image.style.visibility = "hidden";   // don't leave the previous picture up while this one loads
    caption.textContent = label + "   ·   loading…";
    image.onload = () => { if (pics[index] !== pic) return; showing = true; image.style.visibility = "visible"; caption.textContent = label; };
    image.onerror = () => { if (pics[index] !== pic) return; caption.textContent = label + "   ·   this picture can't be shown"; };
    image.src = artUrl(pic.file, 0);
    if (pics.length > 1) { preload(pics[(index + 1) % pics.length]); preload(pics[(index - 1 + pics.length) % pics.length]); }
  }

  function manual(delta: number) { clearInterval(slide); step(delta); }

  function step(delta: number) {
    if (pics.length === 0) return;
    index = (index + delta + pics.length) % pics.length;
    showCurrent();
  }

  function closeViewer() {
    clearInterval(slide);
    viewer.classList.add("hidden");
    image.removeAttribute("src");
    showing = false;
  }

  document.addEventListener("keydown", e => {
    if (viewer.classList.contains("hidden") || !el.isConnected || document.querySelector(".dialog-shield")) return;
    if (e.key === "Escape") closeViewer();
    else if (e.key === "ArrowLeft" && !e.ctrlKey && !e.metaKey) manual(-1);
    else if (e.key === "ArrowRight" && !e.ctrlKey && !e.metaKey) manual(1);
    else return;
    e.preventDefault();
    e.stopImmediatePropagation();
  }, true);

  return {
    el, shown: () => { if (!loaded) void reload(); }, reload,
    openAt: i => openViewer(i, false), next: () => manual(1), close: closeViewer,
    viewerOpen: () => !viewer.classList.contains("hidden"), caption: () => caption.textContent ?? "", showing: () => showing,
  };
}
