// =============================================================================
// Event Serialization — JSON (De)Serialization for KurrentDB
// =============================================================================
// Provides serialize/deserialize delegates for UserEvent and ArticleEvent
// compatible with the KurrentDBEventStore<TEvent> constructor.
//
// Uses System.Text.Json with [JsonDerivedType] for polymorphic serialization.
// The event type discriminator is a string matching the record type name,
// stored as KurrentDB event type metadata for human-readable stream inspection.
// =============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using Abies.Conduit.Domain.Article;
using Abies.Conduit.Domain.User;

namespace Abies.Conduit.Api.Infrastructure;

/// <summary>
/// JSON serialization configuration for domain events stored in KurrentDB.
/// </summary>
public static class EventSerialization
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // ─── UserEvent ────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes a <see cref="UserEvent"/> to a KurrentDB-compatible tuple.
    /// </summary>
    public static (string EventType, ReadOnlyMemory<byte> Data) SerializeUserEvent(UserEvent @event) =>
        (@event.GetType().Name, JsonSerializer.SerializeToUtf8Bytes<object>(@event, Options));

    /// <summary>
    /// Deserializes a <see cref="UserEvent"/> from KurrentDB event type and data.
    /// </summary>
    public static UserEvent DeserializeUserEvent(string eventType, ReadOnlyMemory<byte> data) =>
        eventType switch
        {
            nameof(UserEvent.Registered) =>
                JsonSerializer.Deserialize<UserEvent.Registered>(data.Span, Options)!,
            nameof(UserEvent.ProfileUpdated) =>
                JsonSerializer.Deserialize<UserEvent.ProfileUpdated>(data.Span, Options)!,
            nameof(UserEvent.Followed) =>
                JsonSerializer.Deserialize<UserEvent.Followed>(data.Span, Options)!,
            nameof(UserEvent.Unfollowed) =>
                JsonSerializer.Deserialize<UserEvent.Unfollowed>(data.Span, Options)!,
            _ => throw new InvalidOperationException($"Unknown UserEvent type: {eventType}")
        };

    // ─── ArticleEvent ─────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes an <see cref="ArticleEvent"/> to a KurrentDB-compatible tuple.
    /// </summary>
    public static (string EventType, ReadOnlyMemory<byte> Data) SerializeArticleEvent(ArticleEvent @event) =>
        (@event.GetType().Name, JsonSerializer.SerializeToUtf8Bytes<object>(@event, Options));

    /// <summary>
    /// Deserializes an <see cref="ArticleEvent"/> from KurrentDB event type and data.
    /// </summary>
    public static ArticleEvent DeserializeArticleEvent(string eventType, ReadOnlyMemory<byte> data) =>
        eventType switch
        {
            nameof(ArticleEvent.ArticleCreated) =>
                JsonSerializer.Deserialize<ArticleEvent.ArticleCreated>(data.Span, Options)!,
            nameof(ArticleEvent.ArticleUpdated) =>
                JsonSerializer.Deserialize<ArticleEvent.ArticleUpdated>(data.Span, Options)!,
            nameof(ArticleEvent.ArticleDeleted) =>
                JsonSerializer.Deserialize<ArticleEvent.ArticleDeleted>(data.Span, Options)!,
            nameof(ArticleEvent.CommentAdded) =>
                JsonSerializer.Deserialize<ArticleEvent.CommentAdded>(data.Span, Options)!,
            nameof(ArticleEvent.CommentDeleted) =>
                JsonSerializer.Deserialize<ArticleEvent.CommentDeleted>(data.Span, Options)!,
            nameof(ArticleEvent.ArticleFavorited) =>
                JsonSerializer.Deserialize<ArticleEvent.ArticleFavorited>(data.Span, Options)!,
            nameof(ArticleEvent.ArticleUnfavorited) =>
                JsonSerializer.Deserialize<ArticleEvent.ArticleUnfavorited>(data.Span, Options)!,
            _ => throw new InvalidOperationException($"Unknown ArticleEvent type: {eventType}")
        };
}
