using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Webkit;
using Halcyon.Services;

namespace Halcyon.Droid;

/// <summary>
/// The remote control for Phoenix on the PC: what is playing there, with play, pause, skip, volume and the queue.
/// The PC itself serves the page (so the phone and the PC can never disagree about what the buttons do); this only
/// shows it, full screen, and hands it the pairing code.
/// </summary>
public static class RemoteScreen
{
    /// <summary>Only the paired PC's own remote page may be shown here; anything else stays out.</summary>
    class OnlyThePc(string origin) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) =>
            !(request?.Url?.ToString() ?? "").StartsWith(origin, StringComparison.OrdinalIgnoreCase);

        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            if (request?.IsForMainFrame != true || view == null) return;
            view.LoadDataWithBaseURL(null,
                "<body style='background:#0b0b0b;color:#a6a6a6;font:16px sans-serif;padding:28px'><p style='color:#f2f2f2;font-size:22px'>Can't reach Phoenix on the PC</p>" +
                "<p>Check that Phoenix is open there, that “Let my phone sync with and control this PC” is ticked in its settings, and that the phone is on the same Wi-Fi.</p></body>",
                "text/html", "utf-8", null);
        }
    }

    public static void Open(MainActivity a)
    {
        var url = SyncClient.RemoteUrl;
        var origin = url[..url.IndexOf("/remote", StringComparison.Ordinal)];
        var dialog = new Dialog(a, Android.Resource.Style.ThemeBlackNoTitleBar);
        var web = new WebView(a);
        web.SetBackgroundColor(Color.Rgb(11, 11, 11));
        web.Settings.JavaScriptEnabled = true;    // the page is the PC's own; it is what draws the buttons
        web.Settings.DomStorageEnabled = true;    // it remembers the pairing code
        web.Settings.AllowFileAccess = false;
        web.Settings.AllowContentAccess = false;
        web.Settings.SetSupportZoom(false);
        web.SetWebViewClient(new OnlyThePc(origin));
        dialog.SetContentView(web, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        dialog.DismissEvent += (_, _) => { try { web.StopLoading(); web.Destroy(); } catch { } };
        dialog.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        dialog.Show();
        web.LoadUrl(url);
    }
}
