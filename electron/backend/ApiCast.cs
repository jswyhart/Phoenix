using Halcyon.Core;
using Halcyon.Services;

namespace Phoenix.Backend;

/// <summary>
/// Playing on another device, as on Windows: DLNA speakers and TVs, and Chromecasts, found on the network. Songs,
/// podcasts, audiobooks and radio play there; files on this computer are handed to the device by a small server
/// that only serves what is being cast.
/// </summary>
public static partial class Api
{
    static readonly List<CastTarget> CastFound = new();
    static CancellationTokenSource? _finding;

    static void RegisterCast()
    {
        // looks for devices for a few seconds; each one found is sent as it turns up
        Rpc.On("cast.find", async p =>
        {
            _finding?.Cancel();
            var stop = _finding = new CancellationTokenSource();
            lock (CastFound) CastFound.Clear();
            try
            {
                await CastFinder.FindAsync(t => MainLoop.Post(() =>
                {
                    int index;
                    lock (CastFound)
                    {
                        if (CastFound.Any(f => f.Address == t.Address && f.Name == t.Name)) return;
                        CastFound.Add(t);
                        index = CastFound.Count - 1;
                    }
                    Rpc.Emit("cast.found", new { index, name = t.Name, kind = t.Kind, detail = t.Detail });
                }), stop.Token, Math.Clamp(p.Int("seconds", 5), 1, 15));
            }
            catch (OperationCanceledException) { }
            lock (CastFound) return CastFound.Count;
        });
        Rpc.On("cast.connect", p =>
        {
            CastTarget target;
            lock (CastFound)
            {
                var i = p.Int("index");
                if (i < 0 || i >= CastFound.Count) throw new InvalidOperationException("That device is gone; look again.");
                target = CastFound[i];
            }
            App.SetStatus($"connecting to {target.Name}…");
            P.StartCast(target.Connect());
            App.SetStatus(P.Current == null ? $"{target.Name} is ready: what you play next plays there" : "");
            Rpc.Emit("player", PlayerState());
            return target.Name;
        });
        Rpc.On("cast.stop", _ => { P.StopCast(); Rpc.Emit("player", PlayerState()); });
    }
}
