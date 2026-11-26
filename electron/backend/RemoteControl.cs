using System.Text.Json;
using Halcyon.Core;

namespace Phoenix.Backend;

/// <summary>
/// The phone as a remote control, as on Windows: a small page the phone app (or any browser on your network) opens
/// from this computer, showing what is playing with buttons to work it. It rides on the phone sync server, so it is
/// only there while phone sync is on, and every request must carry the same pairing code.
/// </summary>
public static class RemoteControl
{
    static Player P => App.Player;

    static T Ui<T>(Func<T> f) { T r = default!; MainLoop.Invoke(() => r = f()); return r; }

    /// <summary>What is playing, as the remote page shows it.</summary>
    public static string StateJson() => Ui(() =>
    {
        var cur = P.Current;
        var index = P.CurrentIndex;
        // the queue around what is playing: a long queue is not sent whole every second
        var from = Math.Max(0, index - 3);
        var queue = P.Queue.Skip(from).Take(60).Select((q, i) => new { index = from + i, title = q.Title, artist = q.Subtitle, current = from + i == index }).ToList();
        return JsonSerializer.Serialize(new
        {
            pc = Environment.MachineName,
            title = cur?.Title ?? "", artist = cur?.Subtitle ?? "", album = cur?.Album ?? "",
            playing = P.IsPlaying, position = P.PositionMs / 1000.0, duration = P.DurationMs / 1000.0,
            volume = P.Volume, muted = Program.Engine.Muted, shuffle = P.Shuffle, repeat = P.Repeat,
            rating = cur?.Track?.Rating ?? 0, canRate = cur?.Track != null,
            art = cur == null ? "" : ArtId(cur), sleep = P.SleepText, casting = P.Cast?.Name ?? "",
            queue, queueCount = P.Queue.Count
        });
    });

    static string ArtId(PlayItem item) => string.IsNullOrEmpty(item.ArtPath) ? "" : ((uint)item.ArtPath.GetHashCode()).ToString();

    /// <summary>The cover of what is playing, if it is a picture file on this computer.</summary>
    public static string? ArtFile() => Ui(() =>
    {
        var art = P.Current?.ArtPath ?? "";
        return art != "" && !art.Contains("://") && !art.StartsWith("thumb:") && File.Exists(art) ? art : null;
    });

    /// <summary>Carries out a button press from the remote. Returns false for something it does not know.</summary>
    public static bool Command(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var cmd = doc.RootElement.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "";
        var value = doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        return Ui(() =>
        {
            switch (cmd)
            {
                case "toggle": P.Toggle(); break;
                case "next": P.Next(); break;
                case "prev": P.Previous(); break;
                case "seek": P.Seek((long)(Math.Max(0, value) * 1000)); break;
                case "volume": P.Volume = Math.Clamp(value, 0, 1); break;
                case "mute": Program.Engine.Muted = !Program.Engine.Muted; break;
                case "shuffle": P.Shuffle = !P.Shuffle; break;
                case "repeat": P.Repeat = (P.Repeat + 1) % 3; break;
                case "like": if (P.Current?.Track is { } t) App.Lib.Rate(new[] { t }, t.Rating > 0 ? 0 : 1); break;
                case "play": P.PlayAt((int)value); break;
                case "sleep": P.SetSleep(value > 0 ? TimeSpan.FromMinutes(value) : null); break;
                default: return false;
            }
            Api.PlayerChanged();
            return true;
        });
    }

