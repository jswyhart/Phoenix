// collection → books: audiobooks, ebooks and comics together, by author. Opening one picks up where it was left.
// The comic and EPUB readers open as a layer over the window.
import { call, host, isMac, on } from "./api";
import { debounce, Page, shell } from "./shell";
import { artUrl, ask, dialogButtons, h, menuAt, message, openDialog, pill, showMenu, tabs, textBtn, TileGrid, VList } from "./ui";

interface BookTile { id: number; title: string; caption: string; art: string }
interface View { empty: boolean; counts: string; authors: string[]; author: string | null; books: BookTile[] }
interface Mark { id: number; label: string; created: string; part: number; position: number }
interface BookInfo { id: number; title: string; author: string; series: string; isAudio: boolean; format: string; local: boolean; started: boolean; finished: boolean; progressText: string; marks: number; playingNow: boolean }

// ---------- opening ----------

export async function openBook(id: number, restart = false) {
  try {
    const r = await call<{ kind: string; file: string }>("book.open", { id, restart });
    if (r.kind === "comic") await ComicReader.open(id);
    else if (r.kind === "epub") await EpubReader.open(id);
    else if (r.kind === "pdf") { const err = await host.openPath(r.file); if (err) throw new Error(err); }
  } catch (e: any) { await message("This book can't be opened.\n\n" + (e.message ?? e)); }
}

/** Takes the book to a bookmark: plays the audiobook from there, or opens the reader at that place. */
export async function openBookmark(book: number, mark: number) {
  const r = await call<{ kind: string; part: number; position: number } | null>("bookmarks.go", { book, mark });
  if (!r) return;
  if (r.kind === "comic") { const c = await ComicReader.open(book); c?.show(r.part); }
  else if (r.kind === "epub") { const e = await EpubReader.open(book); await e?.go(r.part, r.position); }
}

/** A book's bookmarks: jump to one (double-click or "go to"), rename it, or delete it. */
export function bookmarksDialog(book: number, title: string, jump: (m: Mark) => void) {
  const d = openDialog(520);
  let marks: Mark[] = [];
  const list = new VList<Mark>({
    rowHeight: 38, boxed: true,
    render: m => h("div", h("div.grow.ell", m.label), h("div", { style: { fontSize: "12px", opacity: "0.6", marginLeft: "8px" } }, m.created)),
    onActivate: () => go(),
  });
  list.el.style.height = "260px";
  list.el.style.flex = "none";
  const selected = () => list.selected()[0] as Mark | undefined;
  async function reload() {
    const keep = selected()?.id;
    marks = await call<Mark[]>("bookmarks.list", { book });
    list.setItems(marks);
    list.select(Math.max(0, marks.findIndex(m => m.id === keep)));
  }
  function go() { const m = selected(); if (!m) return; d.close(); jump(m); }
  d.onEnter = go;
  d.body.append(h("div.h2", "bookmarks"), h("div.note", title), list.el,
    h("div.buttons", pill("go to", go),
      pill("rename", async () => {
        const m = selected();
        if (!m) return;
        const name = await ask("Rename bookmark", "Name", m.label);
        if (!name || name.trim() === "") return;
        await call("bookmarks.rename", { book, mark: m.id, name });
        await reload();
      }),
      pill("delete", async () => { const m = selected(); if (!m) return; await call("bookmarks.delete", { book, mark: m.id }); await reload(); }),
      pill("close", d.close)));
  void reload();
  return d;
}

// ---------- readers ----------

abstract class Reader {
  protected el: HTMLDivElement;
  protected body = h("div", { style: { position: "relative", minHeight: "0", overflow: "hidden" } });
  protected bar = h("div", { style: { height: "44px", margin: "0 18px", display: "flex", alignItems: "center", justifyContent: "space-between", gap: "12px", flex: "none" } });
  protected pageText = h("div", { style: { marginLeft: "14px", minWidth: "150px", whiteSpace: "nowrap" } });
  protected marksBtn = textBtn("bookmarks", () => this.showBookmarks());
  protected status = h("div.center-note", { style: { whiteSpace: "pre-wrap" } }, "opening…");
  protected title = "";
  private keys = (e: KeyboardEvent) => {
    if (document.querySelector(".dialog-shield") || document.querySelector(".menu")) return;
    if (e.target instanceof HTMLSelectElement && e.key !== "Escape") return;
    if (this.key(e)) { e.preventDefault(); e.stopImmediatePropagation(); }
    else if (e.key !== "MediaPlayPause" && e.key !== "MediaTrackNext" && e.key !== "MediaTrackPrevious") e.stopImmediatePropagation();   // reading is not a shortcut for the player
  };

