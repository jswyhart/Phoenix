namespace Halcyon.Core;

/// <summary>
/// The page layout every EPUB chapter is shown with, shared by the apps' web-view readers. It must run before the
/// chapter's own content: it lays the text out in screen-wide columns and turns pages by sliding the body sideways.
/// It talks to the app only through messages: {type:'ready',path}, {type:'state',page,pages} and {type:'edge',dir};
/// the app answers 'ready' with halcyon.init({fontSize,start,bg,fg}) and may call halcyon.next()/prev()/font(px).
/// </summary>
public static class ReaderScript
{
    /// <summary>WebView2 on Windows.</summary>
    public const string PostWebView2 = "m => window.chrome.webview.postMessage(m)";
    /// <summary>WKWebView on macOS, with a script message handler named "halcyon".</summary>
    public const string PostWebKit = "m => window.webkit.messageHandlers.halcyon.postMessage(m)";
    /// <summary>android.webkit.WebView, with a JavaScript interface named "HalcyonHost" that has post(String json).</summary>
    public const string PostAndroid = "m => HalcyonHost.post(JSON.stringify(m))";

    /// <param name="post">A JavaScript function expression that hands one message object to the app.</param>
    /// <param name="relayoutOnHeight">
    /// False for phones: a soft keyboard only changes the height, and re-flowing the pages then would lose the place.
    /// Rotating still changes the width and still re-lays the pages out.
    /// </param>
    public static string Build(string post, bool relayoutOnHeight = true) =>
        Script.Replace("__POST__", post).Replace("__HEIGHT__", relayoutOnHeight ? "true" : "false");

    const string Script = """
        (() => {
          if (window.top !== window || window.halcyon) return;
          const post = __POST__;
          let S = null, page = 0, pages = 1, moved = false, wheelAt = 0, resizeTimer = 0, touch = null, swiped = 0;
          const frac = () => pages > 1 ? page / (pages - 1) : 0;

          function show(p) {
            page = Math.max(0, Math.min(pages - 1, p));
            document.body.style.transform = `translateX(${-page * window.innerWidth}px)`;
            post({ type: 'state', page, pages });
          }
          function go(p) {
            moved = true;
            if (p < 0) post({ type: 'edge', dir: -1 });
            else if (p >= pages) post({ type: 'edge', dir: 1 });
            else show(p);
          }
          function layout(at) {
            if (!S || !document.body) return;
            const W = window.innerWidth, two = W >= 1150;
            const M = two ? 56 : W < 600 ? 22 : Math.max(36, Math.min(170, (W - 720) / 2));
            const V = W < 600 ? 28 : 44;
            const colW = (two ? W / 2 : W) - 2 * M;
            let st = document.getElementById('halcyon-style');
            if (!st) { st = document.createElement('style'); st.id = 'halcyon-style'; (document.head || document.documentElement).appendChild(st); }
            st.textContent = `
              html { height: 100% !important; overflow: hidden !important; background: ${S.bg} !important; }
              body { margin: 0 !important; padding: ${V}px ${M}px !important; box-sizing: border-box !important; height: 100vh !important;
                     width: auto !important; max-width: none !important; min-width: 0 !important;
                     column-width: ${colW}px !important; column-gap: ${2 * M}px !important; column-fill: auto !important;
                     font-size: ${S.fontSize}px !important; line-height: 1.6 !important;
                     background: ${S.bg} !important; color: ${S.fg} !important; overflow: visible !important;
                     -webkit-text-size-adjust: 100% !important; text-size-adjust: 100% !important; }
              body * { color: inherit !important; background-color: transparent !important; max-width: 100% !important; }
              img, svg, video { max-height: calc(100vh - 120px) !important; object-fit: contain; break-inside: avoid; }
              p { orphans: 2; widows: 2; }
              ::-webkit-scrollbar { display: none; }`;
            requestAnimationFrame(() => {
              const width = Math.max(document.body.scrollWidth, document.documentElement.scrollWidth);
              pages = Math.max(1, Math.ceil(width / W - 0.02));
              show(at < 0 ? pages - 1 : Math.round(at * (pages - 1)));
            });
          }

          window.halcyon = {
            init(s) { S = s; layout(S.start); },
            next() { go(page + 1); },
            prev() { go(page - 1); },
            font(px) { if (S) { S.fontSize = px; layout(frac()); } }
          };
          document.addEventListener('DOMContentLoaded', () => post({ type: 'ready', path: location.pathname }));
          // Pictures arriving late change the page count: settle again, staying where the reader was sent.
          window.addEventListener('load', () => { if (S) layout(moved ? frac() : S.start); });
          let lastW = window.innerWidth;
          window.addEventListener('resize', () => {
            if (!__HEIGHT__ && window.innerWidth === lastW) return;
            lastW = window.innerWidth;
            clearTimeout(resizeTimer); resizeTimer = setTimeout(() => layout(frac()), 120);
          });
          window.addEventListener('keydown', e => {
            if (['ArrowRight', 'PageDown', ' '].includes(e.key)) { go(page + 1); e.preventDefault(); }
            else if (['ArrowLeft', 'PageUp'].includes(e.key)) { go(page - 1); e.preventDefault(); }
            else if (e.key === 'Home') { go(0); e.preventDefault(); }
            else if (e.key === 'End') { go(pages - 1); e.preventDefault(); }
          });
          window.addEventListener('wheel', e => {
            e.preventDefault();
            const now = Date.now();
            if (now - wheelAt < 260 || Math.abs(e.deltaY) < 4) return;
            wheelAt = now;
            go(page + (e.deltaY > 0 ? 1 : -1));
          }, { passive: false });
          // Touch screens: a sideways swipe turns the page.
          window.addEventListener('touchstart', e => { touch = e.touches.length === 1 ? { x: e.touches[0].clientX, y: e.touches[0].clientY } : null; }, { passive: true });
          window.addEventListener('touchmove', e => { if (touch) e.preventDefault(); }, { passive: false });
          window.addEventListener('touchend', e => {
            if (!touch) return;
            const dx = e.changedTouches[0].clientX - touch.x, dy = e.changedTouches[0].clientY - touch.y;
            touch = null;
            if (Math.abs(dx) > 45 && Math.abs(dx) > Math.abs(dy) * 1.5) { swiped = Date.now(); go(page + (dx < 0 ? 1 : -1)); }
          }, { passive: true });
          window.addEventListener('click', e => {
            if (Date.now() - swiped < 400 || e.target.closest('a')) return;
            if (e.clientX > window.innerWidth * 0.72) go(page + 1);
            else if (e.clientX < window.innerWidth * 0.28) go(page - 1);
          });
        })();
        """;
}
