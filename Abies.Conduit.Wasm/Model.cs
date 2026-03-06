// =============================================================================
// Model — Application State for the Conduit Frontend
// =============================================================================
// Single immutable model record following the Elm Architecture pattern.
// The Page discriminated union determines which view is rendered.
// Sub-models hold page-specific state (form fields, loaded data, etc.).
//
// Architecture Decision Records:
// - ADR-002: Pure Functional Programming
// - ADR-003: Virtual DOM
// =============================================================================

namespace Abies.Conduit.Wasm;

// ─── Top-Level Model ──────────────────────────────────────────────────────────

/// <summary>
/// The complete application state. Immutable — updated via <c>with</c> expressions
/// in the Transition function.
/// </summary>
/// <param name="Page">The current page being displayed.</param>
/// <param name="Session">The authenticated user session, if any.</param>
/// <param name="ApiUrl">The base URL for the Conduit API.</param>
public sealed record Model(
    Page Page,
    Session? Session,
    string ApiUrl);

/// <summary>
/// An authenticated user session. Stored in the model after login/register.
/// </summary>
/// <param name="Token">The JWT token for API authentication.</param>
/// <param name="Username">The authenticated user's username.</param>
/// <param name="Email">The authenticated user's email.</param>
/// <param name="Bio">The authenticated user's bio.</param>
/// <param name="Image">The authenticated user's profile image URL.</param>
public sealed record Session(
    string Token,
    string Username,
    string Email,
    string Bio,
    string? Image);

// ─── Page Discriminated Union ─────────────────────────────────────────────────

/// <summary>
/// Discriminated union representing all application pages.
/// The current page determines which view is rendered.
/// </summary>
public abstract record Page
{
    private Page() { }

    /// <summary>Home page with article feeds and popular tags.</summary>
    public sealed record Home(HomeModel Data) : Page;

    /// <summary>Sign in page.</summary>
    public sealed record Login(LoginModel Data) : Page;

    /// <summary>Sign up page.</summary>
    public sealed record Register(RegisterModel Data) : Page;

    /// <summary>Article detail page with comments.</summary>
    public sealed record Article(ArticleModel Data) : Page;

    /// <summary>User settings page.</summary>
    public sealed record Settings(SettingsModel Data) : Page;

    /// <summary>Article editor page (create or edit).</summary>
    public sealed record Editor(EditorModel Data) : Page;

    /// <summary>User profile page.</summary>
    public sealed record Profile(ProfileModel Data) : Page;

    /// <summary>Page not found.</summary>
    public sealed record NotFound : Page;
}

// ─── Feed Selection ───────────────────────────────────────────────────────────

/// <summary>
/// Which feed tab is active on the home page.
/// </summary>
public enum FeedTab
{
    /// <summary>Global feed — all articles.</summary>
    Global,
    /// <summary>Your feed — articles from followed authors (requires auth).</summary>
    Your,
    /// <summary>Tag-filtered feed.</summary>
    Tag
}

// ─── Page Sub-Models ──────────────────────────────────────────────────────────

/// <summary>Home page state.</summary>
public sealed record HomeModel(
    FeedTab ActiveTab,
    string? SelectedTag,
    IReadOnlyList<ArticlePreviewData> Articles,
    int ArticlesCount,
    int CurrentPage,
    IReadOnlyList<string> PopularTags,
    bool IsLoading);

/// <summary>Login form state.</summary>
public sealed record LoginModel(
    string Email,
    string Password,
    IReadOnlyList<string> Errors,
    bool IsSubmitting);

/// <summary>Register form state.</summary>
public sealed record RegisterModel(
    string Username,
    string Email,
    string Password,
    IReadOnlyList<string> Errors,
    bool IsSubmitting);

/// <summary>Article detail page state.</summary>
public sealed record ArticleModel(
    string Slug,
    ArticleData? Article,
    IReadOnlyList<CommentData> Comments,
    string CommentBody,
    bool IsLoading);

/// <summary>Settings form state (placeholder for future implementation).</summary>
public sealed record SettingsModel(
    string Image,
    string Username,
    string Bio,
    string Email,
    string Password,
    IReadOnlyList<string> Errors,
    bool IsSubmitting);

/// <summary>Article editor state (placeholder for future implementation).</summary>
public sealed record EditorModel(
    string? Slug,
    string Title,
    string Description,
    string Body,
    string TagInput,
    IReadOnlyList<string> TagList,
    IReadOnlyList<string> Errors,
    bool IsSubmitting);

/// <summary>Profile page state (placeholder for future implementation).</summary>
public sealed record ProfileModel(
    string Username,
    ProfileData? Profile,
    IReadOnlyList<ArticlePreviewData> Articles,
    int ArticlesCount,
    int CurrentPage,
    bool ShowFavorites,
    bool IsLoading);

// ─── Shared Data Types ────────────────────────────────────────────────────────

/// <summary>Author profile info displayed on articles and comments.</summary>
public sealed record AuthorData(
    string Username,
    string Bio,
    string? Image,
    bool Following);

/// <summary>Article preview data for list views (excludes body per spec).</summary>
public sealed record ArticlePreviewData(
    string Slug,
    string Title,
    string Description,
    IReadOnlyList<string> TagList,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Favorited,
    int FavoritesCount,
    AuthorData Author);

/// <summary>Full article data including body.</summary>
public sealed record ArticleData(
    string Slug,
    string Title,
    string Description,
    string Body,
    IReadOnlyList<string> TagList,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Favorited,
    int FavoritesCount,
    AuthorData Author);

/// <summary>Comment data.</summary>
public sealed record CommentData(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Body,
    AuthorData Author);

/// <summary>User profile data.</summary>
public sealed record ProfileData(
    string Username,
    string Bio,
    string? Image,
    bool Following);

// ─── Constants ────────────────────────────────────────────────────────────────

/// <summary>
/// Application-wide constants.
/// </summary>
public static class Constants
{
    /// <summary>Number of articles per page in list views.</summary>
    public const int ArticlesPerPage = 10;
}