  protected constructor(readonly book: number) {
    this.el = h("div.reader", { style: { position: "absolute", inset: "0 0 81px 0", background: "var(--bg)", zIndex: "40", display: "grid", gridTemplateRows: "auto minmax(0, 1fr)" } }, this.bar, this.body);
    this.body.append(this.status);
    document.getElementById("root")!.append(this.el);
    document.addEventListener("keydown", this.keys, true);
  }

  protected abstract key(e: KeyboardEvent): boolean;
  protected abstract jump(m: Mark): void;
  protected abstract place(): { part: number; position: number; label: string } | null;

  close() {
    document.removeEventListener("keydown", this.keys, true);
    this.el.remove();
    // the tile's progress changed while reading
    if (shell.page === "books") setTimeout(() => shell.get("books").shown?.(), 200);
  }

  protected setMarks(n: number) { this.marksBtn.textContent = n === 0 ? "bookmarks" : `bookmarks (${n})`; }

  protected async addBookmark() {
    const at = this.place();
    if (!at) return;
    const r = await call<{ label: string; count: number } | null>("bookmarks.add", { book: this.book, part: at.part, position: at.position, label: at.label });
    if (!r) return;
    this.pageText.textContent = `bookmarked: ${r.label}`;
    this.setMarks(r.count);
  }

  protected async showBookmarks() {
    const marks = await call<Mark[]>("bookmarks.list", { book: this.book });
    this.setMarks(marks.length);
    if (marks.length === 0) { this.pageText.textContent = "no bookmarks yet — choose ⚑"; return; }
    const d = bookmarksDialog(this.book, this.title, m => this.jump(m));
    void d.closed.then(async () => this.setMarks((await call<Mark[]>("bookmarks.list", { book: this.book })).length));
  }

  protected fail(what: string, e: any) {
    this.status.classList.remove("hidden");
    this.status.textContent = `This ${what} can't be opened.\n\n${e?.message ?? e}`;
  }
}

/**
 * Comic reader for .cbz / .cbr books: one page at a time, scaled to fit. Click the right or left edge, use the arrow
 * keys, Space, or the mouse wheel to turn pages; the place is saved on every turn.
 */
export class ComicReader extends Reader {
  static current: ComicReader | null = null;
  pages: string[] = [];
  page = 0;
  private image = h("img", { draggable: false, alt: "", style: { position: "absolute", inset: "0", width: "100%", height: "100%", objectFit: "scale-down" } });
  private dirBtn = textBtn("", async () => { shell.settings = await call("settings.set", { comicRightToLeft: !shell.settings.comicRightToLeft }); this.paintDirection(); });
  private wheelAt = 0;

  /** Opens the comic; if it is already open, brings that reader back. */
  static async open(book: number): Promise<ComicReader | null> {
    if (ComicReader.current?.book === book) return ComicReader.current;
    closeReaders();
    const r = (ComicReader.current = new ComicReader(book));
    await r.load();
    return r;
  }

  private constructor(book: number) {
    super(book);
    const name = h("div.ell", { style: { fontSize: "15px", minWidth: "0" } });
    this.bar.append(name, h("div", { style: { display: "flex", alignItems: "center", gap: "10px", flex: "none" } },
      this.dirBtn, textBtn("‹ back", () => this.turn(-1)), textBtn("next ›", () => this.turn(1)), textBtn("⚑", () => void this.addBookmark()), this.marksBtn,
      textBtn("close", () => closeReaders()), this.pageText));
    this.bar.dataset.title = "";
    this.body.prepend(this.image);
    this.body.addEventListener("click", e => {
      if (this.pages.length === 0) return;
      const r = this.body.getBoundingClientRect(), x = (e.clientX - r.left) / Math.max(1, r.width), rtl = shell.settings.comicRightToLeft;
      if (x > 0.7) this.turn(rtl ? -1 : 1); else if (x < 0.3) this.turn(rtl ? 1 : -1);
    });
    this.body.addEventListener("wheel", e => {
      const now = Date.now();
      if (now - this.wheelAt < 150) return;
      this.wheelAt = now;
      this.turn(e.deltaY > 0 ? 1 : -1);
    }, { passive: true });
    this.paintDirection();
    this.nameEl = name;
  }
  private nameEl: HTMLElement;

