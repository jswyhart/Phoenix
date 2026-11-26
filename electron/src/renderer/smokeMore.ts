// Self-test for what was brought over from the Windows app: collection tabs, radio, sorting and filtering, the music
// page's tab row and playing time, the sleep timer and speed, listening stats, podcast sources, backup and updates.
import { call, host, PlayerState, Song } from "./api";
import type { MusicPage } from "./music";
import type { PodcastsPage } from "./podcasts";
import type { RadioPage } from "./radio";
import type { SettingsPage } from "./settings";
import { shell } from "./shell";
import type { StatsPage } from "./stats";
import { sleep } from "./ui";
import type { VideosPage } from "./videos";
import { remote, WebServicePage } from "./web";
import type { CarePage } from "./care";
import { albumMenu } from "./songs";
import { cleanUp, fromFileName, tagEditor, titleCase } from "./tools";
import type { MixviewPage } from "./mixview";
import { menuHooks } from "./songs";

type Check = (name: string, ok: boolean, detail?: string) => void;
type Shot = (name: string) => Promise<void>;
type Settle = (ms?: number) => Promise<void>;
type Until = (test: () => boolean | Promise<boolean>, ms: number) => Promise<boolean>;

const text = (sel: string) => document.querySelector(sel)?.textContent ?? "";
const visible = (el: Element | null | undefined) => !!el && (el as HTMLElement).offsetParent !== null;
const subTabs = () => [...document.querySelectorAll<HTMLElement>(".subnav .nav.section")].map(e => e.textContent ?? "");

