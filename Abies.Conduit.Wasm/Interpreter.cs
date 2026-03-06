// =============================================================================
// Interpreter — HTTP Command Interpreter for the Conduit Frontend
// =============================================================================
// Pattern-matches on ConduitCommand records and executes HTTP calls against
// the Conduit REST API. Returns Message arrays back into the MVU loop.
//
// Uses System.Net.Http.HttpClient which is available in browser-wasm via
// the browser's Fetch API.
//
// JSON deserialization uses the API's DTO types directly since they match
// the wire format. The interpreter maps DTOs → domain data types used
// by the model.
// =============================================================================

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using Automaton;

namespace Abies.Conduit.Wasm;

/// <summary>
/// HTTP interpreter that converts commands into API calls and feedback messages.
/// </summary>
public static class ConduitInterpreter
{
    private static readonly HttpClient _http = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Interprets a command by dispatching to the appropriate HTTP handler.
    /// Returns feedback messages that flow back into the MVU Transition function.
    /// </summary>
    public static async ValueTask<Result<Message[], PipelineError>> Interpret(Command command)
    {
        try
        {
            Message[] messages = command switch
            {
                FetchArticles cmd => await HandleFetchArticles(cmd),
                FetchFeed cmd => await HandleFetchFeed(cmd),
                FetchArticle cmd => await HandleFetchArticle(cmd),
                FetchComments cmd => await HandleFetchComments(cmd),
                FetchTags cmd => await HandleFetchTags(cmd),
                LoginUser cmd => await HandleLogin(cmd),
                RegisterUser cmd => await HandleRegister(cmd),
                FetchProfile cmd => await HandleFetchProfile(cmd),
                FavoriteArticle cmd => await HandleFavorite(cmd),
                UnfavoriteArticle cmd => await HandleUnfavorite(cmd),
                FollowUser cmd => await HandleFollow(cmd),
                UnfollowUser cmd => await HandleUnfollow(cmd),
                AddComment cmd => await HandleAddComment(cmd),
                DeleteCommentCommand cmd => await HandleDeleteComment(cmd),
                DeleteArticleCommand cmd => await HandleDeleteArticle(cmd),
                _ => []
            };

            return Result<Message[], PipelineError>.Ok(messages);
        }
        catch (Exception ex)
        {
            return Result<Message[], PipelineError>.Ok(
                [new ApiError([$"Network error: {ex.Message}"])]);
        }
    }

    // ─── Article Handlers ─────────────────────────────────────────────────

    [RequiresUnreferencedCode("Calls System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<T>(JsonSerializerOptions, CancellationToken)")]
    private static async Task<Message[]> HandleFetchArticles(FetchArticles cmd)
    {
        var query = BuildArticleQuery(cmd.Limit, cmd.Offset, cmd.Tag, cmd.Author, cmd.Favorited);
        using var request = CreateRequest(HttpMethod.Get, $"{cmd.ApiUrl}/api/articles{query}", cmd.Token);
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return [new ApiError(await ReadErrors(response))];

        var dto = await response.Content.ReadFromJsonAsync<MultipleArticlesDto>(_jsonOptions);
        return dto is null ? [] :
        [
            new ArticlesLoaded(
                dto.Articles.Select(MapArticlePreview).ToList(),
                dto.ArticlesCount)
        ];
    }