    public const string Page = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="theme-color" content="#0b0b0b">
<title>Phoenix remote</title>
<style>
:root{--bg:#0b0b0b;--fg:#f2f2f2;--dim:#a6a6a6;--faint:#6a6a6a;--line:#2a2a2a;--accent:#f0447a}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
html,body{margin:0;background:var(--bg);color:var(--fg);font:16px/1.35 "Segoe UI",system-ui,sans-serif}
body{padding:16px 16px 28px;max-width:560px;margin:0 auto}
header{display:flex;justify-content:space-between;align-items:baseline;color:var(--faint);font-size:12px;letter-spacing:.12em;text-transform:uppercase;margin-bottom:14px}
#art{width:100%;aspect-ratio:1;background:#1c1c1c center/cover no-repeat;display:flex;align-items:center;justify-content:center;color:#444;font-size:64px}
h1{font-weight:300;font-size:30px;margin:16px 0 0;overflow-wrap:anywhere}
#artist{color:var(--dim);font-size:19px;font-weight:300}
#album{color:var(--faint);font-size:14px;margin-top:2px;min-height:19px}
.row{display:flex;align-items:center;gap:10px}
.time{font-size:12px;color:var(--dim);font-variant-numeric:tabular-nums;min-width:40px}
.time:last-child{text-align:right}
input[type=range]{flex:1;accent-color:var(--accent);height:28px;margin:0;min-width:0}
#buttons{justify-content:space-between;margin:10px 0 6px}
button{background:none;border:0;color:var(--fg);font:inherit;padding:0;cursor:pointer}
.b{width:56px;height:56px;border-radius:50%;display:flex;align-items:center;justify-content:center}
.b svg{width:26px;height:26px;fill:currentColor}
.b.small svg{width:20px;height:20px}
.b.big{width:76px;height:76px;border:2px solid var(--fg)}
.b.big svg{width:34px;height:34px}
.b.on{color:var(--accent)} .b.off{color:var(--faint)}
.b:active{background:#1e1e1e}
#note{color:var(--accent);font-size:13px;min-height:18px;margin:6px 0}
h2{font-size:12px;letter-spacing:.12em;color:var(--faint);font-weight:400;margin:22px 0 4px;text-transform:uppercase}
#queue{list-style:none;margin:0;padding:0}
#queue li{padding:9px 0;border-bottom:1px solid var(--line);cursor:pointer}
#queue li.cur .t{color:var(--accent);font-weight:600}
#queue .a{color:var(--faint);font-size:13px}
#pair{display:none;margin-top:30vh;text-align:center}
#pair input{font:inherit;font-size:28px;letter-spacing:.2em;text-align:center;width:9em;background:#161616;color:var(--fg);border:1px solid var(--line);padding:10px;margin:14px 0}
#pair button{border:1px solid var(--fg);border-radius:20px;padding:8px 26px}
</style></head><body>
<div id="pair"><div>Enter the pairing code shown in Phoenix on the computer<br><span style="color:var(--faint);font-size:13px">settings → phone → sync options</span></div>
<input id="code" inputmode="numeric" maxlength="6" autocomplete="off"><br><button id="go">connect</button><div id="pairnote" style="color:var(--accent);margin-top:12px;min-height:20px"></div></div>
<div id="main">
<header><span>Phoenix remote</span><span id="pc"></span></header>
<div id="art">&#9835;</div>
<h1 id="title">nothing playing</h1><div id="artist"></div><div id="album"></div>
<div class="row" style="margin-top:14px"><span class="time" id="pos"></span><input type="range" id="seek" min="0" max="1" step="1" value="0"><span class="time" id="dur"></span></div>
<div class="row" id="buttons">
<button class="b small" id="shuffle" aria-label="shuffle"><svg viewBox="0 0 24 24"><path d="M17 3l4 4-4 4V8h-2.6l-2.2 3-1.3-1.7L13.4 6H17V3zM3 6h4.4l8 10H17v-3l4 4-4 4v-3h-2.6l-8-10H3V6zm0 10h3.4l1.5-2 1.3 1.7L7.4 18H3v-2z"/></svg></button>
<button class="b" id="prev" aria-label="previous"><svg viewBox="0 0 24 24"><path d="M6 5h2v14H6zM20 5v14L9 12z"/></svg></button>
<button class="b big" id="toggle" aria-label="play or pause"><svg viewBox="0 0 24 24"><path id="playicon" d="M8 5v14l11-7z"/></svg></button>
<button class="b" id="next" aria-label="next"><svg viewBox="0 0 24 24"><path d="M16 5h2v14h-2zM4 5l11 7-11 7z"/></svg></button>
<button class="b small" id="repeat" aria-label="repeat"><svg viewBox="0 0 24 24"><path d="M7 7h10v3l4-4-4-4v3H5v6h2V7zm10 10H7v-3l-4 4 4 4v-3h12v-6h-2v4z"/></svg></button>
</div>
<div class="row"><button class="b small" id="like" aria-label="like"><svg viewBox="0 0 24 24"><path d="M12 21l-1.5-1.3C5.4 15.4 2 12.3 2 8.5 2 5.4 4.4 3 7.5 3c1.7 0 3.4.8 4.5 2.1C13.1 3.8 14.8 3 16.5 3 19.6 3 22 5.4 22 8.5c0 3.8-3.4 6.9-8.5 11.2L12 21z"/></svg></button>
<input type="range" id="volume" min="0" max="1" step="0.01" value="0.8" aria-label="volume"></div>
<div id="note"></div>
<h2 id="queuehead">next</h2><ul id="queue"></ul>
</div>
<script>
const $=id=>document.getElementById(id);
let code=(location.hash.match(/code=(\d+)/)||[])[1]||localStorage.getItem('phoenix-code')||'';
if(location.hash)history.replaceState(null,'',location.pathname);
let state=null,artId=null,dragging=false,volDragging=false,stamp=0;
const fmt=s=>{s=Math.max(0,Math.floor(s));const h=Math.floor(s/3600),m=Math.floor(s%3600/60),x=String(s%60).padStart(2,'0');return h?`${h}:${String(m).padStart(2,'0')}:${x}`:`${m}:${x}`};
function needCode(msg){$('main').style.display='none';$('pair').style.display='block';$('pairnote').textContent=msg||'';}
async function call(path,body){
  const r=await fetch(path,{method:body?'POST':'GET',headers:{'X-Halcyon-Code':code},body:body?JSON.stringify(body):undefined,cache:'no-store'});
  if(r.status===401){needCode(code?'That code was not accepted.':'');throw new Error('code');}
  return r;
}
const send=(cmd,value)=>call('/remote/command',{cmd,value}).then(refresh).catch(()=>{});
async function refresh(){
  if(!code){needCode();return;}
  try{
    state=await (await call('/remote/state')).json();stamp=Date.now();
    localStorage.setItem('phoenix-code',code);
    $('pair').style.display='none';$('main').style.display='block';show();
  }catch(e){if(e.message!=='code')$('note').textContent='can\'t reach Phoenix on the computer';}
}
function show(){
  const s=state;if(!s)return;
  $('pc').textContent=s.pc;$('title').textContent=s.title||'nothing playing';$('artist').textContent=s.artist;$('album').textContent=s.album;
  $('playicon').setAttribute('d',s.playing?'M6 5h4v14H6zM14 5h4v14h-4z':'M8 5v14l11-7z');
  $('shuffle').className='b small '+(s.shuffle?'on':'off');$('repeat').className='b small '+(s.repeat?'on':'off');
  $('like').className='b small '+(s.rating>0?'on':s.canRate?'':'off');
  if(!volDragging)$('volume').value=s.volume;
  $('seek').max=Math.max(1,Math.floor(s.duration));$('dur').textContent=s.duration>0?fmt(s.duration):'';
  $('note').textContent=[s.casting?'playing on '+s.casting:'',s.sleep].filter(x=>x).join('  ·  ');
  if(s.art!==artId){artId=s.art;
    if(!s.art){$('art').style.backgroundImage='';$('art').textContent='♫';}
    else call('/remote/art').then(r=>r.ok?r.blob():null).then(b=>{if(b){$('art').style.backgroundImage=`url(${URL.createObjectURL(b)})`;$('art').textContent='';}else{$('art').style.backgroundImage='';$('art').textContent='♫';}}).catch(()=>{});}
  const q=$('queue');q.textContent='';
  $('queuehead').textContent=s.queueCount?`now playing  ·  ${s.queueCount}`:'';
  for(const item of s.queue){const li=document.createElement('li');if(item.current)li.className='cur';
    const t=document.createElement('div');t.className='t';t.textContent=item.title;const a=document.createElement('div');a.className='a';a.textContent=item.artist;
    li.append(t,a);li.onclick=()=>send('play',item.index);q.append(li);}
  tick();
}
function tick(){
  if(!state||dragging)return;
  const p=state.position+(state.playing?(Date.now()-stamp)/1000:0);
  const at=state.duration>0?Math.min(p,state.duration):p;
  $('seek').value=at;$('pos').textContent=state.title?fmt(at):'';
}
for(const id of['toggle','next','prev','shuffle','repeat','like'])$(id).onclick=()=>send(id);
$('seek').oninput=()=>{dragging=true;$('pos').textContent=fmt($('seek').value)};
$('seek').onchange=()=>{dragging=false;send('seek',Number($('seek').value))};
$('volume').oninput=()=>{volDragging=true;call('/remote/command',{cmd:'volume',value:Number($('volume').value)}).catch(()=>{})};
$('volume').onchange=()=>{volDragging=false};
$('go').onclick=()=>{code=$('code').value.trim();refresh()};
$('code').onkeydown=e=>{if(e.key==='Enter')$('go').click()};
setInterval(tick,250);setInterval(refresh,1500);refresh();
</script></body></html>
""";
}
