// Developer self-test: `Phoenix --smoke <dir>` (with PHOENIX_DATA pointing at a throwaway folder) builds a sample
// library, walks the screens, saves screenshots and checks a few behaviours. The verdict goes to <dir>/smoke.log and
// the exit code. Mirrors the Avalonia build's self-test.
import { call, host, PlayerState } from "./api";
import { ComicReader, EpubReader, openBookmark } from "./books";
import type { MusicPage } from "./music";
import type { PicturesPage } from "./pictures";
import type { SearchPage } from "./playlists";
import type { SettingsPage } from "./settings";
import { shell } from "./shell";
import { artUrl, sleep } from "./ui";
import type { VideosPage } from "./videos";
import { Viz } from "./viz";
import { runExtras } from "./smokeMore";

let log = "";
let fails = 0;

function check(name: string, ok: boolean, detail = "") {
  if (!ok) fails++;
  log += `${ok ? "PASS" : "FAIL"}  ${name}  ${detail}\n`;
  void host.smokeLog(`${ok ? "PASS" : "FAIL"}  ${name}`);
}

async function settle(ms = 350) {
  await sleep(ms);
  await new Promise<void>(r => requestAnimationFrame(() => requestAnimationFrame(() => r())));
}

async function shot(name: string) {
  void host.smokeLog("shot " + name);
  await settle();
  await host.smokeShot(name);
}

async function until(test: () => boolean | Promise<boolean>, ms: number): Promise<boolean> {
  const end = Date.now() + ms;
  while (Date.now() < end) { if (await test()) return true; await sleep(100); }
  return false;
}

const text = (sel: string) => document.querySelector(sel)?.textContent ?? "";

function loadImage(url: string): Promise<HTMLImageElement | null> {
  return new Promise(resolve => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => resolve(null);
    img.src = url;
  });
}

/** How many pixels of a canvas have been drawn on. */
function inked(canvas: HTMLCanvasElement): number {
  const g = canvas.getContext("2d")!;
  const px = g.getImageData(0, 0, canvas.width, canvas.height).data;
  let n = 0;
  for (let i = 3; i < px.length; i += 4) if (px[i] > 8) n++;
  return n;
}

interface SmokeBook { id: number; format: string; title: string; author: string; artExists: boolean; progress: number; finished: boolean; part: number; marks: string[] }