export async function runExtras(check: Check, shot: Shot, settle: Settle, until: Until, songs: Song[]) {
  // ---- collection tabs ----
  check("radio is a collection tab", subTabs().join(",") === "music,videos,pictures,podcasts,books,playlists,radio", subTabs().join(","));
  shell.tabs = await call("tabs.set", { tab: "pictures", on: false });
  await settle(200);
  check("a collection tab switched off leaves the collection", !subTabs().includes("pictures") && subTabs().length === 6, subTabs().join(","));
  shell.navigate("music");
  shell.tabs = await call("tabs.set", { tab: "music", on: false });
  await settle(300);
  check("switching off the tab on show moves to the first one left", shell.page === "videos" && !subTabs().includes("music"), `on ${shell.page}`);
  for (const t of ["videos", "podcasts", "books", "playlists"]) shell.tabs = await call("tabs.set", { tab: t, on: false });
  let refused = "";
  try { await call("tabs.set", { tab: "radio", on: false }); } catch (e: any) { refused = String(e.message ?? e); }
  check("the last tab can't be switched off", refused.includes("has to stay on") && subTabs().join(",") === "radio", `${refused} / ${subTabs().join(",")}`);
  for (const t of ["music", "videos", "pictures", "podcasts", "books", "playlists"]) shell.tabs = await call("tabs.set", { tab: t, on: true });
  await settle(200);
  check("tabs switched back on return in their places", subTabs().join(",") === "music,videos,pictures,podcasts,books,playlists,radio", subTabs().join(","));

  // ---- the music page: shuffle all is a tab, the playing time follows the counts, and the bar sorts and filters ----
  shell.navigate("music");
  const music = shell.get<MusicPage>("music");
  await music.bar.ready;
  music.setMode("albums");
  await sleep(100);
  await music.ready();
  await settle(300);
  const tabEl = music.tabRow.querySelector<HTMLElement>(".nav.tab:not(.tabaction)")!;
  const shuffleEl = music.tabRow.querySelector<HTMLElement>(".tabaction")!;
  const a = tabEl.getBoundingClientRect(), b = shuffleEl.getBoundingClientRect();
  const sa = getComputedStyle(tabEl), sb = getComputedStyle(shuffleEl);
  check("shuffle all is drawn like the tabs and on their line", shuffleEl.textContent === "shuffle all" && sa.fontSize === sb.fontSize && sa.fontFamily === sb.fontFamily && Math.abs(a.top - b.top) < 1.5 && Math.abs(a.height - b.height) < 1.5,
    `${sb.fontSize} vs ${sa.fontSize}, top ${b.top.toFixed(1)} vs ${a.top.toFixed(1)}`);
  const counts = music.state().counts;
  check("the music page shows the total playing time after the counts", /12 songs  ·  \d+ minutes?$/.test(counts), counts);
  check("music albums: 3 shown with nothing chosen", music.state().albums === 3, String(music.state().albums));
  music.bar.set({ show: "liked" });
  await sleep(100); await music.ready();
  check("show: liked keeps the album with the liked song", music.state().albums === 1, String(music.state().albums));
  music.bar.set({ show: "", text: "later" });
  await sleep(100); await music.ready();
  check("the filter finds the album by name", music.state().albums === 1, String(music.state().albums));
  music.bar.set({ text: "", sort: "title" });
  await sleep(100); await music.ready();
  const forwards = [...document.querySelectorAll(".tilegrid .tile .t1")].filter(visible).map(e => e.textContent ?? "");
  music.bar.set({ desc: true });
  await sleep(100); await music.ready();
  const backwards = [...document.querySelectorAll(".tilegrid .tile .t1")].filter(visible).map(e => e.textContent ?? "");
  check("sorting by title, and turning it round", forwards.length === 3 && forwards.join() === [...forwards].sort((x, y) => x.localeCompare(y)).join() && backwards.join() === [...forwards].reverse().join(),
    `${forwards.join(", ")} / ${backwards.join(", ")}`);
  const saved = (await call<{ views: Record<string, string> }>("smoke.settings")).views;
  check("the sort is remembered for next time", saved.music === "title|1|", saved.music ?? "nothing");
  await shot("20-music-bar");
  music.bar.set({ sort: "", desc: false });
  music.setMode("artists");
  await sleep(100); await music.ready();

  // the other pages carry the bar too
  shell.navigate("videos");
  const videos = shell.get<VideosPage>("videos");
  await videos.bar.ready;
  check("videos carry the sort and filter bar", visible(videos.tabRow.querySelector(".viewbar input.filter")) && videos.tabRow.querySelectorAll(".viewbar select").length === 2);
  for (const page of ["books", "pictures", "playlists"]) {
    shell.navigate(page);
    await settle(250);
    check(`${page} carry the sort and filter bar`, visible(document.querySelector(".page .viewbar input.filter")), page);
  }

  // ---- radio ----
  shell.navigate("radio");
  const radio = shell.get<RadioPage>("radio");
  await radio.ready();
  check("radio opens on my stations, empty", radio.tab() === "mine" && radio.mine().length === 0 && text(".page").includes("Stations you save appear here"));
  await call("radio.save", { address: "https://streams.example.org/jazz.mp3", name: "Example Jazz" });
  await call("radio.save", { name: "Alpha FM", url: "https://streams.example.org/alpha", tags: "pop, rock", country: "GB" });
  await radio.ready(); radio.shown?.(); await radio.ready();
  check("stations added by address and from the directory are listed by name", radio.mine().map(s => s.name).join(",") === "Alpha FM,Example Jazz", radio.mine().map(s => s.name).join(","));
  const jazz = radio.mine().find(s => s.name === "Example Jazz")!;
  await call("radio.rename", { id: jazz.id, name: "Jazz Example" });
  await call("pin.toggle", { kind: "radio", key: jazz.url, title: "Jazz Example" });
  radio.shown?.(); await radio.ready();
  check("a station can be renamed and pinned", radio.mine().some(s => s.name === "Jazz Example" && s.pinned));
  const quick = await call<{ pins: { title: string; caption: string }[] }>("quick.view");
  check("a pinned station shows in quickplay", quick.pins.some(p => p.title === "Jazz Example" && p.caption === "radio"), quick.pins.map(p => p.title).join(","));
  radio.showTab("sources");
  radio.show([{ id: 0, name: "Directory Station", url: "https://streams.example.org/dir", art: "", tags: "news", country: "US", subtitle: "US  ·  news", saved: false, pinned: false },
    { id: 0, name: "Alpha FM", url: "https://streams.example.org/alpha", art: "", tags: "pop", country: "GB", subtitle: "GB  ·  pop", saved: true, pinned: false }], "POPULAR");
  await settle(300);
  check("sources shows found stations, marking the ones already saved", radio.results().length === 2 && document.querySelectorAll(".page .iconbtn.heartfull").length >= 1 && text(".page").includes("2 stations"));
  await shot("21-radio-sources");
  await radio.save(radio.results()[0]);
  check("the + on a found station saves it", radio.mine().length === 3, String(radio.mine().length));
  radio.showTab("mine");
  await settle(300);
  await shot("21-radio");
  shell.tabs = await call("tabs.set", { tab: "radio", on: false });
  const hidden = await call<{ pins: { title: string }[] }>("quick.view");
  check("radio switched off hides its tab and its quickplay pins", !subTabs().includes("radio") && !hidden.pins.some(p => p.title === "Jazz Example") && shell.page !== "radio", shell.page);
  shell.tabs = await call("tabs.set", { tab: "radio", on: true });

  // ---- sleep timer and playback speed ----
  await call("player.volume", { value: 0 });
  await call("play.tracks", { ids: songs.map(s => s.id), start: songs[0].id });
  await until(async () => (await call<PlayerState>("player.state")).pos > 200, 6000);
  await call("player.sleep", { minutes: 30 });
  let st = await call<PlayerState>("player.state");
  await settle(400);
  const moreBtn = [...document.querySelectorAll<HTMLElement>(".transport .iconbtn")].find(b => (b.title ?? "").startsWith("sleep") || b.title === "Sleep timer, playback speed")!;
  check("the sleep timer counts down and lights the more button", st.sleepOn && st.sleep === "sleep in 30 min" && moreBtn?.classList.contains("on") && moreBtn.title.includes("sleep in 30 min"), `${st.sleep}; ${moreBtn?.title}`);
  await call("player.sleep", { atEnd: true });
  st = await call<PlayerState>("player.state");
  check("sleep: when this one ends", st.sleepAtEnd && st.sleep === "sleep: when this ends", st.sleep);
  await call("player.sleep", {});
  st = await call<PlayerState>("player.state");
  check("the sleep timer switches off", !st.sleepOn && st.sleep === "");
  await call("player.speed", { value: 1.5 });
  st = await call<PlayerState>("player.state");
  const p1 = st.pos, t1 = performance.now();
  await sleep(1500);
  const p2 = (await call<PlayerState>("player.state")).pos, t2 = performance.now();
  const rate = (p2 - p1) / (t2 - t1);
  check("playback speed changes, and the clock runs faster", st.canSpeed && st.speed === 1.5 && rate > 1.25, `${st.speed}×, the clock ran ${rate.toFixed(2)}× as fast`);
  await call("player.speed", { value: 1 });
  await call("player.stop");

  // ---- listening stats ----
  await call("smoke.plays", { ids: [songs[0].id, songs[0].id, songs[1].id, songs[5].id] });
  shell.navigate("stats");
  const stats = shell.get<StatsPage>("stats");
  await stats.show(String(new Date().getFullYear()));
  await settle(300);
  const s = stats.shown_()!;
  check("listening stats count the year's listens", !s.empty && s.plays >= 4 && s.songs.length >= 3 && s.songs[0].name === songs[0].title && s.songs[0].plays >= 2, `${s.plays} plays, top ${s.songs[0]?.name}`);
  check("the year is drawn month by month", s.over.length === new Date().getMonth() + 1 && document.querySelectorAll(".statchart > div").length === s.over.length, `${s.over.length} months`);
  check("stats light quickplay at the top", document.querySelector(".topnav.on")?.textContent === "QUICKPLAY");
  await shot("22-stats");
  await stats.show("all time");
  check("all time uses the play counts", stats.shown_()?.title === "all time");

  // ---- podcast sources ----
  shell.navigate("podcasts");
  const pods = shell.get<PodcastsPage>("podcasts");
  await pods.ready();
  check("podcasts open on my podcasts with the bar", pods.tab() === "mine" && visible(document.querySelector(".page .viewbar")));
  pods.showTab("sources");
  await until(() => pods.sources().length >= 2, 3000);
  check("sources list Apple Podcasts and fyyd", pods.sources().map(x => x.name).join(",") === "Apple Podcasts,fyyd", pods.sources().map(x => x.name).join(","));
  check("Apple Podcasts offers its genres", [...document.querySelectorAll(".page .row.name")].some(r => r.textContent === "Arts"));
  const added = await call<{ id: string }>("pdir.addSource", { name: "My list", url: "https://example.org/list.opml" });
  pods.showTab("sources");
  await until(() => pods.sources().length === 3, 3000);
  check("a source of your own is added to the list", added.id === "own:0" && pods.sources()[2]?.name === "My list" && pods.sources()[2].yours);
  pods.showHits([{ title: "A Show", author: "Someone", art: "", feedUrl: "https://example.org/a.xml", detail: "News", appleId: "", subtitle: "Someone  ·  News", have: false }], "TOP PODCASTS");
  await settle(300);
  check("found podcasts are listed with a way to subscribe", pods.hits().length === 1 && text(".page").includes("A Show") && !visible(document.querySelector(".page .viewbar")));
  await shot("23-podcast-sources");
  await call("pdir.removeSource", { id: "own:0" });
  pods.showTab("mine");

  // ---- settings: listening, display tabs, storage and backup, updates ----
  shell.navigate("settings");
  const settings = shell.get<SettingsPage>("settings");
  settings.showCategory("software", "Listening");
  await settle(400);
  const lbBox = [...document.querySelectorAll<HTMLLabelElement>("label.check")].find(l => l.textContent === "Send my listens to ListenBrainz")?.querySelector("input");
  check("settings: listening has ListenBrainz and Last.fm, off until set up", settings.shown_() === "Listening" && !!lbBox && lbBox.disabled && !lbBox.checked && text(".h2:not(.hidden)") !== "" && [...document.querySelectorAll(".pill")].some(p => p.textContent === "CONNECT…" && visible(p)));
  await shot("24-settings-listening");
  settings.showCategory("software", "Display");
  await settle(300);
  const tabBoxes = [...document.querySelectorAll<HTMLElement>("label.check[data-tab]")].filter(visible);
  check("settings: display lists the collection tabs to switch", tabBoxes.length === 7 && tabBoxes.every(t => t.querySelector("input")!.checked), `${tabBoxes.length}`);
  tabBoxes.find(t => t.dataset.tab === "books")!.querySelector("input")!.click();
  await until(() => !subTabs().includes("books"), 2000);
  check("unticking a tab in settings hides it", !subTabs().includes("books"), subTabs().join(","));
  shell.tabs = await call("tabs.set", { tab: "books", on: true });
  await shot("25-settings-display");
  settings.showCategory("software", "Storage");
  await settle(300);
  const file = host.smoke + (host.platform === "win" ? "\\" : "/") + "test.phoenixbackup";
  const wrote = await call<string>("backup.write", { file });
  const manifest = await call<{ songs: number; playlists: number; device: string }>("backup.stage", { file });
  await call("backup.discard");
  check("a backup is written and can be read back", wrote.startsWith("backed up to") && manifest.songs === 12 && manifest.playlists >= 1, `${wrote}; ${manifest.songs} songs`);
  check("settings: storage shows the data folder", (document.querySelector<HTMLInputElement>(".h2 ~ input[readonly]")?.value ?? "").length > 3 || [...document.querySelectorAll<HTMLInputElement>("input[readonly]")].some(i => visible(i) && i.value.length > 3));
  await shot("26-settings-storage");
  settings.showCategory("software", "Updates");
  await settle(400);
  const u = settings.updates.state();
  check("settings: updates shows this version and its switches", !!u && u.version === shell.init.version && /^\d+\.\d+\.\d+$/.test(u.version) && text("#root").includes(`This is Phoenix ${u.version}.`), u?.version ?? "no state");
  const turned = await call<{ auto: boolean; keepPrevious: boolean }>("updates.set", { auto: true, keepPrevious: false });
  check("update by itself and keep-previous can be switched", turned.auto && !turned.keepPrevious);
  await call("updates.set", { auto: false, keepPrevious: true });
  check(host.platform === "win" ? "a development copy says it can't replace itself" : "this copy knows where it is installed",
    host.platform === "win" ? !!u && !u.canInstall : !!u, u ? `${u.canInstall} ${u.installFolder}` : "");
  await shot("27-settings-updates");
  settings.showCategory("software");

  await runStreaming(check, shot, settle, until);
  await runCare(check, shot, settle, songs);
  await runTools(check, shot, settle, songs);
  await runNow(check, shot, settle, until, songs);
  await runPlayback(check, shot, settle, until, songs);
  await runPodcastDownloads(check, shot, settle, until);
}

