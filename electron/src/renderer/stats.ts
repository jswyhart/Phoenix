// "listening": hours, plays and the top artists, albums and songs for this year, last year, the last 30 days or all
// time, with listening over the period as bars. As on Windows (quickplay → listening stats).
import { call } from "./api";
import { Page, shell } from "./shell";
import { art, h } from "./ui";

interface Top { name: string; detail: string; time: string; ms: number; plays: number; art: string; play: { type: string; key: string } | null }
interface Stats {
  title: string; empty: boolean; hours: string; ms: number; plays: number; artistCount: number; albumCount: number; songCount: number;
  over: { label: string; ms: number; text: string }[]; artists: Top[]; albums: Top[]; songs: Top[]; note: string;
}

export interface StatsPage extends Page { show(period: string): Promise<void>; shown_(): Stats | null }

export function statsPage(): StatsPage {
  let period = "", periods: string[] = [], last: Stats | null = null;
  const periodRow = h("div", { style: { display: "flex", margin: "4px 0 0 6px" } });
  const body = h("div", { style: { margin: "14px 28px 30px 6px" } });
  const el = h("div.page", { style: { paddingLeft: "22px" } },
    h("div.zunehead", "listening"), periodRow,
    h("div", { style: { overflowY: "auto", minHeight: "0", flex: "1 1 0" } }, body));

  const number = (value: string, label: string) =>
    h("div", { style: { marginRight: "44px" } }, h("div", { style: { fontSize: "46px", fontWeight: "300", lineHeight: "1.1" } }, value), h("div.dim", { style: { fontSize: "12px" } }, label));

  function list(title: string, rows: Top[], byPlays: boolean) {
    const most = Math.max(1, ...rows.map(r => (byPlays ? r.plays : r.ms)));
    return h("div", { style: { marginRight: "26px", minWidth: "0" } }, h("div.cap", { style: { margin: "0 0 8px 0" } }, title),
      ...rows.map((r, i) => {
        const detail = [r.detail, r.time, r.plays > 0 ? `${r.plays} play${r.plays === 1 ? "" : "s"}` : ""].filter(x => x !== "").join("  ·  ");
        const line = h("div.bare.statrow", { title: r.play ? "Play" : "", style: { display: "flex", alignItems: "center", height: "54px", cursor: r.play ? "pointer" : "default" } },
          h("div.faint", { style: { width: "22px", flex: "none" } }, String(i + 1)),
          h("div.thumb", { style: { width: "44px", height: "44px" } }, art(r.art, 100)),
          h("div", { style: { marginLeft: "10px", minWidth: "0", flex: "1 1 0" } },
            h("div.ell", { style: { fontSize: "14px" } }, r.name), h("div.dim.ell", { style: { fontSize: "11.5px" } }, detail),
            h("div", { style: { height: "2px", marginTop: "4px", width: Math.max(3, (180 * (byPlays ? r.plays : r.ms)) / most) + "px", background: "var(--grad)" } })));
        if (r.play) line.onclick = () => void play(r.play!);
        return line;
      }));
  }

  async function play(p: { type: string; key: string }) {
    if (p.type === "artist") {
      const ids = await call<number[]>("artist.tracks", { artist: p.key });
      await call("play.tracks", { ids, shuffle: true });
    } else if (p.type === "album") await call("quick.play", { type: "album", key: p.key });
    else await call("quick.play", { type: "track", key: p.key });
  }

  async function show(p: string) {
    if (periods.length === 0) periods = await call<string[]>("stats.periods");
    period = periods.includes(p) ? p : periods[0];
    periodRow.replaceChildren(...periods.map(x => {
      const b = h("button.textbtn.big", { onclick: () => void show(x), style: { opacity: x === period ? "1" : "0.5", marginRight: "12px", padding: "4px 0" } }, x);
      return b;
    }));
    const s = last = await call<Stats>("stats.view", { period });
    body.replaceChildren();
    if (s.empty) {
      body.append(h("div.dim", { style: { fontSize: "20px", margin: "10px 0 6px 0" } }, `Nothing listened to in ${s.title} yet.`));
      if (s.note) body.append(h("div.faint", s.note));
      return;
    }
    const hours = s.ms / 3_600_000;
    body.append(h("div", { style: { display: "flex", marginBottom: "18px" } },
      number(hours >= 1 ? (Math.round(hours * 10) / 10).toLocaleString() : String(Math.floor(s.ms / 60_000)), hours >= 1 ? "hours listened" : "minutes listened"),
      number(s.plays.toLocaleString(), s.plays === 1 ? "play" : "plays"),
      number(s.artistCount.toLocaleString(), s.artistCount === 1 ? "artist" : "artists"),
      number(s.albumCount.toLocaleString(), s.albumCount === 1 ? "album" : "albums"),
      number(s.songCount.toLocaleString(), s.songCount === 1 ? "song" : "songs")));
    // listening over the period, as bars
    if (s.over.length > 1 && s.over.some(o => o.ms > 0)) {
      const max = Math.max(1, ...s.over.map(o => o.ms));
      const width = s.over.length > 14 ? 16 : 40;
      body.append(h("div.statchart", { style: { display: "flex", height: "130px", marginBottom: "22px", alignItems: "flex-end" } },
        ...s.over.map(o => h("div", { style: { width: width + "px", display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "flex-end", height: "100%" } },
          h("div", { title: o.text, style: { width: width - 4 + "px", height: Math.max(o.ms > 0 ? 2 : 0, (96 * o.ms) / max) + "px", background: "var(--accent)" } }),
          h("div.faint", { style: { fontSize: "10px", marginTop: "2px" } }, o.label)))));
    }
    body.append(h("div", { style: { display: "grid", gridTemplateColumns: "1fr 1fr 1fr" } },
      list("TOP ARTISTS", s.artists, false), list("TOP ALBUMS", s.albums, false), list("TOP SONGS", s.songs, true)));
    if (s.note) body.append(h("div.faint", { style: { marginTop: "22px", fontSize: "12px" } }, s.note));
  }

  void show("");
  return { el, shown: () => { void show(period); shell.setStatus(""); }, show, shown_: () => last };
}
