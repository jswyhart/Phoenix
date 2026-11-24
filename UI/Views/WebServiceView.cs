using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Halcyon.Core;
using Halcyon.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Halcyon.UI;

/// <summary>What a web player says it is doing, and which of Phoenix's controls it can be worked with.</summary>
/// <remarks>Shuffle: 0 off, 1 on, 2 smart shuffle (Spotify), null when the page has no shuffle. Repeat: 0 off, 1 all, 2 one.</remarks>
public record WebState(string Title, string Artist, string Album, string Art, bool Playing, double Position, double Duration,
    int? Shuffle, int? Repeat, bool? Liked, bool CanSeek, bool CanNext, bool CanPrevious, bool HasMedia)
{
    public static readonly WebState None = new("", "", "", "", false, 0, 0, null, null, null, false, false, false, false);
}

/// <summary>
/// A streaming service's own web player shown inside Phoenix through the Windows built-in browser engine (WebView2).
/// You sign in on the service's page; the sign-in is kept in a browser profile in Phoenix's data folder. Phoenix does
/// not see the password and takes nothing from the stream.
///
/// While the page is what you are listening to, Phoenix's own controls work it. Play, pause, next, previous and seek
/// go through the page's media session (the same hooks the keyboard's media keys use), so they work on any site that
/// supports those; shuffle, repeat and like are found among the page's own buttons where a site has them, and the
/// player bar only shows the controls a page can really be worked with.
/// </summary>
public class WebServiceView : UserControl, IRemotePlayer
{
    readonly WebView2 _web = new();
    readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520, TextAlignment = TextAlignment.Center };
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(700) };
    bool _started, _covered, _active, _asking, _dumped;

    public WebService Service { get; }
    /// <summary>For the self-test.</summary>
    public bool Ready { get; private set; }
    public string Address => _web.CoreWebView2?.Source ?? "";
    public string PageTitle => _web.CoreWebView2?.DocumentTitle ?? "";
    public bool IsPlayingAudio => _web.CoreWebView2?.IsDocumentPlayingAudio == true;

    // Sizes of the audio files the page has fetched lately (newest last), to work out the bitrate being played.
    readonly List<long> _audioSizes = new();
    readonly HashSet<string> _audioHosts = new();
    static readonly int[] UsualBitrates = { 24, 96, 128, 160, 192, 256, 320 };

    /// <summary>
    /// The bitrate of the song that is playing, worked out from the size of the audio file the page fetched and the
    /// song's length ("≈ 256 kbps"), or "" when it cannot be told. Only for services that fetch a song as one file.
    /// </summary>
    public string BitrateText
    {
        get
        {
            var kbps = Bitrate(_audioSizes, State.Duration);
            return kbps > 0 ? $"≈ {kbps} kbps" : "";
        }
    }

    /// <summary>The usual bitrate nearest to what a file of one of these sizes would be for a song this long (0 if none is plausible).</summary>
    public static int Bitrate(IReadOnlyList<long> sizes, double seconds)
    {
        if (seconds < 20) return 0;
        for (var i = sizes.Count - 1; i >= 0; i--)
        {
            var kbps = sizes[i] * 8 / seconds / 1000;
            if (kbps is < 18 or > 360) continue;      // some other song's file (the next one is fetched early)
            return UsualBitrates.OrderBy(b => Math.Abs(b - kbps)).First();
        }
        return 0;
    }

    void NoteAudio(CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            var headers = e.Response.Headers;
            var type = headers.Contains("Content-Type") ? headers.GetHeader("Content-Type") : "";
            var host = new Uri(e.Request.Uri).Host;
            var audio = type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || host.StartsWith("audio", StringComparison.OrdinalIgnoreCase);
            if (!audio || !headers.Contains("Content-Range")) return;
            // "bytes 0-65535/4812345": the number after the slash is the whole file
            var range = headers.GetHeader("Content-Range");
            if (!long.TryParse(range[(range.LastIndexOf('/') + 1)..], out var total) || total < 100_000) return;
            _audioHosts.Add(host);
            if (_audioSizes.Count == 0 || _audioSizes[^1] != total) { _audioSizes.Add(total); if (_audioSizes.Count > 6) _audioSizes.RemoveAt(0); Changed?.Invoke(); }
        }
        catch { }
    }

    /// <summary>The page's latest report; changes raise <see cref="Changed"/>.</summary>
    public WebState State { get; private set; } = WebState.None;
    public event Action? Changed;
    /// <summary>The page started playing: it takes the controls from whatever had them.</summary>
    public event Action<WebServiceView>? TookOver;

    public WebServiceView(WebService service)
    {
        Service = service;
        _note.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        _note.Text = $"opening {service.Name}…";
        Button Tool(string glyph, string tip, Action click)
        {
            var b = new Button { Content = glyph, ToolTip = tip, Style = (Style)FindResource("IconBtn"), Width = 34, Height = 30, FontSize = 12 };
            b.Click += (_, _) => click();
            return b;
        }
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 4) };
        if (service.Video)
        {
            var back = new Button { Content = "‹  videos", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(6, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Back to your videos" };
            back.Click += (_, _) => App.Win.Navigate("videos");
            tools.Children.Add(back);
        }
        tools.Children.Add(Tool("", "Back", () => { if (_web.CoreWebView2?.CanGoBack == true) _web.CoreWebView2.GoBack(); }));
        tools.Children.Add(Tool("", "Forward", () => { if (_web.CoreWebView2?.CanGoForward == true) _web.CoreWebView2.GoForward(); }));
        tools.Children.Add(Tool("", "Reload", () => _web.CoreWebView2?.Reload()));
        tools.Children.Add(Tool("", service.Name + " home", () => _web.CoreWebView2?.Navigate(service.Url)));
        var caption = new TextBlock { Text = service.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 12 };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");
        tools.Children.Add(caption);

        var dock = new DockPanel();
        DockPanel.SetDock(tools, Dock.Top);
        dock.Children.Add(tools);
        dock.Children.Add(new Grid { Children = { _note, _web } });
        Content = dock;

        Loaded += async (_, _) => await StartAsync(service.Url);
        // local music starting pauses the page and takes the controls back, so the two never play over each other
        App.Player.StateChanged += () =>
        {
            if (!App.Player.IsPlaying) return;
            if (IsPlayingAudio) Pause();
            SetActive(false);
        };
        _poll.Tick += async (_, _) => await AskAsync();
    }

    async Task StartAsync(string? address)
    {
        if (_started) return;
        _started = true;
        try
        {
            // a browser profile per service: its sign-in lives here, apart from the others and from the ebook reader
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDir, Service.Profile));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(HookScript);
            if (Service.Id == "spotify") core.WebResourceResponseReceived += (_, e) => NoteAudio(e);
            // links that want a new window open in the same view
            core.NewWindowRequested += (_, a) => { a.Handled = true; core.Navigate(a.Uri); };
            // a video going full screen in the page takes the whole Phoenix window
            core.ContainsFullScreenElementChanged += (_, _) => App.Win.SetWebFullscreen(core.ContainsFullScreenElement);
            // the page starting to play pauses whatever Phoenix is playing, and Phoenix's controls then work the page
            core.IsDocumentPlayingAudioChanged += (_, _) =>
            {
                if (!core.IsDocumentPlayingAudio) return;
                if (App.Player.IsPlaying && !App.Player.RemoteIsActive) App.Player.Toggle();
                TakeControls();
            };
            core.NavigationCompleted += (_, a) =>
            {
                Ready = true;
                _note.Text = a.IsSuccess ? "" : $"{Service.Name} could not be reached. Check your connection, then use reload.";
                if (a.IsSuccess && !_covered) _web.Visibility = Visibility.Visible;
            };
            if (address != null) core.Navigate(address);
            _poll.Start();
        }
        catch (Exception ex)
        {
            _web.Visibility = Visibility.Collapsed;
            _note.Text = "This page needs the Microsoft Edge WebView2 Runtime, which could not be started.\n\n" + ex.Message;
        }
    }

    /// <summary>Self-test only: shows a stand-in page instead of the service.</summary>
    public async Task LoadTestPageAsync(string html)
    {
        await StartAsync(null);
        var done = new TaskCompletionSource();
        void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs a) => done.TrySetResult();
        _web.CoreWebView2.NavigationCompleted += OnDone;
        _web.CoreWebView2.NavigateToString(html);
        await Task.WhenAny(done.Task, Task.Delay(8000));
        _web.CoreWebView2.NavigationCompleted -= OnDone;
    }

    /// <summary>
    /// The browser view is its own window and always sits on top of anything Phoenix draws over it, so it is hidden
    /// while the now playing screen is open.
    /// </summary>
    public bool Covered
    {
        get => _covered;
        set { _covered = value; _web.Visibility = value ? Visibility.Hidden : Visibility.Visible; }
    }

    // ---------- Phoenix's controls working the page ----------

    /// <summary>The page is what is being listened to: it started playing last, and nothing else has started since.</summary>
    public bool Active => _active;
    public bool Playing => State.Playing;

    /// <summary>This page is now what is being listened to: Phoenix's controls work it from here on.</summary>
    public void TakeControls()
    {
        TookOver?.Invoke(this);
        SetActive(true);
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        Changed?.Invoke();
    }

    Task<string> RunAsync(string script) => _web.CoreWebView2 == null ? Task.FromResult("") : _web.CoreWebView2.ExecuteScriptAsync(script);

    async Task DoAsync(string command, string argument = "null")
    {
        try { await RunAsync($"window.__phx && window.__phx.run('{command}', {argument})"); } catch { }
        await Task.Delay(120);
        await AskAsync();
    }

    static string Num(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    public void Toggle() => _ = DoAsync("toggle");
    public void Next() => _ = DoAsync("next");
    public void Previous() => _ = DoAsync("previous");
    public void ToggleShuffle() => _ = DoAsync("shuffle");
    public void CycleRepeat() => _ = DoAsync("repeat");
    public void ToggleLike() => _ = DoAsync("like");
    public void SeekTo(double seconds) => _ = DoAsync("seek", Num(Math.Max(0, seconds)));
    public void SetVolume(double volume) => _ = DoAsync("volume", Num(Math.Clamp(volume, 0, 1)));
    /// <summary>Stops the page playing. When Windows says the page is making sound it is stopped for certain, even if its player cannot be seen from the page's own content.</summary>
    public void Pause() => _ = DoAsync(IsPlayingAudio ? "stop" : "pause");

    public bool Muted
    {
        get => _web.CoreWebView2?.IsMuted == true;
        set { if (_web.CoreWebView2 != null) _web.CoreWebView2.IsMuted = value; }
    }

    /// <summary>
    /// Runs in every page before its own scripts. It notes the media-session actions the page registers (play, pause,
    /// next, previous, seek), so Phoenix can call them, and holds the helpers that find and work a page's controls.
    /// </summary>
    const string HookScript = """
        (function () {
          if (window.__phx) return;
          var handlers = {};
          try {
            var ms = navigator.mediaSession, original = ms.setActionHandler.bind(ms);
            ms.setActionHandler = function (action, handler) { if (handler) handlers[action] = handler; else delete handlers[action]; return original(action, handler); };
          } catch (e) { }
          var q = function (s, root) { try { return (root || document).querySelector(s); } catch (e) { return null; } };
          var bar = function () { return q('[data-testid="now-playing-bar"]') || q('footer') || document.body; };
          // a control by the name the service gives it, else by what its label says
          var button = function (testId, label) {
            var b = q('[data-testid="' + testId + '"]');
            if (b) return b;
            var all = bar().querySelectorAll('button,[role="button"],[role="checkbox"],[role="switch"]');
            for (var i = 0; i < all.length; i++) { var l = all[i].getAttribute('aria-label') || all[i].getAttribute('title') || ''; if (label.test(l)) return all[i]; }
            return null;
          };
          var range = function (testId, pick) {
            var r = q('[data-testid="' + testId + '"] input[type="range"]');
            if (r) return r;
            var all = bar().querySelectorAll('input[type="range"]');
            for (var i = 0; i < all.length; i++) if (pick(parseFloat(all[i].max))) return all[i];
            return null;
          };
          var seekRange = function () { return range('playback-progressbar', function (max) { return max > 1000; }); };
          var volumeRange = function () { return range('volume-bar', function (max) { return max <= 1; }); };
          // sets a slider the way a person dragging it would, so the page's own code notices
          var setRange = function (r, value) {
            var setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
            setter.call(r, String(value));
            r.dispatchEvent(new Event('input', { bubbles: true }));
            r.dispatchEvent(new Event('change', { bubbles: true }));
          };
          // some players (Spotify) make their audio element in code and never put it on the page, so note every one that plays
          var made = [];
          try {
            var play = HTMLMediaElement.prototype.play;
            HTMLMediaElement.prototype.play = function () { if (made.indexOf(this) < 0) made.push(this); return play.apply(this, arguments); };
          } catch (e) { }
          var everyMedia = function () {
            var all = Array.prototype.slice.call(document.querySelectorAll('audio,video'));
            for (var i = 0; i < made.length; i++) if (all.indexOf(made[i]) < 0) all.push(made[i]);
            return all;
          };
          var media = function () {
            var all = everyMedia(), best = null;
            for (var i = 0; i < all.length; i++) {
              if (!all[i].paused) return all[i];
              if (!best || (all[i].duration || 0) > (best.duration || 0)) best = all[i];
            }
            return best;
          };
          var shuffleBtn = function () { return button('control-button-shuffle', /shuffle/i); };
          var repeatBtn = function () { return button('control-button-repeat', /repeat/i); };
          // Spotify's like button reads "Add to Liked Songs" before and "Add to playlist" after, and carries aria-checked
          var likeBtn = function () {
            var b = q('[data-testid="now-playing-widget"] [data-testid="add-button"]');
            if (b) return b;
            var all = bar().querySelectorAll('button[aria-checked]');
            for (var i = 0; i < all.length; i++) { if (/liked songs|add to playlist|add to (your )?(library|favou?rites)|remove from (your )?(library|favou?rites)/i.test(all[i].getAttribute('aria-label') || '')) return all[i]; }
            return button('add-button', /liked songs|add to (your )?(library|favou?rites)|remove from (your )?(library|favou?rites)/i);
          };
          var label = function (b) { return (b.getAttribute('aria-label') || b.getAttribute('title') || '').toLowerCase(); };
          var checked = function (b) {
            if (!b) return null;
            var c = b.getAttribute('aria-checked') || b.getAttribute('aria-pressed');
            if (c) return c;
            return /^(disable|remove|turn off)/.test(label(b)) ? 'true' : 'false';
          };
          // a control that is switched on is drawn in the service's highlight colour (Spotify's green)
          var lit = function (b) {
            try { var c = getComputedStyle(b).color.match(/[0-9.]+/g); return !!c && +c[1] > +c[0] + 40 && +c[1] > +c[2] + 40; } catch (e) { return false; }
          };
          // what the button's picture looks like, to tell the plain shuffle arrows from the smart-shuffle ones
          var icon = function (b) {
            var paths = b.querySelectorAll('svg path'), s = paths.length + ':';
            for (var i = 0; i < paths.length; i++) s += (paths[i].getAttribute('d') || '').length + ',';
            return s;
          };
          var plainShuffle = null;
          // off, on, or "smart" (Spotify's third shuffle setting)
          var shuffleState = function () {
            var b = shuffleBtn();
            if (!b) return null;
            var l = label(b), c = b.getAttribute('aria-checked') || b.getAttribute('aria-pressed') || '';
            if (/smart/.test(l) && /^(disable|turn off)/.test(l)) return 'smart';
            if (c === 'mixed') return 'smart';
            var on = c === 'true' || lit(b) || /^(disable|turn off)/.test(l);
            if (!on) { plainShuffle = icon(b); return 'false'; }
            if (/smart/.test(l) && /^(enable|turn on)/.test(l)) return 'true';      // "enable smart shuffle" is offered while plain shuffle is on
            if (plainShuffle && icon(b) !== plainShuffle) return 'smart';
            return 'true';
          };
          // Spotify words the repeat button by what a press will do next: "Enable repeat", "Enable repeat one", "Disable repeat"
          var repeatState = function () {
            var b = repeatBtn();
            if (!b) return null;
            var l = label(b);
            if (/^enable repeat one/.test(l)) return 'true';
            if (/^disable repeat/.test(l)) return lit(b) || b.getAttribute('aria-checked') !== 'true' ? 'mixed' : 'true';
            if (/^enable repeat/.test(l)) return 'false';
            return checked(b);
          };
          var clock = function (s) {
            var e = q(s); if (!e) return 0;
            var parts = (e.textContent || '').replace('-', '').trim().split(':'), total = 0;
            for (var i = 0; i < parts.length; i++) { var n = parseFloat(parts[i]); if (isNaN(n)) return 0; total = total * 60 + n; }
            return total;
          };
          var call = function (action, details) { if (!handlers[action]) return false; try { handlers[action](details || { action: action }); return true; } catch (e) { return false; } };
          var click = function (b) { if (!b) return false; b.click(); return true; };

          window.__phx = {
            handlers: handlers,
            run: function (command, value) {
              var m = media(), r;
              switch (command) {
                case 'toggle':
                  if (click(q('[data-testid="control-button-playpause"]'))) return 'button';
                  if (m && !m.paused) return call('pause') ? 'session' : (m.pause(), 'media');
                  if (call('play')) return 'session';
                  if (m) { m.play(); return 'media'; }
                  return 'none';
                case 'pause':
                  if (!m || m.paused) return 'idle';
                  if (click(q('[data-testid="control-button-playpause"]'))) return 'button';
                  return call('pause') ? 'session' : (m.pause(), 'media');
                case 'stop':
                  // Phoenix knows the page is making sound: stop it, whichever way works here
                  if (m && m.paused) m = null;
                  if (click(q('[data-testid="control-button-playpause"]'))) return 'button';
                  if (call('pause')) return 'session';
                  everyMedia().forEach(function (x) { x.pause(); });
                  return 'media';
                case 'next': return click(q('[data-testid="control-button-skip-forward"]')) ? 'button' : call('nexttrack') ? 'session' : 'none';
                case 'previous': return click(q('[data-testid="control-button-skip-back"]')) ? 'button' : call('previoustrack') ? 'session' : 'none';
                case 'seek':
                  if (call('seekto', { action: 'seekto', seekTime: value, fastSeek: false })) return 'session';
                  if ((r = seekRange())) { setRange(r, parseFloat(r.max) > 100000 ? Math.round(value * 1000) : value); return 'slider'; }
                  if (m && isFinite(m.duration)) { m.currentTime = value; return 'media'; }
                  return 'none';
                case 'volume':
                  if ((r = volumeRange())) setRange(r, value);
                  var all = everyMedia();
                  for (var i = 0; i < all.length; i++) all[i].volume = value;
                  return r ? 'slider' : 'media';
                case 'shuffle': return click(shuffleBtn()) ? 'button' : 'none';
                case 'repeat': return click(repeatBtn()) ? 'button' : 'none';
                case 'like': return click(likeBtn()) ? 'button' : 'none';
              }
              return 'unknown';
            },
            state: function () {
              var md = navigator.mediaSession && navigator.mediaSession.metadata, m = media(), art = '';
              if (md && md.artwork && md.artwork.length) art = md.artwork[md.artwork.length - 1].src || '';
              var r = seekRange(), pos = 0, dur = 0;
              if (r && parseFloat(r.max) > 0) { var scale = parseFloat(r.max) > 100000 ? 1000 : 1; pos = parseFloat(r.value) / scale; dur = parseFloat(r.max) / scale; }
              if (!dur) { pos = clock('[data-testid="playback-position"]'); dur = clock('[data-testid="playback-duration"]'); }
              if (!dur && m && isFinite(m.duration)) { pos = m.currentTime; dur = m.duration; }
              return JSON.stringify({
                title: md ? (md.title || '') : '', artist: md ? (md.artist || '') : '', album: md ? (md.album || '') : '', art: art,
                playing: !!(m && !m.paused), pos: pos || 0, dur: dur || 0,
                shuffle: shuffleState(), repeat: repeatState(), liked: checked(likeBtn()),
                canSeek: !!(handlers.seekto || r || (m && isFinite(m.duration) && m.duration > 0)),
                canNext: !!(q('[data-testid="control-button-skip-forward"]') || handlers.nexttrack),
                canPrevious: !!(q('[data-testid="control-button-skip-back"]') || handlers.previoustrack),
                hasMedia: !!m, page: document.title || ''
              });
            },
            // the names of the controls in the page's player bar, for working out why a control is not found (no song or account details)
            controls: function () {
              var out = [], all = bar().querySelectorAll('button,[role="button"],[role="checkbox"],[role="switch"],input[type="range"]');
              for (var i = 0; i < all.length && i < 80; i++) {
                var e = all[i];
                out.push([e.tagName.toLowerCase(), e.getAttribute('data-testid') || '', e.getAttribute('role') || e.getAttribute('type') || '',
                  e.getAttribute('aria-checked') || e.getAttribute('aria-pressed') || '', e.tagName === 'INPUT' ? ('max ' + e.max) : (e.getAttribute('aria-label') || '').replace(/[^A-Za-z ]/g, '').slice(0, 30),
                  e.tagName === 'INPUT' ? '' : (lit(e) ? 'lit' : 'unlit') + ' icon ' + icon(e)].join(' | '));
              }
              return JSON.stringify({ handlers: Object.keys(handlers), controls: out, read: 'shuffle ' + shuffleState() + ', repeat ' + repeatState() + ', liked ' + checked(likeBtn()) });
            }
          };
        })();
        """;

    /// <summary>Asks the page what it is doing now.</summary>
    public async Task AskAsync()
    {
        if (_asking || _web.CoreWebView2 == null) return;
        _asking = true;
        try
        {
            var next = Parse(await RunAsync("window.__phx ? window.__phx.state() : ''"), IsPlayingAudio);
            if (next != State) { State = next; Changed?.Invoke(); }
            if (_active && !_dumped && next.Playing) { _dumped = true; await NoteControlsAsync(); }
        }
        catch { }
        finally { _asking = false; }
    }

    /// <summary>
    /// Once per run, writes down which controls the page's player bar has (their technical names only) in
    /// web-controls-&lt;service&gt;.txt in the data folder, so a control that does not respond can be tracked down.
    /// </summary>
    async Task NoteControlsAsync()
    {
        try
        {
            var inner = JsonSerializer.Deserialize<string>(await RunAsync("window.__phx.controls()"));
            if (string.IsNullOrEmpty(inner)) return;
            using var doc = JsonDocument.Parse(inner);
            var lines = new List<string> { $"{Service.Name} player controls, noted {DateTime.Now:u}", "media session actions: " + string.Join(", ", doc.RootElement.GetProperty("handlers").EnumerateArray().Select(x => x.GetString())), "" };
            lines.Insert(2, "read as: " + (doc.RootElement.TryGetProperty("read", out var read) ? read.GetString() : "") + (BitrateText != "" ? ", " + BitrateText : ""));
            lines.Insert(3, "audio came from: " + string.Join(", ", _audioHosts));
            lines.AddRange(doc.RootElement.GetProperty("controls").EnumerateArray().Select(x => x.GetString() ?? ""));
            await File.WriteAllLinesAsync(Path.Combine(AppPaths.DataDir, $"web-controls-{Service.Id}.txt"), lines);
        }
        catch { }
    }

    /// <summary>Reads the page's answer (a JSON string inside a JSON string, as the browser hands script results back).</summary>
    public static WebState Parse(string raw, bool audible)
    {
        try
        {
            var inner = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(inner)) return WebState.None;
            using var doc = JsonDocument.Parse(inner);
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            double D(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            bool B(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            bool? Flag(string n) => S(n) switch { "true" or "mixed" => true, "false" => false, _ => null };
            int? shuffle = S("shuffle") switch { "true" => 1, "smart" or "mixed" => 2, "false" => 0, _ => null };
            var title = S("title");
            // "true" repeats the list, "mixed" repeats one song
            int? repeat = S("repeat") switch { "true" => 1, "mixed" => 2, "false" => 0, _ => null };
            return new WebState(title != "" ? title : S("page"), S("artist"), S("album"), S("art"), audible || B("playing"), D("pos"), D("dur"),
                shuffle, repeat, Flag("liked"), B("canSeek"), B("canNext"), B("canPrevious"), B("hasMedia"));
        }
        catch { return WebState.None; }
    }
}
