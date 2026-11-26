// "Play on another device", as on Windows: looks on the network for DLNA speakers and TVs and Chromecasts, and sends
// what is playing there.
import { call, on } from "./api";
import { shell } from "./shell";
import { dialogButtons, h, openDialog, VList } from "./ui";

interface Device { index: number; name: string; kind: string; detail: string }

export async function chooseCastDevice() {
  const d = openDialog(480, 420);
  const found: Device[] = [];
  const status = h("div.note", "looking on your network…");
  const list = new VList<Device>({
    rowHeight: 50,
    render: x => h("div", { style: { flexDirection: "column", alignItems: "flex-start", justifyContent: "center" } }, h("div", { style: { fontSize: "14px" } }, x.name), h("div.faint", { style: { fontSize: "12px" } }, x.detail)),
    onActivate: x => void connect(x),
  });
  list.el.style.flex = "1 1 0";
  list.el.style.minHeight = "120px";
  const connect = async (x: Device | undefined) => {
    if (!x) return;
    d.close();
    try { await call("cast.connect", { index: x.index }); } catch (e: any) { shell.setStatus(String(e.message ?? e)); }
  };
  let open = true;
  on("cast.found", (x: Device) => { if (!open) return; found.push(x); list.setItems([...found], true); if (found.length === 1) list.select(0); });
  d.body.append(h("div.h2", "play on another device"),
    h("div.note", "Speakers, TVs and Chromecasts on your network that can play music. Songs, podcasts, audiobooks and radio play there; videos stay on this computer."),
    status, list.el,
    dialogButtons(d, "play there", () => void connect(list.selected()[0])));
  void d.closed.then(() => { open = false; });
  const look = async () => {
    status.textContent = "looking on your network…";
    const n = await call<number>("cast.find", { seconds: 5 });
    if (open) status.textContent = n === 0 ? "No devices found. They must be switched on and on the same network as this computer." : `${n} found`;
  };
  await look();
}
