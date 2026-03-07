// =============================================================================
// Article Errors — Domain-Specific Failure Cases
// =============================================================================
// Errors are values, not exceptions. Each case represents a specific
// business rule violation that the Decider can produce.
// =============================================================================

using Abies.Conduit.Domain.Shared;

namespace Abies.Conduit.Domain.Article;

/// <summary>
/// Errors produced when Article command validation fails.
/// </summary>
public interface ArticleError
{
    /// <summary>A constrained type validation failed (title length, body required, etc.).</summary>
    record Validation(string Message) : ArticleError;

    /// <summary>The article has already been created (duplicate CreateArticle command).</summary>
    record AlreadyPublished : ArticleError;

    /// <summary>The article has not been created yet (commands before CreateArticle).</summary>
    record NotPublished : ArticleError;

    /// <summary>The article has been deleted and cannot accept further commands.</summary>
    record AlreadyDeleted : ArticleError;

    /// <summary>The requester is not the author and cannot modify the article.</summary>
    record NotAuthor(UserId RequesterId) : ArticleError;

    /// <summary>The user has already favorited this article.</summary>
    record AlreadyFavorited(UserId UserId) : ArticleError;

    /// <summary>The user has not favorited this article.</summary>
    record NotFavorited(UserId UserId) : ArticleError;

    /// <summary>The specified comment was not found on this article.</summary>
    record CommentNotFound(CommentId CommentId) : ArticleError;

    /// <summary>The requester is not the comment author and cannot delete it.</summary>
    record NotCommentAuthor(CommentId CommentId, UserId RequesterId) : ArticleError;
}