    [RequiresUnreferencedCode("Calls System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<T>(JsonSerializerOptions, CancellationToken)")]
    private static async Task<Message[]> HandleFetchFeed(FetchFeed cmd)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"{cmd.ApiUrl}/api/articles/feed?limit={cmd.Limit}&offset={cmd.Offset}",
            cmd.Token);
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return [new ApiError(await ReadErrors(response))];

        var dto = await response.Content.ReadFromJsonAsync<MultipleArticlesDto>(_jsonOptions);
        return dto is null ? [] :
        [
            new ArticlesLoaded(
                dto.Articles.Select(MapArticlePreview).ToList(),
                dto.ArticlesCount)
        ];
    }

    private static async Task<Message[]> HandleFetchArticle(FetchArticle cmd)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{cmd.ApiUrl}/api/articles/{cmd.Slug}", cmd.Token);
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return [new ApiError(await ReadErrors(response))];

        var dto = await response.Content.ReadFromJsonAsync<SingleArticleDto>(_jsonOptions);
        return dto?.Article is null ? [] : [new ArticleLoaded(MapArticle(dto.Article))];
    }

    private static async Task<Message[]> HandleFavorite(FavoriteArticle cmd)
    {
        using var request = CreateRequest(
            HttpMethod.Post, $"{cmd.ApiUrl}/api/articles/{cmd.Slug}/favorite", cmd.Token);
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return [new ApiError(await ReadErrors(response))];

        var dto = await response.Content.ReadFromJsonAsync<SingleArticleDto>(_jsonOptions);
        return dto?.Article is null ? [] : [new FavoriteToggled(MapArticlePreview(dto.Article))];
    }

    private static async Task<Message[]> HandleUnfavorite(UnfavoriteArticle cmd)
    {
        using var request = CreateRequest(
            HttpMethod.Delete, $"{cmd.ApiUrl}/api/articles/{cmd.Slug}/favorite", cmd.Token);
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return [new ApiError(await ReadErrors(response))];

        var dto = await response.Content.ReadFromJsonAsync<SingleArticleDto>(_jsonOptions);
        return dto?.Article is null ? [] : [new FavoriteToggled(MapArticlePreview(dto.Article))];
    }

    private static async Task<Message[]> HandleDeleteArticle(DeleteArticleCommand cmd)
    {
        using var request = CreateRequest(
            HttpMethod.Delete, $"{cmd.ApiUrl}/api/articles/{cmd.Slug}", cmd.Token);
        using var response = await _http.SendAsync(request);

        return response.IsSuccessStatusCode
            ? [new ArticleDeleted()]
            : [new ApiError(await ReadErrors(response))];
    }

    // ─── Comment Handlers ─────────────────────────────────────────────────

    private static async Task<Message[]> HandleFetchComments(FetchComments cmd)
    {
        using var request = CreateRequest(
            HttpMethod.Get, $"{cmd.ApiUrl}/api/articles/{cmd.Slug}/comments", cmd.Token);
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return [new ApiError(await ReadErrors(response))];

        var dto = await response.Content.ReadFromJsonAsync<MultipleCommentsDto>(_jsonOptions);
        return dto is null ? [] :
        [
            new CommentsLoaded(dto.Comments.Select(MapComment).ToList())
        ];
    }

    private static async Task<Message[]> HandleAddComment(AddComment cmd)
    {
        using var request = CreateRequest(
            HttpMethod.Post, $"{cmd.ApiUrl}/api/articles/{cmd.Slug}/comments", cmd.Token);
        request.Content = JsonContent.Create(
            new { comment = new { body = cmd.Body } }, options: _jsonOptions);
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return [new ApiError(await ReadErrors(response))];

        var dto = await response.Content.ReadFromJsonAsync<SingleCommentDto>(_jsonOptions);
        return dto?.Comment is null ? [] : [new CommentAdded(MapComment(dto.Comment))];
    }

    private static async Task<Message[]> HandleDeleteComment(DeleteCommentCommand cmd)
    {
        using var request = CreateRequest(
            HttpMethod.Delete,
            $"{cmd.ApiUrl}/api/articles/{cmd.Slug}/comments/{cmd.CommentId}",
            cmd.Token);
        using var response = await _http.SendAsync(request);

        return response.IsSuccessStatusCode
            ? [new CommentDeleted(cmd.CommentId)]
            : [new ApiError(await ReadErrors(response))];
    }

    // ─── Tag Handlers ─────────────────────────────────────────────────────

    private static async Task<Message[]> HandleFetchTags(FetchTags cmd)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{cmd.ApiUrl}/api/tags", null);
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return [new ApiError(await ReadErrors(response))];

        var dto = await response.Content.ReadFromJsonAsync<TagsDto>(_jsonOptions);
        return dto is null ? [] : [new TagsLoaded(dto.Tags)];
    }

    // ─── Auth Handlers ────────────────────────────────────────────────────

    private static async Task<Message[]> HandleLogin(LoginUser cmd)
    {
        using var request = CreateRequest(HttpMethod.Post, $"{cmd.ApiUrl}/api/users/login", null);
        request.Content = JsonContent.Create(
            new { user = new { email = cmd.Email, password = cmd.Password } },
            options: _jsonOptions);
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return [new ApiError(await ReadErrors(response))];

        var dto = await response.Content.ReadFromJsonAsync<UserResponseDto>(_jsonOptions);
        return dto?.User is null ? [] :
        [
            new UserAuthenticated(new Session(
                dto.User.Token, dto.User.Username, dto.User.Email, dto.User.Bio, dto.User.Image))
        ];
    }

    private static async Task<Message[]> HandleRegister(RegisterUser cmd)
    {
        using var request = CreateRequest(HttpMethod.Post, $"{cmd.ApiUrl}/api/users", null);
        request.Content = JsonContent.Create(
            new { user = new { username = cmd.Username, email = cmd.Email, password = cmd.Password } },
            options: _jsonOptions);
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return [new ApiError(await ReadErrors(response))];

        var dto = await response.Content.ReadFromJsonAsync<UserResponseDto>(_jsonOptions);
        return dto?.User is null ? [] :
        [
            new UserAuthenticated(new Session(
                dto.User.Token, dto.User.Username, dto.User.Email, dto.User.Bio, dto.User.Image))
        ];
    }

    // ─── Profile Handlers ─────────────────────────────────────────────────

    private static async Task<Message[]> HandleFetchProfile(FetchProfile cmd)
    {
        using var request = CreateRequest(
            HttpMethod.Get, $"{cmd.ApiUrl}/api/profiles/{cmd.Username}", cmd.Token);
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return [new ApiError(await ReadErrors(response))];

        var dto = await response.Content.ReadFromJsonAsync<ProfileResponseDto>(_jsonOptions);
        return dto?.Profile is null ? [] : [new ProfileLoaded(MapProfile(dto.Profile))];
    }

    private static async Task<Message[]> HandleFollow(FollowUser cmd)
    {
        using var request = CreateRequest(
            HttpMethod.Post, $"{cmd.ApiUrl}/api/profiles/{cmd.Username}/follow", cmd.Token);
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return [new ApiError(await ReadErrors(response))];

        var dto = await response.Content.ReadFromJsonAsync<ProfileResponseDto>(_jsonOptions);
        return dto?.Profile is null ? [] : [new FollowToggled(MapProfile(dto.Profile))];
    }

    private static async Task<Message[]> HandleUnfollow(UnfollowUser cmd)
    {
        using var request = CreateRequest(
            HttpMethod.Delete, $"{cmd.ApiUrl}/api/profiles/{cmd.Username}/follow", cmd.Token);
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return [new ApiError(await ReadErrors(response))];

        var dto = await response.Content.ReadFromJsonAsync<ProfileResponseDto>(_jsonOptions);
        return dto?.Profile is null ? [] : [new FollowToggled(MapProfile(dto.Profile))];
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Creates an HTTP request with optional Bearer token.</summary>
    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", token);
        return request;
    }

    /// <summary>Builds query string for article list endpoint.</summary>
    private static string BuildArticleQuery(
        int limit, int offset, string? tag, string? author, string? favorited)
    {
        var parts = new List<string> { $"limit={limit}", $"offset={offset}" };
        if (tag is not null)
            parts.Add($"tag={Uri.EscapeDataString(tag)}");
        if (author is not null)
            parts.Add($"author={Uri.EscapeDataString(author)}");
        if (favorited is not null)
            parts.Add($"favorited={Uri.EscapeDataString(favorited)}");
        return "?" + string.Join("&", parts);
    }

    /// <summary>Reads error messages from an API error response.</summary>
    private static async Task<IReadOnlyList<string>> ReadErrors(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorResponseDto>(_jsonOptions);
            return body?.Errors?.Body ?? [$"HTTP {(int)response.StatusCode}"];
        }
        catch
        {
            return [$"HTTP {(int)response.StatusCode}"];
        }
    }

    // ─── DTO → Domain Mapping ─────────────────────────────────────────────

    private static ArticlePreviewData MapArticlePreview(ArticleListItemDto dto) =>
        new(dto.Slug, dto.Title, dto.Description,
            dto.TagList, dto.CreatedAt, dto.UpdatedAt, dto.Favorited, dto.FavoritesCount,
            MapAuthor(dto.Author));

    /// <summary>Maps the full article DTO (with body) to an ArticlePreviewData.</summary>
    private static ArticlePreviewData MapArticlePreview(ArticleItemDto dto) =>
        new(dto.Slug, dto.Title, dto.Description,
            dto.TagList, dto.CreatedAt, dto.UpdatedAt, dto.Favorited, dto.FavoritesCount,
            MapAuthor(dto.Author));

    private static ArticleData MapArticle(ArticleItemDto dto) =>
        new(dto.Slug, dto.Title, dto.Description, dto.Body,
            dto.TagList, dto.CreatedAt, dto.UpdatedAt, dto.Favorited, dto.FavoritesCount,
            MapAuthor(dto.Author));

    private static CommentData MapComment(CommentItemDto dto) =>
        new(dto.Id, dto.CreatedAt, dto.UpdatedAt, dto.Body, MapAuthor(dto.Author));

    private static AuthorData MapAuthor(ProfileItemDto dto) =>
        new(dto.Username, dto.Bio, dto.Image, dto.Following);

    private static ProfileData MapProfile(ProfileItemDto dto) =>
        new(dto.Username, dto.Bio, dto.Image, dto.Following);
}

