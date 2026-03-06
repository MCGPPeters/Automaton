// =============================================================================
// Commands — HTTP and Side-Effect Commands for the Conduit Frontend
// =============================================================================
// Commands describe side effects (API calls, storage, etc.) that the runtime
// dispatches to the Interpreter. Each command is a record implementing Command.
//
// The Interpreter pattern-matches on these, executes the HTTP call, and returns
// response Messages back into the MVU loop.
// =============================================================================

namespace Abies.Conduit.Wasm;

/// <summary>
/// All Conduit-specific commands extend this marker.
/// </summary>
public interface ConduitCommand : Command;

// ─── Article Commands ─────────────────────────────────────────────────────────

/// <summary>Fetch a paginated list of articles with optional filters.</summary>
public sealed record FetchArticles(
    string ApiUrl,
    string? Token,
    int Limit = 10,
    int Offset = 0,
    string? Tag = null,
    string? Author = null,
    string? Favorited = null
) : ConduitCommand;

/// <summary>Fetch the authenticated user's feed.</summary>
public sealed record FetchFeed(
    string ApiUrl,
    string Token,
    int Limit = 10,
    int Offset = 0
) : ConduitCommand;

/// <summary>Fetch a single article by slug.</summary>
public sealed record FetchArticle(string ApiUrl, string? Token, string Slug) : ConduitCommand;

/// <summary>Favorite an article.</summary>
public sealed record FavoriteArticle(string ApiUrl, string Token, string Slug) : ConduitCommand;

/// <summary>Unfavorite an article.</summary>
public sealed record UnfavoriteArticle(string ApiUrl, string Token, string Slug) : ConduitCommand;

/// <summary>Delete an article by slug.</summary>
public sealed record DeleteArticleCommand(string ApiUrl, string Token, string Slug) : ConduitCommand;

// ─── Comment Commands ─────────────────────────────────────────────────────────

/// <summary>Fetch comments for an article.</summary>
public sealed record FetchComments(string ApiUrl, string? Token, string Slug) : ConduitCommand;

/// <summary>Add a comment to an article.</summary>
public sealed record AddComment(string ApiUrl, string Token, string Slug, string Body) : ConduitCommand;

/// <summary>Delete a comment from an article.</summary>
public sealed record DeleteCommentCommand(string ApiUrl, string Token, string Slug, Guid CommentId) : ConduitCommand;

// ─── Tag Commands ─────────────────────────────────────────────────────────────

/// <summary>Fetch popular tags.</summary>
public sealed record FetchTags(string ApiUrl) : ConduitCommand;

// ─── Auth Commands ────────────────────────────────────────────────────────────

/// <summary>Login with email and password.</summary>
public sealed record LoginUser(string ApiUrl, string Email, string Password) : ConduitCommand;

/// <summary>Register a new user.</summary>
public sealed record RegisterUser(string ApiUrl, string Username, string Email, string Password) : ConduitCommand;

// ─── Profile Commands ─────────────────────────────────────────────────────────

/// <summary>Fetch a user profile.</summary>
public sealed record FetchProfile(string ApiUrl, string? Token, string Username) : ConduitCommand;

/// <summary>Follow a user.</summary>
public sealed record FollowUser(string ApiUrl, string Token, string Username) : ConduitCommand;

/// <summary>Unfollow a user.</summary>
public sealed record UnfollowUser(string ApiUrl, string Token, string Username) : ConduitCommand;

// ─── Settings Commands ────────────────────────────────────────────────────────

/// <summary>Update the current user's settings.</summary>
public sealed record UpdateUser(
    string ApiUrl,
    string Token,
    string Image,
    string Username,
    string Bio,
    string Email,
    string? Password
) : ConduitCommand;

// ─── Editor Commands ──────────────────────────────────────────────────────────

/// <summary>Create a new article.</summary>
public sealed record CreateArticle(
    string ApiUrl,
    string Token,
    string Title,
    string Description,
    string Body,
    IReadOnlyList<string> TagList
) : ConduitCommand;

/// <summary>Update an existing article.</summary>
public sealed record UpdateArticle(
    string ApiUrl,
    string Token,
    string Slug,
    string Title,
    string Description,
    string Body,
    IReadOnlyList<string> TagList
) : ConduitCommand;