// ---------- podcast downloads ----------

async function runPodcastDownloads(check: Check, shot: Shot, settle: Settle, until: Until) {
  const feed = await call<string>("smoke.podcastFeed");
  const id = await call<number>("podcasts.add", { url: feed });
  let eps = await call<{ total: number; episodes: { id: number; title: string; downloaded: boolean }[] }>("podcasts.episodes", { id });
  check("a podcast is subscribed to from its feed", eps.total === 3 && eps.episodes[0].title === "Episode 1", `${eps.total} episodes`);
  await call("episode.download", { podcast: id, episode: eps.episodes[0].id });
  eps = await call("podcasts.episodes", { id });
  let ps = await call<{ usage: string; folder: string }>("podcastSettings.state");
  check("an episode is downloaded into the podcasts folder", eps.episodes[0].downloaded && ps.usage.startsWith("1 episode downloaded"), ps.usage);
  await call("player.volume", { value: 0 });
  await call("podcasts.play", { podcast: id, episode: eps.episodes[0].id });
  const playing = await until(async () => (await call<PlayerState>("player.state")).pos > 300, 6000);
  check("a downloaded episode plays from its file", playing && (await call<PlayerState>("player.state")).current?.title === "Episode 1");
  await call("player.stop");
  shell.navigate("podcasts");
  const pods = shell.get<PodcastsPage>("podcasts");
  await pods.ready();
  await pods.select(id);
  await settle(300);
  check("the podcasts page marks the downloaded episode", text(".page").includes("downloaded"));
  await shot("40-podcast-downloads");
  shell.navigate("settings");
  const settings = shell.get<SettingsPage>("settings");
  settings.showCategory("software", "Podcasts");
  await settle(400);
  check("settings: podcasts shows the download choices and folder", text("#root").includes("KEEP DOWNLOADED") && text("#root").includes("1 episode downloaded"));
  await shot("41-settings-podcasts");
  ps = await call("podcastSettings.removeAll");
  check("all downloads can be removed", ps.usage === "No episodes are downloaded.", ps.usage);
  await call("podcasts.remove", { id });
  settings.showCategory("software");
}

