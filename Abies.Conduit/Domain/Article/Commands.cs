// =============================================================================
// Article Commands — Intent Representations
// =============================================================================
// Commands are what users want to do. The Decider validates them against
// the current state and produces events (or rejects them with errors).
//
// Commands carry pre-validated constrained types where possible.
// Validation of raw input happens at the API boundary (anti-corruption layer).
// =============================================================================

using Abies.Conduit.Domain.Shared;
using Automaton;

namespace Abies.Conduit.Domain.Article;

/// <summary>
/// Commands representing user intent for the Article aggregate.
/// </summary>
public interface ArticleCommand
{
    /// <summary>
    /// Create a new article.
    /// </summary>
    /// <param name="Id">The pre-generated article ID.</param>
    /// <param name="Title">The validated title.</param>
    /// <param name="Description">The validated description.</param>
    /// <param name="Body">The validated body.</param>
    /// <param name="Tags">The set of validated tags.</param>
    /// <param name="AuthorId">The creating user's ID.</param>
    /// <param name="CreatedAt">The timestamp of creation (injected as capability).</param>
    record CreateArticle(
        ArticleId Id,
        Title Title,
        Description Description,
        Body Body,
        IReadOnlySet<Tag> Tags,
        UserId AuthorId,
        Timestamp CreatedAt) : ArticleCommand;

    /// <summary>
    /// Update an existing article's content.
    /// </summary>
    /// <remarks>
    /// All fields are optional — only provided fields are updated.
    /// Uses <see cref="Option{T}"/> to distinguish "not provided" from "set to empty".
    /// Only the article's author may update it (enforced by Decider).
    /// </remarks>
    /// <param name="Title">New title, if changing.</param>
    /// <param name="Description">New description, if changing.</param>
    /// <param name="Body">New body, if changing.</param>
    /// <param name="RequesterId">The user requesting the update (for authorization).</param>
    /// <param name="UpdatedAt">The timestamp of the update.</param>
    record UpdateArticle(
        Option<Title> Title,
        Option<Description> Description,
        Option<Body> Body,
        UserId RequesterId,
        Timestamp UpdatedAt) : ArticleCommand;

    /// <summary>
    /// Delete an article. Only the author may delete it.
    /// </summary>
    /// <param name="RequesterId">The user requesting deletion (for authorization).</param>
    record DeleteArticle(UserId RequesterId) : ArticleCommand;

    /// <summary>
    /// Add a comment to the article.
    /// </summary>
    /// <param name="CommentId">The pre-generated comment ID.</param>
    /// <param name="AuthorId">The commenting user's ID.</param>
    /// <param name="Body">The validated comment body.</param>
    /// <param name="CreatedAt">The timestamp of the comment.</param>
    record AddComment(
        CommentId CommentId,
        UserId AuthorId,
        CommentBody Body,
        Timestamp CreatedAt) : ArticleCommand;

    /// <summary>
    /// Delete a comment from the article. Only the comment author may delete it.
    /// </summary>
    /// <param name="CommentId">The ID of the comment to delete.</param>
    /// <param name="RequesterId">The user requesting deletion (for authorization).</param>
    record DeleteComment(CommentId CommentId, UserId RequesterId) : ArticleCommand;

    /// <summary>
    /// Favorite an article.
    /// </summary>
    /// <param name="UserId">The user favoriting the article.</param>
    record FavoriteArticle(UserId UserId) : ArticleCommand;

    /// <summary>
    /// Unfavorite an article.
    /// </summary>
    /// <param name="UserId">The user unfavoriting the article.</param>
    record UnfavoriteArticle(UserId UserId) : ArticleCommand;
}
