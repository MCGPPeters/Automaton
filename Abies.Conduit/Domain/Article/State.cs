// =============================================================================
// Article State — The Write Model for the Article Aggregate
// =============================================================================
// Immutable record representing the current state of an article, built by
// folding ArticleEvents via the Decider's Transition function.
// =============================================================================

using Abies.Conduit.Domain.Shared;

namespace Abies.Conduit.Domain.Article;

/// <summary>
/// The current state of an article aggregate.
/// </summary>
/// <param name="Id">The article's unique identifier.</param>
/// <param name="Slug">The URL-friendly slug.</param>
/// <param name="Title">The article title.</param>
/// <param name="Description">The article description/summary.</param>
/// <param name="Body">The article body (Markdown).</param>
/// <param name="Tags">The set of tags.</param>
/// <param name="AuthorId">The user who created the article.</param>
/// <param name="FavoritedBy">The set of users who favorited this article.</param>
/// <param name="Comments">The list of comments, keyed by CommentId.</param>
/// <param name="CreatedAt">When the article was created.</param>
/// <param name="UpdatedAt">When the article was last updated.</param>
/// <param name="Published">Whether the article has been created (initial state is unpublished).</param>
/// <param name="Deleted">Whether the article has been deleted.</param>
public record ArticleState(
    ArticleId Id,
    Slug Slug,
    Title Title,
    Description Description,
    Body Body,
    IReadOnlySet<Tag> Tags,
    UserId AuthorId,
    IReadOnlySet<UserId> FavoritedBy,
    IReadOnlyDictionary<CommentId, Comment> Comments,
    Timestamp CreatedAt,
    Timestamp UpdatedAt,
    bool Published,
    bool Deleted)
{
    /// <summary>
    /// The initial (unpublished) article state.
    /// </summary>
    public static readonly ArticleState Initial = new(
        Id: new ArticleId(Guid.Empty),
        Slug: new Slug(string.Empty),
        Title: new Title(string.Empty),
        Description: new Description(string.Empty),
        Body: new Body(string.Empty),
        Tags: new HashSet<Tag>(),
        AuthorId: new UserId(Guid.Empty),
        FavoritedBy: new HashSet<UserId>(),
        Comments: new Dictionary<CommentId, Comment>(),
        CreatedAt: new Timestamp(DateTimeOffset.MinValue),
        UpdatedAt: new Timestamp(DateTimeOffset.MinValue),
        Published: false,
        Deleted: false);
}