// ---------- playback: equalizer, levelling, crossfade ----------

async function runPlayback(check: Check, shot: Shot, settle: Settle, until: Until, songs: Song[]) {
  const bands = await call<number[]>("playback.bands");
  check("the equalizer has the standard ten bands, 31 Hz to 16 kHz", bands.length === 10 && Math.abs(bands[0] - 31) < 2 && Math.abs(bands[9] - 16000) < 100, bands.map(Math.round).join(", "));
  let pb = await call<any>("playback.set", { eqEnabled: true, eqPreset: "rock" });
  check("an equalizer preset sets the bands", pb.eqPreset === "rock" && pb.eqGains[0] === 5 && pb.eqGains[9] === 4.5, pb.eqGains.join(","));
  pb = await call<any>("playback.set", { band: 2, gain: -3.3 });
  check("moving a band by hand makes it custom", pb.eqPreset === "custom" && pb.eqGains[2] === -3.5, `${pb.eqPreset} ${pb.eqGains[2]}`);
  shell.navigate("settings");
  const settings = shell.get<SettingsPage>("settings");
  settings.showCategory("software", "Playback");
  await settle(400);
  check("settings: playback shows crossfade, levelling and ten equalizer sliders", document.querySelectorAll("input.eqband").length === 10 && text("#root").includes("CROSSFADE") && text("#root").includes("Play every song at about the same loudness"));
  await shot("39-settings-playback");
  await call("playback.set", { eqEnabled: false, eqPreset: "flat" });

  pb = await call<any>("playback.set", { levelling: true, levelMeasure: true });
  await call("player.volume", { value: 0 });
  await call("play.tracks", { ids: [songs[0].id] });
  const lv = await call<{ options: string[] }>("smoke.playback", { id: songs[0].id });
  await call("player.stop");
  check("levelling asks VLC for ReplayGain, and evens out a song without tags", lv.options.some(o => o === ":audio-replay-gain-mode=track") && lv.options.includes(":audio-filter=normvol"), lv.options.join(" "));
  await call("playback.set", { levelling: false });

  // crossfade: the next song starts before the last one has ended
  await call("player.volume", { value: 0 });
  await call("playback.set", { crossfade: 1 });
  const before = (await call<{ handovers: number }>("smoke.playback", { id: songs[0].id })).handovers;
  await call("play.tracks", { ids: songs.slice(0, 3).map(s => s.id), start: songs[0].id });
  const started = Date.now();
  await until(async () => (await call<PlayerState>("player.state")).current?.title === songs[1].title, 8000);
  const took = Date.now() - started;
  const after = (await call<{ handovers: number }>("smoke.playback", { id: songs[0].id })).handovers;
  const st = await call<PlayerState>("player.state");
  check("with crossfade, the next song starts before the last one ends", after === before + 1 && st.current?.title === songs[1].title && took < 3900 && st.playing, `${took} ms, ${after - before} hand-over`);
  await sleep(600);
  check("and plays on", (await call<PlayerState>("player.state")).pos > 200);
  await call("player.stop");
  await call("playback.set", { crossfade: 0 });
  settings.showCategory("software");
}

