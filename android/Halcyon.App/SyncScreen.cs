using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.Droid;

/// <summary>
/// Sync with Phoenix on a PC or Mac on the same Wi-Fi network. Pairing is one tap: the phone finds the computer and
/// asks, and the person there chooses Allow (or the phone's camera scans the QR code in the computer's settings, which
/// opens Phoenix here and pairs at once; or the six-digit code is typed). Then tick the playlists, artists and albums
/// to keep on the phone; the phone keeps them up to date by itself whenever Phoenix is opened.
/// </summary>
public static class SyncScreen
{
    static Settings S => App.Settings;
    static SyncClient Sync => App.Sync;

    /// <summary>What this phone calls itself on the computer ("Pixel 8").</summary>
    public static string PhoneName
    {
        get
        {
            var model = Android.OS.Build.Model ?? "phone";
            var maker = Android.OS.Build.Manufacturer ?? "";
            return maker != "" && !model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) && maker != "Google" ? $"{Upper(maker)} {model}" : model;
        }
    }

    static string Upper(string s) => s == "" ? s : char.ToUpperInvariant(s[0]) + s[1..];

    static string PcName => S.SyncPcName != "" ? S.SyncPcName : S.SyncHost;

    /// <summary>Copies under Music/Halcyon go into the collection: makes sure that folder is one of its folders.</summary>
    static void EnsureFolder()
    {
        var sep = System.IO.Path.DirectorySeparatorChar;
        if (!S.MusicFolders.Any(f => (App.SyncDir + sep).StartsWith(f.TrimEnd(sep) + sep))) { S.MusicFolders.Add(App.SyncDir); S.Save(); }
    }

    // ---------- syncing by itself ----------

    static bool _autoRunning;

    /// <summary>
    /// When Phoenix is opened: if it is paired, syncing by itself is on and it has been ten minutes, quietly brings the
    /// phone up to date when the PC can be reached. Nothing is said when it can't (the phone is away from home).
    /// </summary>
    public static async Task AutoSync(MainActivity a)
    {
        if (_autoRunning || !Sync.IsPaired || !S.SyncAuto || Sync.IsBusy || Fmt.Now - S.SyncLastAuto < 600) return;
        _autoRunning = true;
        try
        {
            if (!await SyncClient.ReachableAsync()) return;
            S.SyncLastAuto = Fmt.Now;
            S.Save();
            EnsureFolder();
            var summary = await Sync.SyncAsync(App.SyncDir, text => HalcyonApp.Main.Post(() => App.SetStatus(text)));
            if (summary != "already up to date") HalcyonApp.Main.Post(() => App.SetStatus($"synced with {PcName}: {summary}"));
        }
        catch { /* the next time Phoenix is opened */ }
        finally { _autoRunning = false; }
    }

    // ---------- pairing from the QR code (phoenix://pair?host=…&t=…) ----------

    public static void PairFromLink(MainActivity a, string host, string token)
    {
        App.SetStatus($"pairing with Phoenix at {host}…");
        _ = Task.Run(async () =>
        {
            try
            {
                await SyncClient.PairAsync(host, token, PhoneName);
                HalcyonApp.Main.Post(() =>
                {
                    App.SetStatus("");
                    a.Push(Build(a));
                    Ui.Alert(a, $"This phone is paired with {PcName}.\n\nChoose the playlists, artists or albums to keep on the phone, then tap “sync now”. After that, the phone keeps them up to date by itself whenever you open Phoenix at home.");
                });
            }
            catch (Exception ex) { HalcyonApp.Main.Post(() => { App.SetStatus(""); Ui.Alert(a, "Couldn't pair.\n\n" + ex.Message); }); }
        });
    }

    public static Page Build(MainActivity a)
    {
        PcLibrary? lib = null;
        var note = "";
        var tab = "playlists";
        var asking = false;
        var page = new Page(a, "sync with pc");

        void Say(string text) { note = text; page.Reload(); }

        async Task Load()
        {
            SetActions();
            if (!Sync.IsPaired) { lib = null; Say(""); return; }
            try
            {
                Say($"reading {PcName}'s collection…");
                lib = await Sync.FetchLibraryAsync();
                Say("");
            }
            catch (Exception ex) { lib = null; Say(ex.Message); }
        }

        // the old way, for a PC with an older Phoenix (or when asking can't get through): the six-digit code
        void AskCode() => Ui.Ask(a, "Pairing code", "6-digit code from the PC's settings", "", async code =>
        {
            S.SyncCode = code.Trim();
            S.SyncPcId = "";
            if (await SyncClient.InfoAsync(S.SyncHost) is { } info) { S.SyncPcName = info.Name; S.SyncPcId = info.Id; }
            S.Save();
            await Load();
        });

        async Task Ask(string address, string name)
        {
            var info = await SyncClient.InfoAsync(address);
            if (info == null) { Say($"Can't reach Phoenix at {address}."); return; }
            if (info.Pairing < 2)
            {
                // an older Phoenix on the PC: pair with its code
                S.SyncHost = address; S.SyncPcName = info.Name; S.Save();
                AskCode();
                return;
            }
            asking = true;
            Say($"Now look at {info.Name}: choose Allow when Phoenix asks whether “{PhoneName}” may pair. (waiting…)");
            try
            {
                await SyncClient.PairAsync(address, "", PhoneName);
                asking = false;
                Say($"Paired with {PcName}. Choose what to keep on this phone below, then tap “sync now”.");
                await Load();
                note = $"Paired with {PcName}. Choose what to keep on this phone below, then tap “sync now”.";
                page.Reload();
            }
            catch (Exception ex) { asking = false; Say(ex.Message); }
        }

        async Task Pair()
        {
            if (asking) return;
            Say("looking for Phoenix on this Wi-Fi network…");
            var found = await SyncClient.DiscoverAsync(2000);
            if (found.Count == 0)
            {
                Say("");
                Ui.Menu(a, "No PC answered",
                    ("Scan the QR code on the PC", () => Ui.Alert(a, "On the PC, open Phoenix → settings → phone → sync options, tick “Let my phone sync”, and point this phone's camera at the QR code there. Open the link it shows: Phoenix pairs by itself.")),
                    ("Type the PC's address", EnterAddress),
                    ("Why can't it be found?", () => Ui.Alert(a, "Check that:\n\n• Phoenix is open on the PC, with “Let my phone sync with and control this PC” ticked (settings → phone).\n• The phone and the PC are on the same Wi-Fi network (not a guest network).\n• Windows Firewall allows Phoenix on private networks.\n\nSome routers don't let devices find each other: scanning the QR code on the PC still works.")));
                return;
            }
            if (found.Count == 1) await Ask(found[0].address, found[0].name);
            else Ui.Menu(a, "Choose your PC", found.Select(f => ($"{f.name}  ·  {f.address}", (Action)(() => _ = Ask(f.address, f.name)))).ToArray());
        }

        void EnterAddress() => Ui.Ask(a, "PC address", "shown in Phoenix's settings on the PC, e.g. 192.168.1.20", S.SyncHost, address =>
        {
            var host = address.Trim();
            if (host != "") _ = Ask(host, host);
        });

        void Forget() => Ui.Confirm(a, $"Forget {PcName}? This phone stops syncing with it until it pairs again. The music already copied stays on the phone.", "Forget", () =>
        {
            SyncClient.Unpair();
            lib = null;
            _ = Load();
        });

        async Task Run()
        {
            if (Sync.IsBusy) return;
            if (lib == null) { await Load(); if (lib == null) return; }
            EnsureFolder();
            try
            {
                var summary = await Sync.SyncAsync(App.SyncDir, text => HalcyonApp.Main.Post(() => { note = text; page.Reload(); App.SetStatus(text); }));
                S.SyncLastAuto = Fmt.Now;
                try { lib = await Sync.FetchLibraryAsync(); } catch { }
                Say("last sync: " + summary);
            }
            catch (Exception ex) { Say(ex.Message); }
        }

        void SetActions()
        {
            if (Sync.IsPaired)
                page.Actions(("sync now", () => _ = Run()), ("remote", () => RemoteScreen.Open(a)), ("forget this pc", Forget));
            else
                page.Actions(("pair with my pc", () => _ = Pair()), ("pairing code", () => { if (S.SyncHost == "") EnterAddress(); else AskCode(); }), ("address", EnterAddress));
        }

        page.Subtitle = () =>
        {
            if (!Sync.IsPaired) return "not paired";
            if (lib == null) return PcName;
            var wanted = Sync.Wanted(lib);
            var have = Sync.State.Files.Select(f => f.PcId).ToHashSet();
            var todo = wanted.Where(t => !have.Contains(t.Id)).ToList();
            if (wanted.Count == 0) return Fmt.Join(lib.Name, "nothing chosen yet", have.Count > 0 ? $"{have.Count} to remove" : "");
            return Fmt.Join(lib.Name, Fmt.Plural(wanted.Count, "song") + " chosen", SyncClient.Size(wanted.Sum(t => t.Size)),
                todo.Count > 0 ? $"{todo.Count} to copy" : wanted.Count > 0 ? "all on this phone" : "");
        };
        page.Chips(() => lib == null ? Array.Empty<string>() : new[] { "playlists", "artists", "albums" }, () => tab, t => tab = t);

        page.List(() =>
        {
            var rows = new List<Row>();
            if (note != "") rows.Add(Row.Note(note));
            if (!Sync.IsPaired)
            {
                if (!asking)
                {
                    rows.Add(Row.Caption("PAIR WITH YOUR PC OR MAC"));
                    rows.Add(new Row { Title = "pair with my pc", Sub = "finds Phoenix on this Wi-Fi network and asks the PC to allow this phone: choose Allow there", Click = () => _ = Pair() });
                    rows.Add(new Row
                    {
                        Title = "scan the qr code on the pc", Sub = "in Phoenix on the PC: settings → phone → sync options. Point this phone's camera at the code and open its link",
                        Click = () => Ui.Alert(a, "Open your phone's camera (or Google Lens) and point it at the QR code in Phoenix's settings on the PC (settings → phone → sync options). Open the link it shows: Phoenix opens here and pairs by itself.")
                    });
                    rows.Add(new Row { Title = "type the pairing code", Sub = "the six-digit code shown in Phoenix's settings on the PC", Click = () => { if (S.SyncHost == "") EnterAddress(); else AskCode(); } });
                }
                return rows;
            }
            rows.Add(new Row
            {
                Title = "sync by itself", Trail = S.SyncAuto ? "on" : "off",
                Sub = S.SyncAuto ? $"whenever Phoenix is opened and {PcName} can be reached (at most every ten minutes)" : "only when you tap “sync now”",
                Click = () => { S.SyncAuto = !S.SyncAuto; S.Save(); page.Reload(); }
            });
            if (lib == null) return rows;
            rows.Add(Row.Caption("KEEP ON THIS PHONE"));
            static string Box(bool on) => on ? "☑  " : "☐  ";
            var st = Sync.State;
            void Flip(List<string> list, string key)
            {
                if (Sync.IsBusy) return;
                SyncClient.Toggle(list, key);
                Sync.SaveState();
                page.Reload();
            }
            switch (tab)
            {
                case "artists":
                    foreach (var g in lib.Tracks.GroupBy(t => t.AlbumArtist, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var on = st.Artists.Contains(g.Key, StringComparer.OrdinalIgnoreCase);
                        var name = g.Key;
                        rows.Add(new Row { Title = Box(on) + name, Sub = Fmt.Join(Fmt.Plural(g.Count(), "song"), SyncClient.Size(g.Sum(t => t.Size))), Click = () => Flip(st.Artists, name) });
                    }
                    break;
                case "albums":
                    foreach (var g in lib.Tracks.GroupBy(t => t.AlbumKey).OrderBy(g => g.First().AlbumArtist, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.First().Album, StringComparer.OrdinalIgnoreCase))
                    {
                        var f = g.First();
                        var viaArtist = st.Artists.Contains(f.AlbumArtist, StringComparer.OrdinalIgnoreCase);
                        var on = viaArtist || st.Albums.Contains(g.Key);
                        var key = g.Key;
                        rows.Add(new Row
                        {
                            Title = Box(on) + f.Album, Dim = viaArtist,
                            Sub = Fmt.Join(f.AlbumArtist, Fmt.Plural(g.Count(), "song"), SyncClient.Size(g.Sum(t => t.Size)), viaArtist ? "whole artist is chosen" : ""),
                            Click = () => { if (!viaArtist) Flip(st.Albums, key); }
                        });
                    }
                    break;
                default:
                    if (lib.Playlists.Count == 0) rows.Add(Row.Note("The PC has no playlists with local songs. Choose artists or albums instead, or on the PC right-click music and choose “Add to phone sync list”."));
                    // the list filled on the PC (right-click → add to phone sync list) comes first and is always synced
                    foreach (var p in lib.Playlists.OrderBy(p => string.Equals(p.Name, SyncClient.PhoneListName, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        var fromPc = string.Equals(p.Name, SyncClient.PhoneListName, StringComparison.OrdinalIgnoreCase);
                        var on = App.Sync.IsChosen(p.Name);
                        var ids = p.TrackIds.ToHashSet();
                        var name = p.Name;
                        rows.Add(new Row
                        {
                            Title = Box(on) + name + (fromPc ? "  (chosen on the PC)" : ""),
                            Click = () => { if (fromPc) Ui.Toast(a, "This list is filled on the PC (right-click → add to phone sync list) and is always synced. Change it there."); else Flip(st.Playlists, name); },
                            Sub = Fmt.Join(Fmt.Plural(p.TrackIds.Count, "song"), SyncClient.Size(lib.Tracks.Where(t => ids.Contains(t.Id)).Sum(t => t.Size)))
                        });
                    }
                    break;
            }
            return rows;
        });
        _ = Load();
        return page;
    }
}
