// =============================================================================
// Home Page — Global Feed, Your Feed, Tags
// =============================================================================
// The landing page of the Conduit app with feed tabs, article list,
// popular tags sidebar, and pagination.
// =============================================================================

using Abies.DOM;
using static Abies.Html.Attributes;
using static Abies.Html.Elements;
using static Abies.Html.Events;

namespace Abies.Conduit.Wasm.Pages;

/// <summary>
/// Home page view — feed tabs, article list, and popular tags.
/// </summary>
public static class Home
{
    /// <summary>
    /// Renders the home page.
    /// </summary>
    public static Node View(HomeModel model, Session? session) =>
        div([class_("home-page")],
        [
            Banner(),
            div([class_("container page")],
            [
                div([class_("row")],
                [
                    div([class_("col-md-9")],
                    [
                        FeedTabs(model.ActiveTab, session),
                        Views.ArticlePreview.List(model.Articles, model.IsLoading),
                        Views.ArticlePreview.Pagination(
                            model.ArticlesCount, model.CurrentPage, Constants.ArticlesPerPage)
                    ]),
                    div([class_("col-md-3")],
                    [
                        Sidebar(model.PopularTags)
                    ])
                ])
            ])
        ]);

    /// <summary>
    /// Renders the banner/jumbotron section.
    /// </summary>
    private static Node Banner() =>
        div([class_("banner")],
        [
            div([class_("container")],
            [
                h1([class_("logo-font")], [text("conduit")]),
                p([], [text("A place to share your knowledge.")])
            ])
        ]);

    /// <summary>
    /// Renders feed toggle tabs.
    /// </summary>
    private static Node FeedTabs(FeedTab activeTab, Session? session)
    {
        var tabs = new List<Node>();

        if (session is not null)
            tabs.Add(Tab("Your Feed", FeedTab.Your, activeTab));

        tabs.Add(Tab("Global Feed", FeedTab.Global, activeTab));

        if (activeTab is FeedTab.Tag)
            tabs.Add(Tab("# ", FeedTab.Tag, activeTab));

        return div([class_("feed-toggle")],
        [
            ul([class_("nav nav-pills outline-active")], tabs.ToArray())
        ]);
    }

    /// <summary>
    /// Renders a single feed tab.
    /// </summary>
    private static Node Tab(string label, FeedTab tab, FeedTab activeTab)
    {
        var activeClass = tab == activeTab ? "nav-link active" : "nav-link";
        return li([class_("nav-item")],
        [
            a([class_(activeClass), href(""), onclick(new FeedTabChanged(tab))],
                [text(label)])
        ]);
    }

    /// <summary>
    /// Renders the popular tags sidebar.
    /// </summary>
    private static Node Sidebar(IReadOnlyList<string> tags) =>
        div([class_("sidebar")],
        [
            p([], [text("Popular Tags")]),
            tags.Count == 0
                ? text("Loading tags...")
                : div([class_("tag-list")],
                    tags.Select(tag =>
                        a([href(""), class_("tag-pill tag-default"),
                           onclick(new FeedTabChanged(FeedTab.Tag, tag))],
                            [text(tag)])).ToArray())
        ]);
}