// ---------- Mixview, lyrics, quickplay clicks, display settings ----------

async function runNow(check: Check, shot: Shot, settle: Settle, until: Until, songs: Song[]) {
  const key = await call<string>("track.albumKey", { id: songs[0].id });
  menuHooks.mixview(key, "Quiet Trio");
  const mix = shell.get<MixviewPage>("mixview");
  await until(() => mix.centre()?.title === "First Record", 3000);
  for (let i = 0; i < 40; i++) mix.step(0.05);
  await settle(300);
  const c = mix.centre(), around = mix.around();
  check("Mixview puts the album in the middle with its artist and related artists around it", c?.title === "First Record" && around.some(n => n.title === "Quiet Trio" && !n.albumKey),
    `${c?.title}: ${around.map(n => n.title).join(", ")}`);
  const tiles = [...document.querySelectorAll<HTMLElement>(".mixtile")];
  check("Mixview's tiles spread out around the middle", tiles.length === around.length + 1 && new Set(tiles.map(t => t.style.transform)).size === tiles.length, `${tiles.length} tiles`);
  await shot("37-mixview");
  const other = around.find(n => !n.albumKey);
  if (other) {
    tiles.find(t => t.querySelector(".mixtitle")?.textContent === other.title)?.click();
    await until(() => mix.centre()?.title === other.title, 3000);
    check("clicking an artist moves it to the middle with its albums around it", mix.centre()?.title === other.title && mix.around().some(n => n.title === "First Record"), mix.centre()?.title ?? "");
  }

  // lyrics: timed lines follow the song
  await call("lyrics.import", { id: songs[0].id, text: "[ar:Quiet Trio]\n[00:00.00]First line\n[00:01.50]Second line\n[00:03.00]Third line" });
  await call("player.volume", { value: 0 });
  await call("play.tracks", { ids: [songs[0].id] });
  shell.showNow(true);
  const now = (shell as any).nowPlaying as import("./nowplaying").NowPlaying;
  await now.showLyrics(true);
  await until(() => now.lyricsShown.lines === 3, 4000);
  await until(() => now.lyricsShown.now >= 1, 4000);
  check("now playing shows the song's lyrics, and timed lines follow the music", now.lyricsShown.lines === 3 && now.lyricsShown.synced && now.lyricsShown.now >= 1 && text(".lyricline.sung") !== "",
    `${now.lyricsShown.lines} lines, on ${now.lyricsShown.now}: ${text(".lyricline.sung")}`);
  await shot("38-lyrics");
  await call("lyrics.remove", { id: songs[0].id });
  await now.showLyrics(false);
  shell.showNow(false);
  await call("player.stop");

  // quickplay: a click opens the album, unless settings say it plays
  shell.display = await call("display.set", { quickplayPlays: false });
  shell.navigate("quickplay");
  await settle(400);
  const tile = [...document.querySelectorAll<HTMLElement>(".tile.small")].find(t => t.querySelector(".t1")?.textContent === "First Record");
  tile?.click();
  await settle(500);
  const music = shell.get<MusicPage>("music");
  check("a click on an album in quickplay opens it in the collection", shell.page === "music" && music.state().songs.length === 4 && document.querySelector(".tilegrid .tile.sel .t1")?.textContent === "First Record",
    `${shell.page}, ${music.state().songs.length} songs`);
  shell.navigate("settings");
  const settings = shell.get<SettingsPage>("settings");
  settings.showCategory("software", "Display");
  await settle(400);
  check("settings: display has quickplay and artist photos", text("#root").includes("Clicking an album in quickplay plays it straight away") && text("#root").includes("Show photos of the artist behind now playing"));
  settings.showCategory("software", "Translation");
  await settle(300);
  check("settings: translation and lyrics has the LRCLIB switch", text("#root").includes("Look lyrics up on LRCLIB by itself"));
  settings.showCategory("software");
}

