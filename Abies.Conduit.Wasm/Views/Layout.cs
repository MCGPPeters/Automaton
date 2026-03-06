// =============================================================================
// Layout — Shared Navigation Bar and Footer
// =============================================================================
// Pure view functions for the Conduit chrome: top navigation and footer.
// Composed around page content in the main View function.
// =============================================================================

using Abies.DOM;
using static Abies.Html.Attributes;
using static Abies.Html.Elements;

namespace Abies.Conduit.Wasm.Views;

/// <summary>
/// Shared layout components: navigation bar and footer.
/// </summary>
public static class Layout
{
    /// <summary>
    /// Renders the full page layout: navbar + content + footer.
    /// </summary>
    public static Node Page(Page currentPage, Session? session, Node content) =>
        div([],
        [
            Navbar(currentPage, session),
            content,
            Footer()
        ]);

    /// <summary>
    /// Renders the top navigation bar with conditional links based on auth state.
    /// </summary>
    private static Node Navbar(Page currentPage, Session? session) =>
        nav([class_("navbar navbar-light")],
        [
            div([class_("container")],
            [
                a([class_("navbar-brand"), href("/")], [text("conduit")]),
                ul([class_("nav navbar-nav pull-xs-right")],
                    NavLinks(currentPage, session))
            ])
        ]);

    /// <summary>
    /// Builds navigation links based on whether the user is logged in.
    /// </summary>
    private static Node[] NavLinks(Page currentPage, Session? session)
    {
        if (session is not null)
            return
            [
                NavLink("Home", "/", currentPage is Page.Home),
                NavLink("\u2003New Article", "/editor",
                    currentPage is Page.Editor, iconClass: "ion-compose"),
                NavLink("\u2003Settings", "/settings",
                    currentPage is Page.Settings, iconClass: "ion-gear-a"),
                NavLink(session.Username, $"/profile/{session.Username}",
                    currentPage is Page.Profile p && p.Data.Username == session.Username)
            ];

        return
        [
            NavLink("Home", "/", currentPage is Page.Home),
            NavLink("Sign in", "/login", currentPage is Page.Login),
            NavLink("Sign up", "/register", currentPage is Page.Register)
        ];
    }

    /// <summary>
    /// Renders a single navigation link item.
    /// </summary>
    private static Node NavLink(string label, string url, bool isActive, string? iconClass = null)
    {
        var activeClass = isActive ? "nav-link active" : "nav-link";
        var children = iconClass is not null
            ? new Node[] { i([class_(iconClass)], []), text(label) }
            : [text(label)];

        return li([class_("nav-item")],
        [
            a([class_(activeClass), href(url)], children)
        ]);
    }

    /// <summary>
    /// Renders the page footer.
    /// </summary>
    private static Node Footer() =>
        footer([],
        [
            div([class_("container")],
            [
                a([href("/"), class_("logo-font")], [text("conduit")]),
                span([class_("attribution")],
                [
                    text("An interactive learning project from "),
                    a([href("https://thinkster.io")], [text("Thinkster")]),
                    text(". Code & design licensed under MIT.")
                ])
            ])
        ]);
}