// =============================================================================
// Internal DTOs — JSON wire format for deserialization
// =============================================================================
// These mirror the Conduit API response shapes. We use separate DTOs from the
// API project since this is a client — we don't reference the server assembly.
// =============================================================================

// ─── Article DTOs ─────────────────────────────────────────────────────────────

internal sealed record MultipleArticlesDto(
    IReadOnlyList<ArticleListItemDto> Articles,
    int ArticlesCount);

internal sealed record SingleArticleDto(ArticleItemDto Article);

internal sealed record ArticleItemDto(
    string Slug,
    string Title,
    string Description,
    string Body,
    IReadOnlyList<string> TagList,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Favorited,
    int FavoritesCount,
    ProfileItemDto Author);

internal sealed record ArticleListItemDto(
    string Slug,
    string Title,
    string Description,
    IReadOnlyList<string> TagList,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Favorited,
    int FavoritesCount,
    ProfileItemDto Author);

// ─── Comment DTOs ─────────────────────────────────────────────────────────────

internal sealed record MultipleCommentsDto(IReadOnlyList<CommentItemDto> Comments);

internal sealed record SingleCommentDto(CommentItemDto Comment);

internal sealed record CommentItemDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Body,
    ProfileItemDto Author);

// ─── Profile DTOs ─────────────────────────────────────────────────────────────

internal sealed record ProfileResponseDto(ProfileItemDto Profile);

internal sealed record ProfileItemDto(
    string Username,
    string Bio,
    string? Image,
    bool Following);

// ─── User DTOs ────────────────────────────────────────────────────────────────

internal sealed record UserResponseDto(UserItemDto User);

internal sealed record UserItemDto(
    string Email,
    string Token,
    string Username,
    string Bio,
    string? Image);

// ─── Error DTOs ───────────────────────────────────────────────────────────────

internal sealed record ErrorResponseDto(ErrorBodyDto? Errors);

internal sealed record ErrorBodyDto(IReadOnlyList<string>? Body);

// ─── Tags DTOs ────────────────────────────────────────────────────────────────

internal sealed record TagsDto(IReadOnlyList<string> Tags);