async function run() {
  // the built-in typeface: each weight must come from Selawik, not a system fallback
  await document.fonts.ready;
  for (const [label, weight] of [["regular", 400], ["light", 300], ["semibold", 600], ["bold", 700]] as const) {
    await document.fonts.load(`${weight} 13px Selawik`);
    const face = [...document.fonts].find(f => f.family.replace(/"/g, "") === "Selawik" && Number(f.weight) === weight);
    check(`Selawik ${label} is built in`, !!face && face.status === "loaded", face ? `${face.family} ${face.weight} ${face.status}` : "not found");
  }
  check("the page's type is Selawik", getComputedStyle(document.body).fontFamily.includes("Selawik"), getComputedStyle(document.body).fontFamily);
  await settle(600);
  check("app starts with an empty library", shell.init.counts.tracks === 0, `${shell.init.counts.tracks} songs`);
  await shot("01-empty");

  const made = await call<{ root: string; counts: any; sep: string }>("smoke.media", { dir: host.smoke });
  const sep = made.sep;
  await settle();
  let books = await call<SmokeBook[]>("smoke.books");
  const comic = books.find(b => b.format === "comic"), ebook = books.find(b => b.format === "epub");
  check("books scanned", books.length === 2, `${books.length} books`);
  check("comic metadata from ComicInfo.xml", comic?.title === "Hero Tales #3" && comic.author === "Pat Pencil" && comic.artExists, comic?.title ?? "missing");
  check("epub metadata", ebook?.title === "A Quiet Book" && ebook.author === "Sam Author", ebook?.title ?? "missing");
  check("music scanned", made.counts.tracks === 12 && made.counts.albums === 3, `${made.counts.tracks} songs, ${made.counts.albums} albums`);

  // ---- video thumbnail ----
  const vids = await call<{ id: number; art: string; title: string }[]>("smoke.videos");
  const vid = vids[0];
  check("video scanned with a thumbnail marker", !!vid && vid.art.startsWith("thumb:"), vid?.art ?? "none");
  if (vid) {
    const started = Date.now();
    const thumb = await loadImage(artUrl(vid.art, 200));
    const full = await loadImage(artUrl(vid.art, 0));
    const saved = (await call<{ thumbnails: string[] }>("smoke.state")).thumbnails;
    check("video thumbnail is made from the video", !!thumb && thumb.naturalWidth > 100 && saved.length === 1,
      `${thumb ? `${thumb.naturalWidth}x${thumb.naturalHeight}` : "none"}, ${saved.length} saved, ${Date.now() - started} ms`);
    check("video thumbnail has the picture's shape", !!full && Math.abs(full.naturalWidth / full.naturalHeight - 320 / 180) < 0.05, full ? `${full.naturalWidth}x${full.naturalHeight}` : "none");
  }

  // ---- pictures: one click opens the viewer; the arrows move on even past a picture that can't be read ----
  shell.navigate("pictures");
  const pics = shell.get<PicturesPage>("pictures");
  await pics.reload();
  await settle(500);
  await shot("10-pictures");
  const picTiles = [...document.querySelectorAll<HTMLElement>(".tile.picture")];
  picTiles[1]?.querySelector("img")?.click();
  await settle(400);
  check("one click on a picture opens it in the viewer", pics.viewerOpen() && pics.caption().startsWith("photo 2.png"), picTiles.length < 2 ? "no tile found" : pics.caption());
  await shot("10-picture-clicked");
  pics.close();
  pics.openAt(0);
  await until(() => pics.showing(), 3000);
  check("picture viewer opens on the first picture", pics.showing() && pics.caption().startsWith("photo 1.png") && pics.caption().includes("1 of 4"), pics.caption());
  // clicking the arrow twice quickly is a double-click on it: the viewer must stay open
  const arrows = document.querySelectorAll<HTMLElement>(".viewnav");
  arrows[1]?.dispatchEvent(new MouseEvent("dblclick", { bubbles: true }));
  await settle(200);
  check("a quick double click on the arrow doesn't close the viewer", pics.viewerOpen(), pics.caption());
  pics.close();
  pics.openAt(0);
  await until(() => pics.showing(), 3000);
  pics.next();
  await until(() => pics.showing(), 3000);
  check("next arrow moves to the next picture", pics.showing() && pics.caption().startsWith("photo 2.png"), pics.caption());
  pics.next();
  await until(() => pics.showing(), 3000);
  pics.next();
  await until(() => pics.caption().includes("can't be shown"), 3000);
  check("an unreadable picture says so instead of keeping the last one up", !pics.showing() && pics.caption().startsWith("photo 4.png") && pics.caption().includes("can't be shown"), pics.caption());
  pics.next();
  await until(() => pics.showing(), 3000);
  check("next arrow carries on past it", pics.showing() && pics.caption().startsWith("photo 1.png"), pics.caption());
  await shot("10-picture-viewer");
  pics.close();

  // ---- videos ----
  shell.navigate("videos");
  const videos = shell.get<VideosPage>("videos");
  videos.showTab("all");
  await sleep(200);
  await videos.ready();
  check("videos page lists the clip", videos.items().length === 1 && videos.items()[0].title === "Sample Clip", videos.items().map(v => v.title).join(","));
  await shot("09-videos");

  // play it: VLC's frames must arrive in the window's own canvas (no native video window)
  if (vid) {
    await call("player.volume", { value: 0 });
    shell.video.resetStats();
    await call("play.videos", { ids: [vid.id] });
    const started = Date.now();
    await until(async () => shell.video.frames >= 10 && (await call<PlayerState>("player.state")).pos >= 600, 8000);
    const pos = (await call<PlayerState>("player.state")).pos;
    const c = shell.video.centre();
    check("video frames are drawn in the window", shell.video.frames >= 10 && shell.video.width === 320 && shell.video.height === 180,
      `${shell.video.frames} frames, ${shell.video.width}x${shell.video.height}, ${Date.now() - started} ms`);
    check("the picture has the clip's colours", c[1] > 90 && c[0] > 90 && c[2] < 110, `centre rgb ${c.join(",")}`);
    check("video plays with the clock running", pos >= 600, `${pos} ms`);
    check("playing a video opens now playing on the picture", shell.nowOpen() && !document.querySelector(".now .video")!.classList.contains("hidden"));
    await shot("09-video-playing");
    await call("player.stop");
    shell.showNow(false);
  }

  // optional measurement (PHOENIX_BENCH=1): how many frames of an uncompressed 1080p clip reach the screen
  if (shell.init.bench) {
    for (const [w, h, fps] of [[1920, 1080, 30], [1920, 1080, 60]] as const) {
      await call("smoke.bigVideo", { dir: host.smoke, width: w, height: h, frames: fps * 4, fps });
      await until(() => shell.video.width === w, 15000);
      const s0 = await call<any>("smoke.state");
      shell.video.resetStats();
      const t0 = performance.now();
      await sleep(3000);
      const s1 = await call<any>("smoke.state");
      const secs = (performance.now() - t0) / 1000, drawn = shell.video.frames;
      log += `INFO  ${w}x${h} at ${fps} fps: VLC presented ${((s1.videoFrames - s0.videoFrames) / secs).toFixed(1)} fps, sent ${((s1.framesSent - s0.framesSent) / secs).toFixed(1)} fps, drawn ${(drawn / secs).toFixed(1)} fps; copy+send ${s1.sendMs.toFixed(2)} ms/frame, upload+draw ${(shell.video.drawMs / Math.max(1, drawn)).toFixed(2)} ms/frame
`;
      if (fps === 30) check("1080p frames reach the screen at film speed or better", drawn / secs >= 24, `${(drawn / secs).toFixed(1)} fps`);
      await shot(`16-bench-${h}p${fps}`);
      await call("player.stop");
      await sleep(300);
    }
    shell.showNow(false);
  }

  // ---- music ----
  shell.navigate("music");
  const music = shell.get<MusicPage>("music");
  await sleep(300);
  await music.ready();
  await settle();
  let m = music.state();
  check("music page lists artists, albums and songs", m.left.length === 3 && m.albums === 3 && m.songs.length === 12, `${m.left.length} artists, ${m.albums} albums, ${m.songs.length} songs`);
  const covers = [...document.querySelectorAll<HTMLImageElement>(".tilegrid .tile img")];
  await until(() => covers.every(i => i.complete), 5000);
  check("album covers are drawn", covers.length === 3 && covers.every(i => i.naturalWidth > 0), covers.map(i => i.naturalWidth).join(","));
  await shot("05-music");
  music.setMode("songs");
  await sleep(200);
  await music.ready();
  m = music.state();
  check("songs view lists every song by title", m.songs.length === 12 && m.songs[0].title === "Song 1" && m.songs[11].title === "Song 4", m.songs.map(s => s.title).join(","));
  await settle();
  const fullRows = [...document.querySelectorAll<HTMLElement>(".songrow.full")];
  check("songs view draws full-width rows with their headings", fullRows.length === 12 && fullRows[0].offsetWidth > 800 && (document.querySelector<HTMLElement>(".songhead")?.offsetWidth ?? 0) > 800,
    `${fullRows.length} rows, ${fullRows[0]?.offsetWidth ?? 0} px wide`);
  await shot("05-music-songs");
  music.setMode("artists");
  await sleep(200);
  await music.ready();

  // ---- playing a song: VLC's clock must run and the bar must follow ----
  await call("player.volume", { value: 0 });
  const songs = music.state().songs;
  await call("play.tracks", { ids: songs.map(s => s.id), start: songs[0].id });
  const playing = await until(async () => (await call<PlayerState>("player.state")).pos >= 600, 10000);
  let st = await call<PlayerState>("player.state");
  check("a song plays with the clock running", playing && st.playing, `${st.pos} ms`);
  await settle(300);
  check("the bar shows what is playing", text(".transport .title") === "Song 1" && text(".transport .artist") === "Quiet Trio", `${text(".transport .title")} / ${text(".transport .artist")}`);
  check("the bar's clock follows", /^0:0[0-9]$/.test(text(".transport .seekrow .t")) && text(".transport .seekrow .t.r") === "0:04", `${text(".transport .seekrow .t")} of ${text(".transport .seekrow .t.r")}`);
  await call("player.next");
  await until(async () => (await call<PlayerState>("player.state")).current?.title === "Song 2", 4000);
  await settle(300);
  check("next moves to the next song", text(".transport .title") === "Song 2", text(".transport .title"));

  // ---- visualizer: every style draws from the playing song's own spectrum ----
  await call("settings.set", { visualizer: 1, visualizerColor: 0 }).then(s => (shell.settings = s));
  await call("player.repeat");   // repeat all, so the short songs keep coming while the styles are tried
  shell.showNow(true);
  const vizCanvas = document.querySelector<HTMLCanvasElement>("canvas.viz")!;
  const live = await until(() => !vizCanvas.classList.contains("hidden") && vizCanvas.width > 100 && inked(vizCanvas) > 500, 8000);
  check("the visualizer draws the playing song", live, `${vizCanvas.width}x${vizCanvas.height}, ${vizCanvas.width > 0 ? inked(vizCanvas) : 0} pixels`);
  await shot("07-viz-live-bars");
  document.dispatchEvent(new KeyboardEvent("keydown", { key: "v", bubbles: true }));
  await until(() => shell.settings.visualizer === 2, 2000);
  check("V moves to the next visualizer style", shell.settings.visualizer === 2, shell.init.vizStyles[shell.settings.visualizer]);
  await shot("07-viz-live-mirrored");
  await call("settings.set", { visualizer: 0 }).then(s => (shell.settings = s));
  shell.showNow(false);
  shell.showNow(true);
  await settle(300);
  check("style 0 shows the album art wall instead", vizCanvas.classList.contains("hidden") && !document.querySelector(".now .mosaic")!.classList.contains("hidden") && document.querySelectorAll(".now .mosaic img").length === 40);
  await call("player.toggle");
  st = await call<PlayerState>("player.state");
  const at = st.pos;
  await sleep(700);
  st = await call<PlayerState>("player.state");
  check("pause holds the song", !st.playing && Math.abs(st.pos - at) < 250, `${at} → ${st.pos} ms`);
  await call("rate.cycle", { id: st.current!.trackId });
  await settle(300);
  check("liking the song fills the heart", !!document.querySelector(".transport .iconbtn.heartfull"));
  await shot("06-now-playing");
  shell.showNow(false);
  await call("player.stop");
  await call("player.repeat"); await call("player.repeat");   // back to no repeat
  await settle(200);
  check("stop clears the bar", text(".transport .title") === "nothing playing", text(".transport .title"));

  // every style and colour scheme, drawn from a made-up signal: nothing may throw, and each must put something on the canvas
  {
    const canvas = document.createElement("canvas");
    canvas.width = 900; canvas.height = 520;
    const viz = new Viz(canvas);
    let clock = 0;
    viz.now = () => clock;
    const failed: string[] = [];
    for (let style = 1; style < shell.init.vizStyles.length; style++) {
      viz.mode = style;
      viz.palette = (style - 1) % shell.init.vizPalettes.length;
      try {
        for (let frame = 0; frame < 40; frame++) {
          clock += 33;
          const kick = frame % 8 < 2 ? 0.9 : 0.1;   // a kick drum every few frames over a chord
          const bars = Array.from({ length: 40 }, (_, b) => (b < 6 ? kick : b === 14 || b === 15 ? 0.6 : b === 28 ? 0.45 : 0.05 + 0.1 * Math.abs(Math.sin(b + frame / 5))));
          const scope = new Float32Array(1024);
          for (let i = 0; i < scope.length; i++) scope[i] = kick * 0.5 * Math.sin((2 * Math.PI * 60 * (frame * 1024 + i)) / 44100) + 0.25 * Math.sin((2 * Math.PI * 440 * (frame * 1024 + i)) / 44100);
          viz.feed({ bars, peaks: bars.map(b => Math.min(1, b + 0.08)), scope });
        }
        if (inked(canvas) < 200) failed.push(`${shell.init.vizStyles[style]}: nothing drawn`);
      } catch (e: any) { failed.push(`${shell.init.vizStyles[style]}: ${e?.message ?? e}`); }
    }
    check("every visualizer style draws", failed.length === 0, failed.join("; "));
  }

  // ---- books, settings, quickplay ----
  shell.navigate("books");
  await shot("02-books");
  const longPath = [made.root, "A very long folder name that goes on and on", "and a second level that is also long", "and a third so the path cannot fit in the box", "My Music"].join(sep);
  shell.settings = await call("settings.addFolders", { kind: "music", paths: [longPath] });
  shell.navigate("settings");
  const settings = shell.get<SettingsPage>("settings");
  settings.showCategory("software");
  await settings.refreshFolders();
  await settle(400);
  check("settings open on Software, Collection", settings.shown_() === "Collection", settings.shown_());
  await shot("03-settings");
  const rows = [...document.querySelectorAll<HTMLElement>(".pathrow")];
  const longRow = rows.find(r => r.dataset.path === longPath);
  const shown = longRow?.textContent ?? "";
  check("a long folder path is shortened to fit its box, keeping its start and its last folder",
    !!longRow && shown !== longPath && shown.includes(sep + "..." + sep) && shown.endsWith(sep + "My Music") && longPath.startsWith(shown.slice(0, shown.indexOf("...")))
      && longRow.scrollWidth <= longRow.clientWidth + 1 && longRow.clientWidth < 620,
    longRow ? `${shown}  (${longRow.scrollWidth} of ${longRow.clientWidth} px)` : "not found");
  const shortRow = rows.find(r => r.dataset.path !== longPath && (r.dataset.path ?? "").length > 0 && r.clientWidth > 0 && r.scrollWidth <= r.clientWidth && r.textContent === r.dataset.path);
  const anyShort = rows.some(r => r.dataset.path !== longPath && r.clientWidth > 0);
  if (anyShort) check("a folder path that fits is shown whole", !!shortRow || rows.filter(r => r.dataset.path !== longPath).every(r => (r.textContent ?? "").includes("...")), shortRow?.textContent ?? "all paths here are longer than the box");
  shell.settings = await call("settings.removeFolder", { kind: "music", path: longPath });
  settings.showCategory("servers", "Plex");
  await settle(200);
  check("settings: Servers shows Plex", settings.shown_() === "Plex" && !!document.querySelector(".pill") && [...document.querySelectorAll(".h2")].some(e => e.textContent === "plex" && (e as HTMLElement).offsetParent !== null), settings.shown_());
  await shot("03-settings-plex");
  settings.showCategory("software");
  await settings.refreshFolders();
  shell.navigate("quickplay");
  await settle(500);
  const likedTiles = document.querySelectorAll(".tile.small").length;
  check("quickplay shows new albums, history and the liked song", likedTiles >= 5, `${likedTiles} tiles`);
  await shot("04-quickplay");
  shell.applyTheme("dark");
  await shot("08-dark-quickplay");
  shell.navigate("music");
  await shot("08-dark-music");
  shell.applyTheme("light");

  // ---- playlists and search ----
  const listId = await call<number>("playlist.create", { name: "Test list", ids: songs.slice(0, 3).map(s => s.id) });
  shell.navigate("playlists");
  await settle(500);
  check("a new playlist appears with its songs", [...document.querySelectorAll(".row.name")].some(r => r.textContent === "Test list") && listId > 0, `${document.querySelectorAll(".row.name").length} lists`);
  await shot("11-playlists");
  shell.navigate("search");
  shell.get<SearchPage>("search").search("later");
  await settle(500);
  check("search finds an album and its songs", document.querySelectorAll(".tile.square").length === 1 && document.querySelectorAll(".songrow.full").length === 4, `${document.querySelectorAll(".tile.square").length} albums, ${document.querySelectorAll(".songrow.full").length} songs`);
  await shot("12-search");
  shell.navigate("podcasts");
  await shot("13-podcasts");

  // ---- phone sync: the page, the switch, and the phone's side of the protocol over the loopback interface ----
  {
    shell.navigate("phone");
    await settle(400);
    check("phone page says sync is off to begin with", text("#root").includes("PHONE SYNC IS OFF"));
    const three = songs.slice(4, 7).map(x => x.id);
    const added = await call<{ added: number; skipped: number }>("phone.add", { ids: three });
    const again = await call<{ added: number; skipped: number }>("phone.add", { ids: three });
    check("songs are added to the phone sync list once", added.added === 3 && again.added === 0, `${added.added} then ${again.added}`);
    const on = await call<any>("phone.enable", { on: true });
    check("turning phone sync on starts the server with a six-digit code", on.running && /^\d{6}$/.test(on.code), `running ${on.running}, port ${on.port}, ${on.error}`);
    shell.navigate("music");
    shell.navigate("phone");
    await settle(500);
    check("phone page shows it is ready, the code and the list", text("#root").includes("READY TO SYNC OVER WI-FI") && text("#root").includes(on.code) && text("#root").includes("PHONE SYNC LIST  3") && document.querySelectorAll(".songrow.full").length === 3,
      `${document.querySelectorAll(".songrow.full").length} rows`);
    await shot("17-phone");
    const r = await call<any>("smoke.sync");
    const info = JSON.parse(r.info);
    check("sync: /sync/info answers without a code as app Halcyon, version 1", r.infoStatus === 200 && info.app === "Halcyon" && info.version === 1 && info.name === r.machine, r.info);
    check("sync: the library is refused without the code and with a wrong one", r.noCodeStatus === 401 && r.wrongCodeStatus === 401 && r.wrongCodeBody === '{"error":"wrong pairing code"}', `${r.noCodeStatus}, ${r.wrongCodeStatus} ${r.wrongCodeBody}`);
    const first = JSON.parse(r.firstTrack);
    const keys = "id,title,artist,albumArtist,album,genre,year,trackNo,discNo,durationMs,size,modified,path,hasArt,rating,ratedAt,playCount,lastPlayed";
    check("sync: the library lists every local song with the fields the phone app reads", r.libraryStatus === 200 && r.tracks === 12 && r.libraryName === r.machine && Object.keys(first).join(",") === keys, `${r.tracks} songs; ${Object.keys(first).join(",")}`);
    check("sync: a song says where the phone should put it and that it has a cover", /^[^/]+\/[^/]+\/\d\d Song \d\.wav$/.test(first.path) && first.hasArt === true && first.durationMs > 3000, `${first.path}, hasArt ${first.hasArt}`);
    const lists = JSON.parse(r.playlists) as { name: string; trackIds: number[] }[];
    const phoneList = lists.find(l => l.name === "Phone sync");
    check("sync: playlists come with it, the phone sync list among them", !!phoneList && phoneList.trackIds.join(",") === three.join(",") && lists.some(l => l.name === "Test list"), lists.map(l => `${l.name} (${l.trackIds.length})`).join(", "));
    check("sync: a song file downloads whole", r.fileStatus === 200 && r.fileMatches && r.fileSize === r.listedSize, `${r.fileSize} of ${r.listedSize} bytes`);
    check("sync: its cover downloads", r.artStatus === 200 && r.artIsPng, `status ${r.artStatus}`);
    check("sync: an unknown song is not found", r.missingStatus === 404, `status ${r.missingStatus}`);
    check("sync: a newer like from the phone is taken, an older one is not", r.appliedNewer === 1 && r.ratingAfter === 1 && r.playCountAfter >= 2 && r.appliedOlder === 0 && r.ratingAfterOlder === 1,
      `applied ${r.appliedNewer} then ${r.appliedOlder}; rating ${r.ratingBefore} → ${r.ratingAfter} → ${r.ratingAfterOlder}`);
    check("sync: discovery answers HALCYON_HERE with the name and port", r.discovery === `HALCYON_HERE|${r.machine}|${r.port}`, r.discovery);
    {
      // the phone as a remote: the page, then what is playing, and a button press that works the player
      await call("player.volume", { value: 0 });
      await call("play.tracks", { ids: [songs[0].id] });
      await sleep(600);
      const rc = await call<any>("smoke.remote");
      const st = JSON.parse(rc.state), after = JSON.parse(rc.after);
      check("remote: the page is served, and asks for the code before telling anything", rc.pageStatus === 200 && rc.isPage && rc.refused === 401, `${rc.pageStatus} ${rc.refused}`);
      check("remote: it shows what is playing, and its play button works the player", st.title === songs[0].title && st.playing === true && rc.pressStatus === 200 && after.playing === false, `${st.title} ${st.playing} → ${after.playing}`);
      await call("player.stop");
      // pairing: asked and refused, asked and allowed (its own key then works), the QR code's token, forgetting
      const pr = await call<any>("smoke.pair");
      check("pairing: a phone asks; no refuses it, yes gives it its own key, which works in place of the code", pr.denied === 403 && pr.allowed === 200 && pr.keyLength >= 20 && pr.withKey === 200 && pr.listedName === "Test phone" && pr.lastSummary.includes("3 songs"), JSON.stringify(pr));
      check("pairing: the QR code's page opens Phoenix, its token pairs at once (a wrong one doesn't), and a forgotten phone is shut out", pr.badToken === 403 && pr.qr === 200 && pr.pagePairs && pr.infoHasId && pr.afterForget === 401 && pr.qrPng > 200, JSON.stringify(pr));
    }
    shell.navigate("settings");
    settings.showCategory("phone");
    await settle(400);
    check("settings: Phone shows sync options with the pairing code", settings.shown_() === "Sync options" && text(".statusline").includes(on.code) || [...document.querySelectorAll(".statusline")].some(e => (e.textContent ?? "").includes(on.code)), settings.shown_());
    check("settings: Phone shows the QR code for pairing", !!document.querySelector("img.pairqr") && (document.querySelector("img.pairqr") as HTMLImageElement).src.startsWith("data:image/png"));
    await shot("17-settings-phone");
    const off = await call<any>("phone.enable", { on: false });
    check("turning phone sync off stops the server", off.enabled === false && off.running === false);
    settings.showCategory("software");
  }

  // ---- readers ----
  if (comic) {
    const reader = await ComicReader.open(comic.id);
    await settle(300);
    reader!.show(1);
    await settle(300);
    books = await call<SmokeBook[]>("smoke.books");
    let c = books.find(b => b.id === comic.id)!;
    check("comic reader opens and turns pages", reader!.pages.length === 3 && reader!.page === 1 && Math.abs(c.progress - 2 / 3) < 0.01, `${reader!.pages.length} pages, on ${reader!.page + 1}`);
    const pageImg = document.querySelector<HTMLImageElement>(".reader img")!;
    await until(() => pageImg.complete && pageImg.naturalWidth > 0, 3000);
    check("the comic page is drawn", pageImg.naturalWidth === 64 && pageImg.naturalHeight === 96, `${pageImg.naturalWidth}x${pageImg.naturalHeight}`);
    await shot("14-comic-reader");
    reader!.show(99);
    await settle(200);
    c = (await call<SmokeBook[]>("smoke.books")).find(b => b.id === comic.id)!;
    check("comic reader ends on the last page", reader!.page === 2 && c.finished);
    reader!.close();
  }
  if (ebook) {
    const reader = await EpubReader.open(ebook.id);
    await settle();
    const t = reader!.chapterText;
    check("epub reader shows the chapter text", t.includes("Opening") && t.includes("Paragraph 7") && !t.includes("<p>") && text(".reader").includes("Paragraph 7"), t.length + " characters");
    await shot("15-epub-reader");
    reader!.close();
    check("epub text extraction", (await call<string>("smoke.htmlText", { html: "<p>One &amp; two</p><p>Three<br/>four</p>" })) === "One & two\n\nThree\n\nfour");
  }

  // ---- bookmarks ----
  if (comic && ebook) {
    const m1 = await call<{ id: number }>("bookmarks.add", { book: comic.id, part: 2, position: 0, label: "" });
    const m2 = await call<{ id: number }>("bookmarks.add", { book: comic.id, part: 0, position: 0, label: "  Cover  " });
    let c = (await call<SmokeBook[]>("smoke.books")).find(b => b.id === comic.id)!;
    check("bookmarks listed in reading order with default or custom labels", c.marks.join(" | ") === "Cover | Page 3", c.marks.join(" | "));
    const reader = await ComicReader.open(comic.id);
    reader!.show(0);
    await openBookmark(comic.id, m1.id);
    await settle(200);
    check("jumping to a comic bookmark turns the open reader to that page", ComicReader.current?.page === 2, `on page ${(ComicReader.current?.page ?? -1) + 1}`);
    ComicReader.current?.close();
    const em = await call<{ id: number }>("bookmarks.add", { book: ebook.id, part: 1, position: 0.5, label: "" });
    await EpubReader.open(ebook.id);
    await settle();
    await openBookmark(ebook.id, em.id);
    await settle();
    check("jumping to an ebook bookmark turns the open reader to that chapter", EpubReader.current?.chapter === 1 && (EpubReader.current?.chapterText ?? "").includes("The End"), `chapter ${(EpubReader.current?.chapter ?? -1) + 1}`);
    EpubReader.current?.close();
    await call("bookmarks.delete", { book: comic.id, mark: m2.id });
    c = (await call<SmokeBook[]>("smoke.books")).find(b => b.id === comic.id)!;
    check("deleting a bookmark removes it", c.marks.length === 1, c.marks.join(" | "));
    shell.navigate("books");
    await shot("06-books-after");
  }

  // ---- what was brought over from the Windows app ----
  await runExtras(check, shot, settle, until, songs);
}

export async function runSmoke() {
  try { await run(); }
  catch (e: any) { fails++; log += `EXCEPTION ${e?.stack ?? e}\n`; }
  log += fails === 0 ? "ALL PASSED\n" : `${fails} FAILED\n`;
  await host.smokeDone(log, fails);
}