  private paintDirection() { this.dirBtn.textContent = shell.settings.comicRightToLeft ? "right to left" : "left to right"; }

  private async load() {
    try {
      const c = await call<{ title: string; author: string; pages: string[]; page: number; marks: number }>("comic.open", { id: this.book });
      this.title = c.title;
      this.nameEl.textContent = c.author ? `${c.title} — ${c.author}` : c.title;
      this.pages = c.pages;
      this.setMarks(c.marks);
      this.show(c.page);
    } catch (e) { this.image.classList.add("hidden"); this.fail("comic", e); }
  }

  private turn(delta: number) { if (this.pages.length > 0) this.show(this.page + delta); }

  show(page: number) {
    if (this.pages.length === 0) return;
    this.page = Math.min(this.pages.length - 1, Math.max(0, page));
    this.status.classList.add("hidden");
    this.image.onerror = () => { this.status.classList.remove("hidden"); this.status.textContent = "This page can't be shown."; };
    this.image.src = artUrl(this.pages[this.page], 0);
    this.pageText.textContent = `page ${this.page + 1} of ${this.pages.length}`;
    void call("comic.show", { id: this.book, page: this.page, count: this.pages.length });
  }

  protected jump(m: Mark) { this.show(m.part); }
  protected place() { return this.pages.length ? { part: this.page, position: 0, label: `Page ${this.page + 1} of ${this.pages.length}` } : null; }

  protected key(e: KeyboardEvent): boolean {
    const rtl = shell.settings.comicRightToLeft;
    switch (e.key) {
      case "Escape": closeReaders(); return true;
      case "d": case "D": if (e.ctrlKey || e.metaKey) { void this.addBookmark(); return true; } return false;
      case "ArrowRight": this.turn(rtl ? -1 : 1); return true;
      case "ArrowLeft": this.turn(rtl ? 1 : -1); return true;
      case "PageDown": case " ": this.turn(1); return true;
      case "PageUp": this.turn(-1); return true;
      case "Home": this.show(0); return true;
      case "End": this.show(this.pages.length - 1); return true;
    }
    return false;
  }

  close() { super.close(); if (ComicReader.current === this) ComicReader.current = null; }
}

/**
 * EPUB reader: the text of one chapter at a time in a column that scrolls, with chapter jumping and a text-size
 * setting. Pictures inside the book are not drawn.
 */
export class EpubReader extends Reader {
  static current: EpubReader | null = null;
  chapter = 0;
  chapterText = "";
  private chapters: string[] = [];
  private scroll = h("div", { style: { position: "absolute", inset: "0", overflowY: "auto" } });
  private text = h("div", { style: { maxWidth: "720px", margin: "24px auto 60px auto", padding: "0 40px", whiteSpace: "pre-wrap", userSelect: "text", webkitUserSelect: "text", cursor: "text" } });
  private toc = h("select.box", { style: { width: "280px" } });
  private saveTimer: ReturnType<typeof setTimeout> | undefined;
  private opened = false;

  static async open(book: number): Promise<EpubReader | null> {
    if (EpubReader.current?.book === book) return EpubReader.current;
    closeReaders();
    const r = (EpubReader.current = new EpubReader(book));
    await r.load();
    return r;
  }