// ---------- the tag editor, your own tags, Smart DJ and auto playlists ----------

async function runTools(check: Check, shot: Shot, settle: Settle, songs: Song[]) {
  check("tidy capitals keeps small words small and leaves capitals inside words", titleCase("the end of the road") === "The End of the Road" && titleCase("songs by AC/DC") === "Songs by AC/DC" && titleCase("mcCartney live") === "mcCartney Live",
    titleCase("the end of the road"));
  check("clean up takes out underscores, extra spaces and a leading number", cleanUp("03 - my_song  title ", true) === "my song title" && cleanUp("a__b", false) === "a b", cleanUp("03 - my_song  title ", true));
  const read = fromFileName("07 - Artist Name - A Title", "%n - %a - %t");
  check("a file name is read by its pattern", read?.n === "07" && read?.a === "Artist Name" && read?.t === "A Title" && fromFileName("no numbers here", "%n - %t") === null, JSON.stringify(read));

  shell.navigate("music");
  const album = (await call<{ albums: { key: string; title: string }[] }>("music.view", { mode: "albums" })).albums.find(a => a.title === "Later Record")!;
  const info = await call<{ ids: number[] }>("album.info", { key: album.key });
  const ed = (await tagEditor(info.ids))!;
  await settle(300);
  check("the tag editor opens with a row for each song", ed.rows.length === 4 && document.querySelectorAll(".tagrow[data-i]").length === 4, `${ed.rows.length} rows`);
  const fromNames = ed.fromFileNames("%n %t");
  check("from file names fills in numbers and titles", fromNames.done === 4 && ed.rows.every(r => /^Song \d$/.test(r.title) && r.trackNo > 0), `${fromNames.done} done, ${fromNames.skipped} skipped`);
  const swapped = ed.replace("Song", "Track");
  ed.numberTracks();
  await shot("35-tag-editor");
  const failed = await ed.save();
  ed.close();
  let now = (await call<Song[]>("album.songs", { key: album.key })).map(s => s.title);
  check("the tag editor saves its changes", swapped === 4 && failed === 0 && now.every(t => t.startsWith("Track ")), now.join(", "));
  const back = (await tagEditor(info.ids))!;
  back.replace("Track", "Song");
  await back.save();
  back.close();
  now = (await call<Song[]>("album.songs", { key: album.key })).map(s => s.title);
  check("and back again", now.every(t => t.startsWith("Song ")), now.join(", "));

  // your own tags
  await call("owntags.apply", { ids: [songs[2].id, songs[3].id], text: "workout, late night", before: [] });
  const tagged = (await call<Song[]>("album.songs", { key: (await call<string>("track.albumKey", { id: songs[2].id })) })).find(s => s.id === songs[2].id)!;
  const shared = await call<{ common: string[]; all: { tag: string; count: number }[] }>("owntags.get", { ids: [songs[2].id, songs[3].id] });
  check("your own tags go on the songs and are listed with their counts", tagged.tags.includes("workout") && shared.common.length === 2 && shared.all.some(t => t.tag === "workout" && t.count === 2), `${tagged.tags} / ${shared.all.map(t => t.tag + " " + t.count).join(", ")}`);
  await call("owntags.apply", { ids: [songs[2].id, songs[3].id], text: "", before: shared.common });

  // Smart DJ
  await call("player.volume", { value: 0 });
  const said = await call<string>("dj.start", { ids: [songs[0].id] });
  const q = await call<PlayerState>("player.state");
  check("Smart DJ starts a mix like the song", said.startsWith("smart dj") && shell.queue.length >= 1 && q.current != null, `${said}; ${shell.queue.length} in the queue`);
  await call("player.stop");

  // an auto playlist made by rules
  shell.navigate("playlists");
  const id = await call<string>("smart.save", { name: "Rule list", rating: 1, sort: 0, limit: 0, maxPlays: -1 });
  const lists = await call<{ name: string; smart: string | null; rules: string }[]>("playlists.list");
  const row = lists.find(l => l.smart === id);
  const liked = await call<{ songs: Song[] }>("playlists.songs", { smart: id });
  check("an auto playlist made by rules lists the songs that match", !!row && row.rules.startsWith("liked") && liked.songs.length >= 1 && liked.songs.every(s => s.rating > 0), `${row?.rules}; ${liked.songs.length} songs`);
  check("its rules count what they match", (await call<string>("smart.count", { name: "x", rating: 0, limit: 5, maxPlays: -1 })) === "12 songs match; the playlist keeps 5");
  await settle(400);
  await shot("36-auto-playlist");
  await call("smart.delete", { id });
}

// ---------- library care and metadata ----------

