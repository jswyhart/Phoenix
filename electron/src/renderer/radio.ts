// collection → radio: "my stations" (the ones you saved) and "sources" (the Radio Browser directory, browsed by what is
// popular, by genre, by country, or searched by name), where stations are added from. As on Windows.
import { call, on } from "./api";
import { debounce, Page, shell } from "./shell";
import { art, ask, h, iconBtn, MenuEntry, menuAt, tabs, textBtn, ViewBar, VList } from "./ui";

export interface Station { id: number; name: string; url: string; art: string; tags: string; country: string; subtitle: string; saved: boolean; pinned: boolean }
interface Group { name: string; key: string; count: number }

export interface RadioPage extends Page {
  showTab(tab: string): void; tab(): string; mine(): Station[]; results(): Station[];
  show(stations: Station[], title: string): void; save(s: Station): Promise<void>; ready(): Promise<void>;
}

export function radioPage(): RadioPage {
  let tab = "mine", way = "popular", asked = 0, mine: Station[] = [], results: Station[] = [];
  let groupsLoaded: Record<string, Group[]> = {};
  let pending: Promise<void> = Promise.resolve();
  const bar = new ViewBar("radio", call);
  bar.onChange = () => void loadMine();
  const tabBar = tabs([["mine", "my stations"], ["sources", "sources"]], t => showTab(t));
  tabBar.el.append(bar.el);

  // ---- my stations ----
  const mineCap = h("div.cap", "MY STATIONS");
  const hint = h("div.note.hidden", { style: { margin: "4px 0 0 8px" } }, "Stations you save appear here. Find some under “sources”, or add a stream's address yourself.");
  const row = (s: Station, saved: boolean) => h("div", { title: s.url },
    h("div.thumb", { style: { width: "36px", height: "36px", marginRight: "10px" } }, art(s.art, 80)),
    h("div.grow", h("div.ell", { style: { fontSize: "14px" } }, s.name), h("div.ell.faint", { style: { fontSize: "11.5px" } }, s.subtitle)),
    saved ? null : addButton(s));
  const mineList = new VList<Station>({
    rowHeight: 46,
    render: s => row(s, true),
    onActivate: s => void listen(s),
    onContext: (sel, e) => { if (sel[0]) menuAt(menuFor(sel[0], true), e); },
  });
  const minePane = h("div.pane", h("div.split", mineCap, textBtn("add by address", addByAddress)), hint, mineList.el);
  minePane.querySelector(".split")!.setAttribute("style", "margin-right: 28px");

  // ---- sources ----
  const ways = new Map<string, HTMLElement>();
  const waysRow = h("div", { style: { display: "flex", gap: "18px", margin: "0 0 6px 8px" } },
    ...[["popular", "popular"], ["genres", "genres"], ["countries", "countries"]].map(([id, label]) => {
      const b = textBtn(label, () => void browse(id));
      ways.set(id, b);
      return b;
    }));
  const groupNote = h("div.note", { style: { margin: "4px 8px 6px 8px", fontSize: "12px" } });
  const groupList = new VList<Group>({
    rowHeight: 30,
    render: g => h("div.name", h("div.grow.ell", g.name), h("div.faint", { style: { fontSize: "11.5px" } }, g.count.toLocaleString())),
    onSelect: sel => { if (sel[0]) void openGroup(sel[0]); },
  });
  const foundCap = h("div.cap", { style: { margin: "0 16px 0 8px" } }, "STATIONS");
  const searchBox = h("input", { type: "text", spellcheck: false, placeholder: "a station's name", title: "A station's name (what you type is sent to radio-browser.info)", style: { maxWidth: "320px" } });
  searchBox.addEventListener("keydown", e => { if (e.key === "Enter") void find(searchBox.value); });
  const note = h("div.note", { style: { margin: "6px 28px 6px 8px", fontSize: "12px" } });
  const foundList = new VList<Station>({
    rowHeight: 46,
    render: s => row(s, false),
    onActivate: s => void listen(s),
    onContext: (sel, e) => { if (sel[0]) menuAt(menuFor(sel[0], false), e); },
  });
  const left = h("div.pane", h("div.cap", "BROWSE"), waysRow, groupNote, groupList.el);
  const right = h("div.pane", { style: { marginLeft: "18px" } },
    h("div", { style: { display: "flex", alignItems: "center", margin: "-4px 28px 4px 0" } }, foundCap, searchBox, textBtn("search", () => void find(searchBox.value))),
    note, foundList.el);
  const sourcesPane = h("div.cols", { style: { gridTemplateColumns: "300px 1fr" } }, left, right);

  const body = h("div.cols", minePane, sourcesPane);
  const el = h("div.page", tabBar.el, body);

  function addButton(s: Station) {
    const b = iconBtn(s.saved ? "heart" : "plus", s.saved ? "In my stations" : "Add to my stations", () => void save(s), "small");
    b.disabled = s.saved;
    b.style.marginRight = "22px";
    b.dataset.noactivate = "1";
    b.dataset.noselect = "1";
    if (s.saved) b.classList.add("heartfull");
    return b;
  }

  function showTab(t: string) {
    tab = t === "sources" ? "sources" : "mine";
    tabBar.select(tab);
    bar.el.classList.toggle("hidden", tab !== "mine");
    minePane.classList.toggle("hidden", tab !== "mine");
    sourcesPane.classList.toggle("hidden", tab !== "sources");
    void loadMine();
    if (tab === "sources") {
      paintWays();
      if (results.length === 0 && asked === 0 && !shell.init.smoke) void browse("popular");
    }
  }

  async function loadMine() {
    pending = (async () => {
      const r = await call<{ total: number; stations: Station[] }>("radio.mine", { view: bar.value });
      mine = r.stations;
      mineCap.textContent = r.total === mine.length ? `MY STATIONS  ${r.total}` : `MY STATIONS  ${mine.length} of ${r.total}`;
      hint.classList.toggle("hidden", r.total > 0);
      mineList.setItems(mine, true);
      // the + marks follow what is saved
      if (results.length) { const saved = new Set(mine.map(s => s.url)); results = results.map(s => ({ ...s, saved: s.saved || saved.has(s.url) })); foundList.setItems(results, true); }
    })();
    return pending;
  }

  function paintWays() { ways.forEach((b, id) => { b.style.opacity = id === way ? "1" : "0.5"; }); }

  async function browse(w: string) {
    way = w;
    paintWays();
    groupList.setItems([]);
    if (w === "popular") {
      groupNote.textContent = "The stations people tune in to most, from the Radio Browser directory (radio-browser.info). Choose genres or countries above to browse, or search by name on the right.";
      await stations("POPULAR", () => call("radio.popular"));
      return;
    }
    groupNote.textContent = "asking radio-browser.info…";
    try {
      const groups = groupsLoaded[w] ??= await call<Group[]>("radio.groups", { kind: w });
      if (way !== w) return;
      groupList.setItems(groups);
      groupNote.textContent = groups.length === 0 ? "The directory sent nothing back." : w === "genres" ? "Choose a genre to see its stations." : "Choose a country to see its stations.";
    } catch (e: any) { groupNote.textContent = "The station directory can't be reached: " + (e.message ?? e); }
  }

  const openGroup = (g: Group) => stations(g.name.toUpperCase(), () => call("radio.group", { kind: way, key: g.key }));

  async function stations(title: string, fetch: () => Promise<Station[]>) {
    const mineAsk = ++asked;
    foundCap.textContent = title;
    note.textContent = "asking radio-browser.info…";
    try {
      const found = await fetch();
      if (mineAsk !== asked) return;   // something else was chosen meanwhile
      show(found, title);
    } catch (e: any) { if (mineAsk === asked) note.textContent = "The station directory can't be reached: " + (e.message ?? e); }
  }

  function show(found: Station[], title: string) {
    results = found;
    foundCap.textContent = title;
    foundList.setItems(results);
    note.textContent = results.length === 0 ? "No stations found." : `${results.length} stations. Double-click one to listen; the + adds it to your stations.`;
  }

  async function find(text: string) {
    if (text.trim() === "") return;
    if (tab !== "sources") showTab("sources");
    await stations(`“${text.trim().toUpperCase()}”`, () => call("radio.search", { text }));
  }

  async function listen(s: Station) {
    try { await call("radio.play", s); } catch (e: any) { shell.setStatus(String(e.message ?? e)); }
  }

  async function save(s: Station) {
    await call("radio.save", s);
    shell.setStatus(`${s.name} added to my stations`);
    await loadMine();
  }

  async function addByAddress() {
    const url = await ask("Add a station", "Stream address (http://… or https://…)", "https://");
    if (!url || url.trim() === "" || url.trim() === "https://") return;
    const name = (await ask("Add a station", "Name", "")) ?? "";
    try { await call("radio.save", { address: url, name }); await loadMine(); }
    catch (e: any) { shell.setStatus(String(e.message ?? e)); }
  }

  function menuFor(s: Station, saved: boolean): MenuEntry[] {
    return [
      { label: "Listen", action: () => void listen(s) },
      !saved ? { label: "Add to my stations", disabled: s.saved, action: () => void save(s) } : null,
      saved ? { label: s.pinned ? "Unpin from quickplay" : "Pin to quickplay", action: async () => { await call("pin.toggle", { kind: "radio", key: s.url, title: s.name }); await loadMine(); } } : null,
      saved ? { label: "Rename…", action: async () => { const name = await ask("Rename station", "Name", s.name); if (name && name.trim()) { await call("radio.rename", { id: s.id, name }); await loadMine(); } } } : null,
      saved ? "-" : null,
      saved ? { label: "Remove", action: async () => { await call("radio.remove", { id: s.id }); await loadMine(); } } : null,
      { label: "Copy stream address", action: () => void navigator.clipboard.writeText(s.url).catch(() => {}) },
    ];
  }

  on("lib", debounce(() => { if (el.isConnected) void loadMine(); }));
  showTab("mine");
  return {
    el, shown: () => void loadMine(), showTab, tab: () => tab, mine: () => mine, results: () => results, show, save, ready: () => pending,
  };
}