  private constructor(book: number) {
    super(book);
    this.bar.append(this.toc, h("div", { style: { display: "flex", alignItems: "center", gap: "10px", flex: "none" } },
      textBtn("‹ chapter", () => void this.go(this.chapter - 1, 0)), textBtn("chapter ›", () => void this.go(this.chapter + 1, 0)),
      textBtn("A−", () => void this.setFont(-1)), textBtn("A+", () => void this.setFont(1)),
      textBtn("⚑", () => void this.addBookmark()), this.marksBtn, textBtn("close", () => closeReaders()), this.pageText));
    this.scroll.append(this.text);
    this.body.prepend(this.scroll);
    this.scroll.addEventListener("scroll", () => { clearTimeout(this.saveTimer); this.saveTimer = setTimeout(() => void this.save(), 600); }, { passive: true });
    this.toc.onchange = () => { void this.go(this.toc.selectedIndex, 0); this.toc.blur(); };
    this.applyFont(shell.settings.readerFontSize);
  }

  private applyFont(size: number) {
    this.text.style.fontSize = size + "px";
    this.text.style.lineHeight = String(1.6);
  }

  private async setFont(delta: number) {
    shell.settings = await call("settings.set", { readerFontSize: shell.settings.readerFontSize + delta * 2 });
    this.applyFont(shell.settings.readerFontSize);
  }

  private async load() {
    try {
      const b = await call<{ title: string; author: string; chapters: string[]; chapter: number; fraction: number; fontSize: number; marks: number }>("epub.open", { id: this.book });
      this.title = b.title;
      this.chapters = b.chapters;
      this.toc.replaceChildren(...b.chapters.map(c => h("option", c)));
      this.applyFont(b.fontSize);
      this.setMarks(b.marks);
      this.opened = true;
      await this.go(b.chapter, b.fraction);
    } catch (e) { this.scroll.classList.add("hidden"); this.fail("book", e); }
  }

  private fraction(): number {
    const max = Math.max(1, this.scroll.scrollHeight - this.scroll.clientHeight);
    return Math.min(1, Math.max(0, this.scroll.scrollTop / max));
  }

  async go(chapter: number, fraction: number) {
    if (!this.opened) return;
    const c = await call<{ index: number; text: string }>("epub.chapter", { id: this.book, index: chapter });
    this.chapter = c.index;
    this.chapterText = c.text;
    this.text.textContent = c.text;
    this.status.classList.add("hidden");
    this.toc.selectedIndex = c.index;
    // the page has not been measured yet: land on the saved place once it has
    await new Promise<void>(r => requestAnimationFrame(() => r()));
    this.scroll.scrollTop = fraction * Math.max(0, this.scroll.scrollHeight - this.scroll.clientHeight);
    await this.save();
  }

  private async save() {
    if (!this.opened || !this.el.isConnected) return;
    const label = await call<string>("epub.save", { id: this.book, chapter: this.chapter, fraction: this.fraction() });
    if (label) this.pageText.textContent = label;
  }

  protected jump(m: Mark) { void this.go(m.part, m.position); }
  protected place() {
    if (!this.opened) return null;
    const f = this.fraction();
    return { part: this.chapter, position: f, label: `${this.chapters[this.chapter] ?? ""}  ·  ${Math.floor(f * 100)}%` };
  }

  protected key(e: KeyboardEvent): boolean {
    if (e.key === "Escape") { closeReaders(); return true; }
    if ((e.key === "d" || e.key === "D") && (e.ctrlKey || e.metaKey)) { void this.addBookmark(); return true; }
    if (e.key === "ArrowRight") { void this.go(this.chapter + 1, 0); return true; }
    if (e.key === "ArrowLeft") { void this.go(this.chapter - 1, 0); return true; }
    return false;
  }

  close() {
    clearTimeout(this.saveTimer);
    if (this.opened) void call("epub.save", { id: this.book, chapter: this.chapter, fraction: this.fraction() }).then(() => call("epub.close", { id: this.book }));
    super.close();
    if (EpubReader.current === this) EpubReader.current = null;
  }
}

export function closeReaders() {
  ComicReader.current?.close();
  EpubReader.current?.close();
}

// ---------- the page ----------

