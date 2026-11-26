using System.Windows;
using System.Windows.Controls;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>
/// The streaming services placed under a collection, shown as more tabs in that collection's own row of tabs, in
/// the same style and on the same line ("movies  tv shows  collections  all  youtube  netflix").
/// </summary>
public static class ServiceTabs
{
    const string Marker = "service-tab";

    /// <summary>Puts the services placed under <paramref name="collection"/> into a row of tabs, just after its own tabs.</summary>
    public static List<RadioButton> Fill(Panel row, string collection, string? current = null)
    {
        foreach (var old in row.Children.OfType<RadioButton>().Where(rb => Equals(rb.Uid, Marker)).ToList()) row.Children.Remove(old);
        // after the row's own tabs (its other radio buttons), before anything else on the line
        var at = 0;
        for (var i = 0; i < row.Children.Count; i++) if (row.Children[i] is RadioButton) at = i + 1;
        var made = new List<RadioButton>();
        foreach (var service in WebServices.Under(collection))
        {
            var target = service;
            var tab = new RadioButton
            {
                Content = service.Name.ToLowerInvariant(), Tag = service.Page, Uid = Marker, GroupName = "services-" + collection + row.GetHashCode(),
                Style = (Style)Application.Current.FindResource("NavTab"), IsChecked = current == service.Page, ToolTip = $"{service.Name}, shown here in Phoenix"
            };
            tab.Click += (_, _) =>
            {
                if (current != target.Page) tab.IsChecked = false;   // the tab stays unlit: the service opens on a page of its own
                App.Win.Navigate(target.Page);
            };
            row.Children.Insert(at++, tab);
            made.Add(tab);
        }
        return made;
    }

    /// <summary>Keeps a page's row of tabs up to date: when the page is shown, and when services are moved.</summary>
    public static void Attach(FrameworkElement page, Panel row, string collection, Action? changed = null)
    {
        void Refresh()
        {
            Fill(row, collection);
            changed?.Invoke();
        }
        page.IsVisibleChanged += (_, _) => { if (page.IsVisible) Refresh(); };
        WebServices.Changed += () => page.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    /// <summary>The service tabs a row holds right now (for the self-test).</summary>
    public static List<string> In(Panel row) => row.Children.OfType<RadioButton>().Where(rb => Equals(rb.Uid, Marker)).Select(rb => (string)rb.Tag).ToList();
}

/// <summary>A collection tab of your own: the streaming services you put in it, as tabs, with a card for each.</summary>
public class CustomTabView : UserControl
{
    readonly string _page;
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 14) };
    readonly WrapPanel _cards = new() { Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _note = Make.Text("", 14, "FgDim", new Thickness(8, 6, 28, 0), wrap: true);

    public string TabPage => _page;
    public List<string> Services => ServiceTabs.In(_row);

    public CustomTabView(string page)
    {
        _page = page;
        var body = new StackPanel { Margin = new Thickness(20, 8, 0, 0) };
        body.Children.Add(_row);
        body.Children.Add(_note);
        body.Children.Add(_cards);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = body };
        ServiceTabs.Attach(this, _row, page, ShowCards);
    }

    void ShowCards()
    {
        var services = WebServices.Under(_page);
        _cards.Children.Clear();
        _note.Text = services.Count == 0
            ? "Nothing in this tab yet. In settings → servers → streaming, switch a service on and choose this tab under “show in”."
            : "";
        _note.Visibility = services.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var service in services)
        {
            var target = service;
            var card = new Border { Width = 230, Height = 130, Margin = new Thickness(0, 0, 14, 14), Padding = new Thickness(16, 12, 16, 12) };
            card.SetResourceReference(Border.BackgroundProperty, "BgAlt");
            var name = new TextBlock { Text = service.Name.ToLowerInvariant(), FontSize = 30, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.FontFamilyProperty, "HeadFont");
            name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var stack = Make.Column(name, Make.Text(service.Url.Replace("https://", "").TrimEnd('/'), 12, "FgFaint", new Thickness(0, 6, 0, 0)));
            card.Child = stack;
            var button = new Button { Style = Make.Style("BareBtn"), Content = card, ToolTip = "Open " + service.Name };
            button.Click += (_, _) => App.Win.Navigate(target.Page);
            _cards.Children.Add(button);
        }
    }
}
