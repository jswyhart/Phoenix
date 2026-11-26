using Android.Content;
using Android.Graphics;
using Android.Views;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.Droid;

/// <summary>
/// The song's lyrics over the now playing picture, as on the desktop: from a .lrc or .txt file beside the song, its
/// tags, LRCLIB or the clipboard. Timed lyrics follow the song, the line being sung lit and kept in view; tapping a
/// timed line jumps there.
/// </summary>
public class LyricsPane : LinearLayout
{
    readonly MainActivity _a;
    readonly ScrollView _scroll;
    readonly LinearLayout _lines;
    readonly TextView _note;
    readonly TextView _remove;
    Lyrics? _lyrics;
    PlayItem? _for;
    int _now = -1;
    static readonly HashSet<long> Asked = new();

    public LyricsPane(MainActivity a) : base(a)
    {
        _a = a;
        Orientation = Orientation.Vertical;
        SetBackgroundColor(Color.Argb(0xC8, 0, 0, 0));
        Clickable = true;
        _lines = Ui.Column(a);
        _lines.SetPadding(Th.Dp(22), Th.Dp(44), Th.Dp(22), Th.Dp(40));
        _scroll = new ScrollView(a) { VerticalScrollBarEnabled = false };
        _scroll.AddView(_lines);
        AddView(_scroll, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        _note = Ui.Text(a, "", 12.5f, Color.Argb(0xC0, 0xFF, 0xFF, 0xFF), null, 3);
        _note.SetPadding(Th.Dp(22), Th.Dp(4), Th.Dp(22), Th.Dp(2));
        AddView(_note, Ui.Fill());
        TextView Btn(string label, Action act)
        {
            var t = Ui.Text(a, label, 13, Color.White);
            t.SetPadding(Th.Dp(14), Th.Dp(10), Th.Dp(14), Th.Dp(10));
            t.Background = Ui.Ripple();
            t.Click += (_, _) => act();
            return t;
        }
        _remove = Btn("remove", () => { if (_for?.Track is { } t) { Lyrics.Remove(t); Reload(); } });
        var buttons = Ui.RowOf(a, Btn("find online", () => _ = FindAsync(false)), Btn("paste", Paste), _remove);
        buttons.SetPadding(Th.Dp(8), 0, Th.Dp(8), Th.Dp(6));
        AddView(buttons, Ui.Fill());
    }

    public int LineCount => _lyrics?.Lines.Count ?? 0;
    public int LineNow => _now;
    public string NoteText => _note.Text ?? "";

    void Reload() { _for = null; if (App.Player.Current is { } cur) Load(cur); }

    public void Load(PlayItem cur)
    {
        if (ReferenceEquals(cur, _for)) return;
        _for = cur;
        _now = -1;
        var t = cur.Track;
        _lyrics = t == null ? null : Lyrics.For(t);
        _lines.RemoveAllViews();
        if (_lyrics == null)
        {
            _lines.AddView(Ui.Text(_a, t == null ? "Lyrics are shown for songs in your collection."
                : "No lyrics for this song yet.\n\nFind them online, paste them, or put a .lrc or .txt file with the same name beside the song.", 16, Color.Argb(0xD0, 0xFF, 0xFF, 0xFF), null, 0));
            _note.Text = "";
        }
        else
        {
            foreach (var line in _lyrics.Lines)
            {
                var v = Ui.Text(_a, line.Text == "" ? " " : line.Text, 18, Color.Argb(0x99, 0xFF, 0xFF, 0xFF), Th.Light, 0);
                v.SetPadding(0, Th.Dp(5), 0, Th.Dp(5));
                if (_lyrics.Synced && line.Ms >= 0) { var ms = line.Ms; v.Click += (_, _) => App.Player.Seek(ms); }
                _lines.AddView(v);
            }
            _note.Text = _lyrics.Synced ? _lyrics.Source + "  ·  tap a line to jump to it" : _lyrics.Source;
            _scroll.ScrollTo(0, 0);
        }
        _remove.Visibility = t != null && Lyrics.HasStored(t) ? ViewStates.Visible : ViewStates.Gone;
        // look it up by itself, if settings say so (once per song)
        if (_lyrics == null && t != null && App.Settings.LyricsAuto && Asked.Add(t.Id)) _ = FindAsync(true);
    }

    /// <summary>Lights the line being sung and keeps it in view.</summary>
    public void Follow(long ms)
    {
        if (_lyrics is not { Synced: true }) return;
        var i = _lyrics.LineAt(ms);
        if (i == _now) return;
        if (_now >= 0 && _now < _lines.ChildCount) ((TextView)_lines.GetChildAt(_now)!).SetTextColor(Color.Argb(0x99, 0xFF, 0xFF, 0xFF));
        _now = i;
        if (i < 0 || i >= _lines.ChildCount) return;
        var v = (TextView)_lines.GetChildAt(i)!;
        v.SetTextColor(Color.White);
        _scroll.SmoothScrollTo(0, Math.Max(0, v.Top - _scroll.Height / 3));
    }

    async Task FindAsync(bool quiet)
    {
        if (_for?.Track is not { } t) return;
        if (!quiet) _note.Text = "looking on LRCLIB…";
        try
        {
            var text = await LyricsFinder.FindAsync(t);
            if (text == null) { if (!quiet) _note.Text = "LRCLIB has no lyrics for this song"; return; }
            Lyrics.Save(t, text);
            if (_for?.Track == t) Reload();
        }
        catch (Exception ex) { if (!quiet) _note.Text = "couldn't reach LRCLIB: " + ex.Message; }
    }

    void Paste()
    {
        if (_for?.Track is not { } t) return;
        var clip = (ClipboardManager?)_a.GetSystemService(Context.ClipboardService);
        var text = clip?.PrimaryClip is { ItemCount: > 0 } c ? c.GetItemAt(0)?.CoerceToText(_a) ?? "" : "";
        if (string.IsNullOrWhiteSpace(text)) { _note.Text = "copy the lyrics first, then paste"; return; }
        if (Lyrics.Parse(text, "imported") == null) { _note.Text = "that doesn't look like lyrics"; return; }
        Lyrics.Save(t, text);
        Reload();
    }
}

/// <summary>The ten equalizer bands, each a slider from -12 to +12 dB.</summary>
public static class EqualizerDialog
{
    public static void Show(MainActivity a, Action changed)
    {
        var s = App.Settings;
        var gains = EqPresets.Current(s).ToArray();
        var list = Ui.Column(a);
        list.SetPadding(Th.Dp(20), Th.Dp(8), Th.Dp(20), 0);
        for (var i = 0; i < EqPresets.Bands.Length; i++)
        {
            var band = i;
            var hz = EqPresets.Bands[i];
            var label = Ui.Text(a, "", 13, Th.FgDim);
            void Say() => label.Text = $"{(hz >= 1000 ? $"{hz / 1000:0.#} kHz" : $"{hz:0} Hz")}   {gains[band]:+0.0;-0.0;0} dB";
            Say();
            var bar = new SeekBar(a) { Max = 48, Progress = (int)Math.Round((gains[band] + 12) * 2) };
            bar.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(Th.Accent);
            bar.ThumbTintList = Android.Content.Res.ColorStateList.ValueOf(Th.Accent);
            bar.ProgressChanged += (_, e) =>
            {
                if (!e.FromUser) return;
                gains[band] = e.Progress / 2.0 - 12;
                Say();
                s.EqGains = gains.ToArray();
                s.EqPreset = "custom";
                HalcyonApp.Engine.ApplyEqualizer();
            };
            list.AddView(label);
            list.AddView(bar, Ui.Fill());
        }
        var scroll = new ScrollView(a);
        scroll.AddView(list);
        new AlertDialog.Builder(a).SetTitle("Equalizer")!.SetView(scroll)!
            .SetPositiveButton("Done", (_, _) => { s.Save(); changed(); })!
            .SetNeutralButton("Flat", (_, _) => { s.EqGains = new double[EqPresets.Bands.Length]; s.EqPreset = "custom"; s.Save(); HalcyonApp.Engine.ApplyEqualizer(); changed(); })!
            .Show();
    }
}
