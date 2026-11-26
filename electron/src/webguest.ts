// Runs in a streaming service's page before the page's own scripts (the preload of every <webview>). It is the same
// hook the Windows app puts in its WebView2 pages: it notes the media-session actions the page registers (play, pause,
// next, previous, seek), so Phoenix's player bar can call them, and holds the helpers that find and work a page's own
// controls (shuffle, repeat, like). It runs in the page itself, not in this preload's own world, so it can see them.
// Nothing is sent anywhere: Phoenix's window asks window.__phx for the state and calls window.__phx.run.
import { webFrame } from "electron";

const HOOK = `
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
`;

void webFrame.executeJavaScript(HOOK).catch(() => {});