export function booksPage(): Page {
  let tab = "all", author: string | null = null, authors: string[] = [], dirty = false, seq = 0;
  const authorCap = h("div.cap", "AUTHORS"), bookCap = h("div.cap", "BOOKS"), count = h("div.cap.count");
  const tabBar = tabs([["all", "all"], ["audio", "audiobooks"], ["ebook", "ebooks"], ["progress", "in progress"]], t => { tab = t; author = null; void load(); });
  tabBar.el.append(count);
  const authorList = new VList<string>({
    rowHeight: 30,
    render: name => h("div.name", h("div.grow.ell", name)),
    onSelect: () => { const i = authorList.selectedIndex(); author = i <= 0 ? null : authors[i]; void load(); },
  });
  const tiles = new TileGrid<BookTile>("poster");
  tiles.onClick = t => tiles.select(t.key);
  tiles.onActivate = t => void openBook(t.item.id);
  tiles.onContext = (t, e) => void menuFor(t.item.id, e.clientX, e.clientY);
  const empty = h("div.center-note.hidden", h("div.note", { style: { maxWidth: "520px" } },
    "Add a book folder in settings. Phoenix picks up audiobooks (a folder of audio files, or an .m4b) and ebooks (EPUB, PDF, and comics as CBZ or CBR)."));
  const el = h("div.page", tabBar.el, h("div.cols", { style: { gridTemplateColumns: "260px 1fr" } }, h("div.pane", authorCap, authorList.el), h("div.pane.gap", bookCap, tiles.el), empty));

  async function load() {
    const mine = ++seq;
    const v = await call<View>("books.view", { tab, author });
    if (mine !== seq) return;
    tabBar.select(tab);
    empty.classList.toggle("hidden", !v.empty);
    count.textContent = v.counts;
    author = v.author;
    authorCap.textContent = `AUTHORS  ${v.authors.length}`;
    authors = ["all authors", ...v.authors];
    authorList.setItems(authors, true);
    authorList.select(author == null ? 0 : Math.max(0, authors.findIndex(a => a.toLowerCase() === author!.toLowerCase())));
    bookCap.textContent = `BOOKS  ${v.books.length}`;
    tiles.setItems(v.books.map(b => ({ key: String(b.id), title: b.title, caption: b.caption, art: b.art, item: b })));
  }

  async function menuFor(id: number, x: number, y: number) {
    const b = await call<BookInfo | null>("book.info", { id });
    if (!b) return;
    showMenu([
      { label: b.started && !b.finished ? `Continue (${b.progressText})` : b.isAudio ? "Listen" : "Read", action: () => openBook(id) },
      { label: "Start from the beginning", disabled: !(b.started || b.finished), action: () => openBook(id, true) },
      b.format === "pdf" ? null : { label: b.marks === 0 ? "Bookmarks" : `Bookmarks (${b.marks})…`, disabled: b.marks === 0, action: () => { bookmarksDialog(id, b.title, m => void openBookmark(id, m.id)); } },
      b.playingNow ? { label: "Bookmark the current spot", action: async () => { const label = await call<string | null>("book.bookmarkHere"); if (label) shell.setStatus("bookmarked " + label); } } : null,
      "-",
      { label: b.finished ? "Mark as not started" : "Mark as finished", action: async () => { await call("book.finished", { id, value: !b.finished }); await load(); } },
      { label: "Edit book info…", action: () => editBook(b) },
      b.local ? { label: isMac ? "Show in Finder" : "Show in file manager", action: async () => { const p = await call<string | null>("book.path", { id }); if (p) void host.reveal(p); } } : null,
    ], x, y);
  }

  function editBook(b: BookInfo) {
    const d = openDialog(500);
    const title = h("input", { type: "text", value: b.title }), authorBox = h("input", { type: "text", value: b.author }), series = h("input", { type: "text", value: b.series, placeholder: "leave blank if none" });
    const field = (label: string, input: HTMLElement) => h("div.field", h("span", label), input);
    d.body.append(h("div.h2", "edit book info"), field("Title", title), field("Author", authorBox), field("Series", series),
      dialogButtons(d, "save", async () => { await call("book.save", { id: b.id, title: title.value, author: authorBox.value, series: series.value }); d.close(); await load(); }));
    title.focus();
  }

  on("lib", debounce(() => { if (el.isConnected) void load(); else dirty = true; }));
  void load();
  // progress changes while reading or listening; refresh when the page comes back into view
  return { el, shown: () => { dirty = false; void load(); } };
}
