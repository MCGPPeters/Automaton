// =============================================================================
// ArticlePreview — Reusable Article Card Component
// =============================================================================
// Renders a single article preview card as seen on the home page and
// profile page feeds. Includes author info, favorite button, title,
// description, tags, and "Read more..." link.
// =============================================================================

using Abies.DOM;
using static Abies.Html.Attributes;
using static Abies.Html.Elements;
using static Abies.Html.Events;

namespace Abies.Conduit.App.Views;

/// <summary>
/// Renders article preview cards for feed lists.
/// </summary>
public static class ArticlePreview
{
    /// <summary>
    /// Renders a single article preview card.
    /// </summary>
    public static Node Render(ArticlePreviewData article) =>
        div([class_("article-preview")],
        [
            div([class_("article-meta")],
            [
                a([href($"/profile/{article.Author.Username}")],
                [
                    img([src(article.Author.Image ?? "https://api.realworld.io/images/smiley-cyrus.jpeg")])
                ]),
                div([class_("info")],
                [
                    a([href($"/profile/{article.Author.Username}"), class_("author")],
                        [text(article.Author.Username)]),
                    span([class_("date")],
                        [text(article.CreatedAt.ToString("MMMM d, yyyy"))])
                ]),
                FavoriteButton(article)
            ]),
            a([href($"/article/{article.Slug}"), class_("preview-link")],
            [
                h1([], [text(article.Title)]),
                p([], [text(article.Description)]),
                span([], [text("Read more...")]),
                TagList(article.TagList)
            ])
        ]);

    /// <summary>
    /// Renders the favorite button with heart icon and count.
    /// </summary>
    private static Node FavoriteButton(ArticlePreviewData article)
    {
        var btnClass = article.Favorited
            ? "btn btn-primary btn-sm pull-xs-right"
            : "btn btn-outline-primary btn-sm pull-xs-right";

        return button(
            [class_(btnClass), onclick(new ToggleFavorite(article.Slug, article.Favorited))],
            [
                i([class_("ion-heart")], []),
                text($" {article.FavoritesCount}")
            ]);
    }

    /// <summary>
    /// Renders the tag list for an article preview.
    /// </summary>
    private static Node TagList(IReadOnlyList<string> tags) =>
        ul([class_("tag-list")],
            tags.Select(tag =>
                li([class_("tag-default tag-pill tag-outline")],
                    [text(tag)])).ToArray());

    /// <summary>
    /// Renders a list of article previews, or a loading/empty message.
    /// </summary>
    public static Node List(IReadOnlyList<ArticlePreviewData> articles, bool isLoading)
    {
        if (isLoading)
            return div([class_("article-preview")], [text("Loading articles...")]);

        if (articles.Count == 0)
            return div([class_("article-preview")], [text("No articles are here... yet.")]);

        return div([], articles.Select(Render).ToArray());
    }

    /// <summary>
    /// Renders pagination buttons.
    /// </summary>
    public static Node Pagination(int articlesCount, int currentPage, int articlesPerPage)
    {
        var pageCount = (int)Math.Ceiling((double)articlesCount / articlesPerPage);
        if (pageCount <= 1)
            return new Empty();

        return nav([],
        [
            ul([class_("pagination")],
                Enumerable.Range(1, pageCount).Select(page =>
                {
                    var activeClass = page == currentPage
                        ? "page-item active"
                        : "page-item";
                    return li([class_(activeClass)],
                    [
                        a([class_("page-link"), href(""), onclick(new PageChanged(page))],
                            [text(page.ToString())])
                    ]);
                }).ToArray())
        ]);
    }
}
