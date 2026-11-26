using System.Net;
using System.Text;
using System.Xml.Linq;
using Halcyon.Core;
using Halcyon.Services;

namespace Phoenix.Backend;

/// <summary>
/// The self-test's casting check, as on Windows: a pretend DLNA speaker on this machine answers the commands a real
/// one would, and the player is sent to it, worked there, moved on when a song ends there, and brought back.
/// </summary>
public static partial class Api
{
    static void RegisterSmokeCast()
    {
        if (!Program.SmokeMode) return;
        Rpc.On("smoke.cast", async castParams =>
        {
            var port = 0;
            HttpListener? speaker = null;
            for (var tries = 0; tries < 20 && speaker == null; tries++)
            {
                port = 42300 + Random.Shared.Next(4000);
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{port}/");
                try { l.Start(); speaker = l; } catch { }
            }
            if (speaker == null) return (object)new { skipped = true };
            string loadedUrl = "", loadedMeta = "", state = "STOPPED", volume = "";
            _ = Task.Run(async () =>
            {
                while (speaker.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await speaker.GetContextAsync(); } catch { return; }
                    var reply = "";
                    if (c.Request.Url!.AbsolutePath == "/desc.xml")
                        reply = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><friendlyName>Pretend Speaker</friendlyName><serviceList>" +
                                "<service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>/ctl/av</controlURL></service>" +
                                "<service><serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType><controlURL>ctl/rc</controlURL></service></serviceList></device></root>";
                    else
                    {
                        using var reader = new StreamReader(c.Request.InputStream);
                        var soap = XDocument.Parse(await reader.ReadToEndAsync());
                        var action = soap.Descendants().First(x => x.Name.NamespaceName.StartsWith("urn:schemas-upnp-org:service"));
                        string Arg(string name) => action.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value ?? "";
                        var inner = "";
                        switch (action.Name.LocalName)
                        {
                            case "SetAVTransportURI": loadedUrl = Arg("CurrentURI"); loadedMeta = Arg("CurrentURIMetaData"); break;
                            case "Play": state = "PLAYING"; break;
                            case "Pause": state = "PAUSED_PLAYBACK"; break;
                            case "Stop": state = "STOPPED"; break;
                            case "SetVolume": volume = Arg("DesiredVolume"); break;
                            case "GetTransportInfo": inner = $"<CurrentTransportState>{state}</CurrentTransportState>"; break;
                            case "GetPositionInfo": inner = "<TrackDuration>0:00:04</TrackDuration><RelTime>0:00:02</RelTime>"; break;
                        }
                        reply = $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:{action.Name.LocalName}Response xmlns:u=\"{action.Name.NamespaceName}\">{inner}</u:{action.Name.LocalName}Response></s:Body></s:Envelope>";
                    }
                    var bytes = Encoding.UTF8.GetBytes(reply);
                    c.Response.ContentType = "text/xml";
                    c.Response.ContentLength64 = bytes.Length;
                    await c.Response.OutputStream.WriteAsync(bytes);
                    c.Response.Close();
                }
            });
            async Task<bool> Until(Func<bool> test, int ms = 6000)
            {
                var end = DateTime.UtcNow.AddMilliseconds(ms);
                while (DateTime.UtcNow < end) { if (test()) return true; await Task.Delay(100); }
                return test();
            }
            try
            {
                var location = $"http://127.0.0.1:{port}/desc.xml";
                using var http = new HttpClient();
                var described = DlnaDevice.Describe(XDocument.Parse(await http.GetStringAsync(location)), location);
                var tracks = Lib.Albums.First(a => a.Tracks.Count >= 2).Tracks;
                P.Volume = 0;
                P.PlayTracks(tracks);
                await Until(() => P.IsPlaying && P.PositionMs > 0, 3000);
                P.StartCast(new CastTarget("Pretend Speaker", "DLNA", "127.0.0.1", port, location).Connect());
                var loaded = await Until(() => loadedUrl != "" && state == "PLAYING" && P.OnCast);
                var fetched = loadedUrl == "" ? Array.Empty<byte>() : await http.GetByteArrayAsync(loadedUrl);
                await Until(() => volume != "", 2000);
                var castName = P.Cast?.Name;
                var metaHasTitle = loadedMeta.Contains(tracks[0].Title);   // before the next song replaces it
                await Until(() => P.Cast is { Loaded: true }, 4000);
                P.Toggle();
                var paused = await Until(() => state == "PAUSED_PLAYBACK", 3000);
                P.Toggle();
                await Until(() => state == "PLAYING", 3000);
                await Task.Delay(1300);
                var firstUrl = loadedUrl;
                state = "STOPPED";   // the speaker reached the end of the song
                var movedOn = await Until(() => P.CurrentIndex == 1 && loadedUrl != firstUrl && state == "PLAYING", 8000);
                P.StopCast();
                var back = await Until(() => !P.OnCast && P.Cast == null && !P.IsPlaying && P.CurrentIndex == 1, 4000);
                return new
                {
                    skipped = false, describedName = described?.Name, transport = described?.TransportUrl, expectedTransport = $"http://127.0.0.1:{port}/ctl/av",
                    loaded, fetched = fetched.Length, size = new FileInfo(tracks[0].SourceId).Length, metaHasTitle,
                    volume, castName, paused, movedOn, back, index = P.CurrentIndex
                };
            }
            finally
            {
                P.StopCast(resumeHere: false);
                P.Stop();
                try { speaker.Stop(); } catch { }
            }
        });
    }
}
