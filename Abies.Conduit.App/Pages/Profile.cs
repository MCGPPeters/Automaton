// =============================================================================
// Profile Page — User Profile with Articles
// =============================================================================
// Displays a user's profile information, follow button (or edit settings link
// for the current user), and tabbed article lists (my articles / favorited).
// Matches the RealWorld spec template.
// =============================================================================

using Abies.DOM;
using static Abies.Html.Attributes;
using static Abies.Html.Elements;
using static Abies.Html.Events;

namespace Abies.Conduit.App.Pages;

/// <summary>
/// Profile page view — user profile with article tabs.
/// </summary>
public static class Profile
{
    /// <summary>
    /// Renders the profile page.
    /// </summary>
    public static Node View(ProfileModel model, Session? session)
    {
        if (model.IsLoading || model.Profile is null)
            return div([class_("profile-page")],
            [
                div([class_("container")],
                [
                    div([class_("row")],
                    [
                        div([class_("col-xs-12 col-md-10 offset-md-1")],
                            [text("Loading profile...")])
                    ])
                ])
            ]);

        var profile = model.Profile;
        return div([class_("profile-page")],
        [
            UserInfo(profile, session),
            div([class_("container")],
            [
                div([class_("row")],
                [
                    div([class_("col-xs-12 col-md-10 offset-md-1")],
                    [
                        ArticleTabs(model.ShowFavorites),
                        Views.ArticlePreview.List(model.Articles, false),
                        Views.ArticlePreview.Pagination(
                            model.ArticlesCount, model.CurrentPage, Constants.ArticlesPerPage)
                    ])
                ])
            ])
        ]);
    }

    /// <summary>
    /// Renders the user info banner.
    /// </summary>
    private static Node UserInfo(ProfileData profile, Session? session) =>
        div([class_("user-info")],
        [
            div([class_("container")],
            [
                div([class_("row")],
                [
                    div([class_("col-xs-12 col-md-10 offset-md-1")],
                    [
                        img([src(profile.Image ?? "https://api.realworld.io/images/smiley-cyrus.jpeg"),
                             class_("user-img")]),
                        h4([], [text(profile.Username)]),
                        p([], [text(profile.Bio)]),
                        ..ActionButton(profile, session)
                    ])
                ])
            ])
        ]);

    /// <summary>
    /// Renders the follow/unfollow button or edit settings link based on auth state.
    /// </summary>
    private static Node[] ActionButton(ProfileData profile, Session? session)
    {
        if (session is not null && session.Username == profile.Username)
            return
            [
                a([class_("btn btn-sm btn-outline-secondary action-btn"),
                   href("/settings")],
                [
                    i([class_("ion-gear-a")], []),
                    text("\u00A0 Edit Profile Settings")
                ])
            ];

        if (session is null)
            return [];

        var btnClass = profile.Following
            ? "btn btn-sm btn-secondary action-btn"
            : "btn btn-sm btn-outline-secondary action-btn";
        var label = profile.Following
            ? $"\u00A0 Unfollow {profile.Username}"
            : $"\u00A0 Follow {profile.Username}";

        return
        [
            button([class_(btnClass),
                onclick(new ToggleFollow(profile.Username, profile.Following))],
            [
                i([class_("ion-plus-round")], []),
                text(label)
            ])
        ];
    }

    /// <summary>
    /// Renders the article tabs (My Articles / Favorited Articles).
    /// </summary>
    private static Node ArticleTabs(bool showFavorites) =>
        div([class_("articles-toggle")],
        [
            ul([class_("nav nav-pills outline-active")],
            [
                li([class_("nav-item")],
                [
                    a([class_(showFavorites ? "nav-link" : "nav-link active"),
                       href(""),
                       onclick(new ProfileTabChanged(false))],
                        [text("My Articles")])
                ]),
                li([class_("nav-item")],
                [
                    a([class_(showFavorites ? "nav-link active" : "nav-link"),
                       href(""),
                       onclick(new ProfileTabChanged(true))],
                        [text("Favorited Articles")])
                ])
            ])
        ]);
}