async function runCare(check: Check, shot: Shot, settle: Settle, songs: Song[]) {
  const made = await call<{ copy: string; tracks: number }>("smoke.copySong", { id: songs[0].id });
  check("a second copy of a song is scanned in", made.tracks === 13, `${made.tracks} songs`);
  shell.navigate("care");
  const care = shell.get<CarePage>("care");
  await care.show("dups");
  await settle(300);
  const g = care.groups();
  check("the duplicate finder finds the copy by name and length", g.length === 1 && g[0].tracks.length === 2 && g[0].why.startsWith("same artist, title and length"), g.map(x => `${x.tracks.length} × ${x.tracks[0].title}: ${x.why}`).join("; "));
  check("library care lights settings and lists the copies with a choice of which to keep", document.querySelector(".topnav.on")?.textContent === "SETTINGS" && document.querySelectorAll(".dupgroup input[type=radio]").length === 2);
  await shot("32-care-duplicates");
  const copyId = g[0].tracks.find(t => t.path === made.copy)!.id;
  const original = g[0].tracks.find(t => t.path !== made.copy)!.id;
  await call("rate", { ids: [copyId], rating: 1 });
  const r = await call<{ files: string[] }>("care.resolve", { group: 0, keep: original });
  const after = await call<{ tracks: number }>("smoke.state").then((x: any) => x.counts);
  const liked = (await call<{ songs: Song[] }>("music.view", { mode: "songs" })).songs.find(x => x.id === original);
  check("keeping one copy takes the other out and passes on its like", after.tracks === 12 && r.files.length === 1 && r.files[0] === made.copy && liked?.rating === 1, `${after.tracks} songs, rating ${liked?.rating}`);

  // the copy's file is still there and not in the library; scan it in again, then take its file away
  await call("smoke.copySong", { id: songs[0].id });
  await call("smoke.deleteFile", { path: made.copy });
  await care.show("missing");
  await settle(300);
  check("the missing-file finder lists the song whose file has gone", care.missing().length === 1 && care.missing()[0].path === made.copy, care.missing().map(t => t.path).join(", "));
  await shot("33-care-missing");
  await call("care.removeMissing", { ids: [] });
  await care.show("missing");
  const counts = await call<any>("smoke.state").then(x => x.counts);
  check("missing songs can be taken out of the library", care.missing().length === 0 && counts.tracks === 12, `${counts.tracks} songs`);

  // metadata sources
  shell.navigate("settings");
  const settings = shell.get<SettingsPage>("settings");
  settings.showCategory("software", "Metadata");
  await settle(400);
  let m = settings.meta.state()!;
  check("settings: metadata lists every source with its weight", !!m && m.sources.length === 8 && document.querySelectorAll(".metasource").length === 8 && m.order[0] === "Pictures in the album's folder",
    m ? `${m.sources.length} sources; ${m.order.join(" → ")}` : "none");
  m = await call("meta.setSource", { id: "deezer", weight: 10 });
  check("a higher weight asks a source sooner", m.order.indexOf("Deezer") < m.order.indexOf("MusicBrainz + Cover Art Archive"), m.order.join(" → "));
  m = await call("meta.setSource", { id: "deezer", weight: 5, enabled: false });
  check("a source switched off is not asked", !m.order.includes("Deezer"), m.order.join(" → "));
  await call("meta.setSource", { id: "deezer", enabled: true });
  check("sources that need a key say so", m.sources.filter(s => s.keyHelp && !s.ready).map(s => s.id).sort().join() === "discogs,fanart,lastfm,theaudiodb", m.sources.filter(s => !s.ready).map(s => s.id).join());
  await shot("34-settings-metadata");
  const key = (await call<{ albums: { key: string }[] }>("music.view", { mode: "albums" })).albums[0].key;
  await call("meta.tie", { key, source: "itunes" });
  const tied = await call<{ tied: string }>("meta.album", { key });
  check("an album can be tied to one source", tied.tied === "itunes");
  await call("meta.tie", { key, source: "" });
  const menu = await albumMenu(key);
  const labels = menu.filter((x): x is { label: string } => !!x && typeof x === "object").map(x => x.label);
  check("the album menu offers find album info, its info source, the tag editor and Mixview", ["Find album info…", "Info source", "Tag editor…", "Mixview", "Start Smart DJ"].every(l => labels.includes(l)), labels.join(", "));
  settings.showCategory("software");
}

// ---------- streaming services and tabs of your own ----------

