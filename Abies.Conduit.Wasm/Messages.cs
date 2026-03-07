// =============================================================================
// Messages — All MVU Messages for the Conduit Frontend
// =============================================================================
// Messages are immutable records describing events that change application state.
// The Transition function pattern-matches on these types.
//
// Organized by domain:
//   - Navigation messages (URL changes)
//   - Form input messages (field changes)
//   - Form submission messages (login, register)
//   - API response messages (data loaded, errors)
//   - UI interaction messages (tab change, pagination, favorite, follow)
// =============================================================================

namespace Abies.Conduit.Wasm;

/// <summary>
/// All application-specific messages implement this marker interface.
/// </summary>
public interface ConduitMessage : Message;

// ─── Navigation ───────────────────────────────────────────────────────────────

// UrlChanged and UrlRequest are provided by the Abies framework.
// We use them directly in the Transition function.

// ─── Form Input ───────────────────────────────────────────────────────────────

/// <summary>Login form field changed.</summary>
public sealed record LoginEmailChanged(string Value) : ConduitMessage;

/// <summary>Login form field changed.</summary>
public sealed record LoginPasswordChanged(string Value) : ConduitMessage;

/// <summary>Login form submitted.</summary>
public sealed record LoginSubmitted : ConduitMessage;

/// <summary>Register form field changed.</summary>
public sealed record RegisterUsernameChanged(string Value) : ConduitMessage;

/// <summary>Register form field changed.</summary>
public sealed record RegisterEmailChanged(string Value) : ConduitMessage;

/// <summary>Register form field changed.</summary>
public sealed record RegisterPasswordChanged(string Value) : ConduitMessage;

/// <summary>Register form submitted.</summary>
public sealed record RegisterSubmitted : ConduitMessage;

/// <summary>Comment body changed on article page.</summary>
public sealed record CommentBodyChanged(string Value) : ConduitMessage;

/// <summary>Comment form submitted.</summary>
public sealed record CommentSubmitted : ConduitMessage;

// ─── Settings Form ────────────────────────────────────────────────────────────

/// <summary>Settings form image URL changed.</summary>
public sealed record SettingsImageChanged(string Value) : ConduitMessage;

/// <summary>Settings form username changed.</summary>
public sealed record SettingsUsernameChanged(string Value) : ConduitMessage;

/// <summary>Settings form bio changed.</summary>
public sealed record SettingsBioChanged(string Value) : ConduitMessage;

/// <summary>Settings form email changed.</summary>
public sealed record SettingsEmailChanged(string Value) : ConduitMessage;

/// <summary>Settings form password changed.</summary>
public sealed record SettingsPasswordChanged(string Value) : ConduitMessage;

/// <summary>Settings form submitted.</summary>
public sealed record SettingsSubmitted : ConduitMessage;

// ─── Editor Form ──────────────────────────────────────────────────────────────

/// <summary>Editor form title changed.</summary>
public sealed record EditorTitleChanged(string Value) : ConduitMessage;

/// <summary>Editor form description changed.</summary>
public sealed record EditorDescriptionChanged(string Value) : ConduitMessage;

/// <summary>Editor form body changed.</summary>
public sealed record EditorBodyChanged(string Value) : ConduitMessage;

/// <summary>Editor form tag input changed.</summary>
public sealed record EditorTagInputChanged(string Value) : ConduitMessage;

/// <summary>Editor form add tag.</summary>
public sealed record EditorAddTag : ConduitMessage;

/// <summary>Editor form tag input key pressed (for Enter detection).</summary>
public sealed record EditorTagKeyDown(string Key) : ConduitMessage;

/// <summary>Editor form remove tag.</summary>
public sealed record EditorRemoveTag(string Tag) : ConduitMessage;

/// <summary>Editor form submitted.</summary>
public sealed record EditorSubmitted : ConduitMessage;

// ─── Profile Interaction ──────────────────────────────────────────────────────

/// <summary>Profile page tab changed (my articles vs favorited).</summary>
public sealed record ProfileTabChanged(bool ShowFavorites) : ConduitMessage;

// ─── UI Interaction ───────────────────────────────────────────────────────────

/// <summary>Home page feed tab changed.</summary>
public sealed record FeedTabChanged(FeedTab Tab, string? Tag = null) : ConduitMessage;

/// <summary>Pagination page changed.</summary>
public sealed record PageChanged(int PageNumber) : ConduitMessage;

/// <summary>Toggle article favorite.</summary>
public sealed record ToggleFavorite(string Slug, bool Favorited) : ConduitMessage;

/// <summary>Toggle follow author.</summary>
public sealed record ToggleFollow(string Username, bool Following) : ConduitMessage;

/// <summary>Delete article.</summary>
public sealed record DeleteArticle(string Slug) : ConduitMessage;

/// <summary>Delete comment.</summary>
public sealed record DeleteComment(string Slug, Guid CommentId) : ConduitMessage;

// ─── API Responses ────────────────────────────────────────────────────────────

/// <summary>Articles loaded from API.</summary>
public sealed record ArticlesLoaded(IReadOnlyList<ArticlePreviewData> Articles, int ArticlesCount) : ConduitMessage;

/// <summary>Single article loaded from API.</summary>
public sealed record ArticleLoaded(ArticleData Article) : ConduitMessage;

/// <summary>Comments loaded from API.</summary>
public sealed record CommentsLoaded(IReadOnlyList<CommentData> Comments) : ConduitMessage;

/// <summary>Popular tags loaded from API.</summary>
public sealed record TagsLoaded(IReadOnlyList<string> Tags) : ConduitMessage;

/// <summary>User authenticated (login or register succeeded).</summary>
public sealed record UserAuthenticated(Session Session) : ConduitMessage;

/// <summary>Profile loaded from API.</summary>
public sealed record ProfileLoaded(ProfileData Profile) : ConduitMessage;

/// <summary>Article favorited/unfavorited response.</summary>
public sealed record FavoriteToggled(ArticlePreviewData Article) : ConduitMessage;

/// <summary>Author followed/unfollowed response.</summary>
public sealed record FollowToggled(ProfileData Profile) : ConduitMessage;

/// <summary>Comment added response.</summary>
public sealed record CommentAdded(CommentData Comment) : ConduitMessage;

/// <summary>Comment deleted response.</summary>
public sealed record CommentDeleted(Guid CommentId) : ConduitMessage;

/// <summary>Article deleted response.</summary>
public sealed record ArticleDeleted : ConduitMessage;

/// <summary>User settings updated response.</summary>
public sealed record UserUpdated(Session Session) : ConduitMessage;

/// <summary>Article created/updated response — navigate to the article.</summary>
public sealed record ArticleSaved(string Slug) : ConduitMessage;

/// <summary>API error response.</summary>
public sealed record ApiError(IReadOnlyList<string> Errors) : ConduitMessage;

/// <summary>Logout the current user.</summary>
public sealed record Logout : ConduitMessage;
