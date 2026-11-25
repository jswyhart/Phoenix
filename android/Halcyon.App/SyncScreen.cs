using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.Droid;

/// <summary>
/// Sync with the Halcyon Windows app on the same Wi-Fi network: pair with the PC, tick the playlists, artists and
/// albums to keep on the phone, then copy. Unticking something removes its copies on the next sync.
/// </summary>
public static class SyncScreen
{
    static Settings S => App.Settings;
    static SyncClient Sync => App.Sync;

    public static Page Build(MainActivity a)
    {
        PcLibrary? lib = null;
        var note = "";
        var tab = "playlists";
        var page = new Page(a, "sync with pc");

        void Say(string text) { note = text; page.Reload(); }

        async Task Load()
        {
            if (!Sync.IsPaired) { Say(S.SyncHost == "" ? "Tap “find pc” while Phoenix is open on the PC with phone sync turned on." : "Enter the pairing code shown in Phoenix's settings on the PC."); return; }
            try
            {
                Say("reading the PC's collection…");
                lib = await Sync.FetchLibraryAsync();
                Say("");
            }
            catch (Exception ex) { lib = null; Say(ex.Message); }
        }

        void AskCode() => Ui.Ask(a, "Pairing code", "6-digit code from the PC's settings", "", async code =>
        {
            S.SyncCode = code.Trim();
            S.Save();
            await Load();
        });

        void UseHost(string address)
        {
            S.SyncHost = address.Trim();
            S.Save();
            if (S.SyncCode == "") AskCode(); else _ = Load();
        }

        async Task Find()
        {
            Say("looking for Phoenix on this network…");
            var found = await SyncClient.DiscoverAsync();
            Say("");
            if (found.Count == 0)
            {
                Ui.Confirm(a, "No PC answered. Check that Phoenix is open on the PC, that “Let my phone sync with this PC” is ticked in its settings, " +
                              "and that Windows Firewall allows Phoenix on private networks.\n\nYou can also type the PC's address, shown in its settings.", "Enter address", EnterAddress);
                return;
            }
            if (found.Count == 1) UseHost(found[0].address);
            else Ui.Menu(a, "Choose a PC", found.Select(f => ($"{f.name}  ·  {f.address}", (Action)(() => UseHost(f.address)))).ToArray());
        }

        void EnterAddress() => Ui.Ask(a, "PC address", "e.g. 192.168.1.20", S.SyncHost, UseHost);

        async Task Run()
        {
            if (Sync.IsBusy) return;
            if (lib == null) { await Load(); if (lib == null) return; }
            // Copies go under Music/Halcyon; make sure that is part of the collection.
            var sep = System.IO.Path.DirectorySeparatorChar;
            if (!S.MusicFolders.Any(f => (App.SyncDir + sep).StartsWith(f.TrimEnd(sep) + sep))) { S.MusicFolders.Add(App.SyncDir); S.Save(); }
            try
            {
                var summary = await Sync.SyncAsync(App.SyncDir, text => HalcyonApp.Main.Post(() => { note = text; page.Reload(); App.SetStatus(text); }));
                try { lib = await Sync.FetchLibraryAsync(); } catch { }
                Say("last sync: " + summary);
            }
            catch (Exception ex) { Say(ex.Message); }
        }

        void Remote()
        {
            if (!Sync.IsPaired) { Say("Pair with the PC first (find pc, then its pairing code)."); return; }
            RemoteScreen.Open(a);
        }

        page.Actions(("sync now", () => _ = Run()), ("remote", Remote), ("find pc", () => _ = Find()), ("pairing code", AskCode), ("address", EnterAddress));
        page.Subtitle = () =>
        {
            if (lib == null) return S.SyncHost == "" ? "not paired" : S.SyncHost;
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
            if (lib == null) return rows;
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
                    if (lib.Playlists.Count == 0) rows.Add(Row.Note("The PC has no playlists with local songs. Choose artists or albums instead."));
                    // the list filled on the PC (music dragged onto its phone icon) comes first and is always synced
                    foreach (var p in lib.Playlists.OrderBy(p => string.Equals(p.Name, SyncClient.PhoneListName, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        var fromPc = string.Equals(p.Name, SyncClient.PhoneListName, StringComparison.OrdinalIgnoreCase);
                        var on = App.Sync.IsChosen(p.Name);
                        var ids = p.TrackIds.ToHashSet();
                        var name = p.Name;
                        rows.Add(new Row
                        {
                            Title = Box(on) + name + (fromPc ? "  (chosen on the PC)" : ""),
                            Click = () => { if (fromPc) Ui.Toast(a, "This list is filled on the PC and is always synced. Change it there."); else Flip(st.Playlists, name); },
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