async function runStreaming(check: Check, shot: Shot, settle: Settle, until: Until) {
  const tabsIn = (row: Element | null) => [...(row?.querySelectorAll<HTMLElement>(".service-tab") ?? [])].map(t => t.textContent ?? "");
  shell.navigate("videos");
  const videos = shell.get<VideosPage>("videos");
  await call("web.set", { id: "youtube", on: true });
  await until(() => tabsIn(videos.tabRow).length === 1, 3000);
  check("a video service switched on sits under videos, after its tabs", tabsIn(videos.tabRow).join() === "youtube" && videos.tabRow.querySelector(".service-tab")!.previousElementSibling?.textContent === "all",
    tabsIn(videos.tabRow).join());
  const own = [...videos.tabRow.querySelectorAll<HTMLElement>(".nav.tab:not(.service-tab)")].find(visible)!.getBoundingClientRect(), svc = videos.tabRow.querySelector<HTMLElement>(".service-tab")!.getBoundingClientRect();
  check("its tab is drawn like the page's own and on their line", Math.abs(own.top - svc.top) < 1.5 && Math.abs(own.height - svc.height) < 1.5, `${svc.top} vs ${own.top}`);
  await shot("28-videos-with-service");
  await call("web.set", { id: "ytmusic", on: true });
  await until(() => subTabs().includes("youtube music"), 3000);
  check("a music service gets a collection tab of its own", subTabs().at(-1) === "youtube music", subTabs().join(","));
  await call("web.place", { id: "ytmusic", place: "music" });
  await until(() => !subTabs().includes("youtube music"), 3000);
  shell.navigate("music");
  const music = shell.get<MusicPage>("music");
  await until(() => tabsIn(music.tabRow).length === 1, 3000);
  check("a service can be put under any collection", tabsIn(music.tabRow).join() === "youtube music" && !subTabs().includes("youtube music"), tabsIn(music.tabRow).join());

  // a tab of your own
  const made = await call<{ id: string; page: string }>("tabs.addCustom", { name: "Watch" });
  await until(() => subTabs().includes("watch"), 3000);
  check("a tab of your own joins the collection after its tabs", subTabs().join(",") === "music,videos,pictures,podcasts,books,playlists,radio,watch", subTabs().join(","));
  await call("web.place", { id: "youtube", place: made.page });
  shell.navigate(made.page);
  await settle(300);
  check("a tab of your own shows its services as tabs and cards", tabsIn(document.querySelector(".page")).join() === "youtube" && document.querySelectorAll(".webcard").length === 1 && document.querySelector(".subnav .on")?.textContent === "watch",
    `${tabsIn(document.querySelector(".page")).join()} / ${document.querySelectorAll(".webcard").length} cards`);
  await shot("29-custom-tab");

  // a service's page, with a stand-in web player: the bar works it
  const url = await call<string>("smoke.webPage", { dir: host.smoke });
  const page = shell.get<WebServicePage>("web:youtube");
  const loading = page.loadTestPage(url);   // it loads once it is in the window
  shell.navigate("web:youtube");
  await loading;
  await settle(500);
  check("a service's page lights the tab it is placed in and offers the way back", document.querySelector(".subnav .on")?.textContent === "watch" && text(".webbar").startsWith("‹  watch") && tabsIn(document.querySelector(".webbar")).join() === "youtube",
    `${document.querySelector(".subnav .on")?.textContent} / ${text(".webbar")}`);
  check("the page loads in its webview with the hook in place", page.ready && (await page.view.executeJavaScript("typeof window.__phx")) === "object");
  await page.command("volume", 0);
  await page.command("toggle");
  await until(() => remote.active === page && !!page.state?.playing, 6000);
  await settle(400);
  check("the page playing takes the player bar", remote.active === page && text(".transport .title") === "Web Song" && text(".transport .artist") === "Web Artist  ·  YouTube", `${text(".transport .title")} / ${text(".transport .artist")}`);
  await shot("30-web-player");
  (document.querySelector<HTMLElement>(".transport .iconbtn.filled[title^='Play']")!).click();
  await until(() => page.state?.playing === false, 4000);
  check("the bar's pause pauses the page", page.state?.playing === false);
  shell.navigate("music");
  await settle(300);
  check("leaving the page keeps it (the page is moved out of sight, not closed)", page.el.isConnected && page.el.classList.contains("offstage"));
  await page.command("toggle");
  await until(() => !!page.state?.playing, 4000);
  const songs = shell.get<MusicPage>("music").state().songs;
  await call("play.tracks", { ids: songs.map(s => s.id), start: songs[0].id });
  await until(() => page.state?.playing === false, 5000);
  check("Phoenix playing pauses the page and takes the bar back", page.state?.playing === false && remote.active === null && text(".transport .title") !== "Web Song", text(".transport .title"));
  await call("player.stop");

  // settings, and taking it all away again
  shell.navigate("settings");
  const settings = shell.get<SettingsPage>("settings");
  settings.showCategory("servers", "Streaming");
  await settle(400);
  const rows = [...document.querySelectorAll<HTMLElement>(".webrow")].filter(visible);
  const yt = rows.find(r => r.dataset.service === "youtube");
  check("settings: streaming lists the services with where each is shown", rows.length >= 11 && !!yt && yt.querySelector("select")!.value === made.page, `${rows.length} rows, youtube in ${yt?.querySelector("select")?.value}`);
  await shot("31-settings-streaming");
  await call("tabs.removeCustom", { id: made.id });
  await until(() => !subTabs().includes("watch"), 3000);
  const back = (await call<{ services: { id: string; place: string }[] }>("web.state")).services.find(s => s.id === "youtube");
  check("removing a tab of your own puts its services back", !subTabs().includes("watch") && back?.place === "videos", back?.place);
  await call("web.set", { id: "youtube", on: false });
  await call("web.set", { id: "ytmusic", on: false });
  await call("web.place", { id: "ytmusic", place: "tab" });
  settings.showCategory("software");
}
