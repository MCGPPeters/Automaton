// =============================================================================
// User State — The Write Model for the User Aggregate
// =============================================================================
// Immutable record representing the current state of a user, built by
// folding UserEvents via the Decider's Transition function.
// =============================================================================

using Abies.Conduit.Domain.Shared;

namespace Abies.Conduit.Domain.User;

/// <summary>
/// The current state of a user aggregate.
/// </summary>
/// <param name="Id">The user's unique identifier.</param>
/// <param name="Email">The user's email address.</param>
/// <param name="Username">The user's display name.</param>
/// <param name="PasswordHash">The hashed password.</param>
/// <param name="Bio">The user's biography.</param>
/// <param name="Image">The user's avatar URL.</param>
/// <param name="Following">The set of user IDs this user follows.</param>
/// <param name="CreatedAt">When the user registered.</param>
/// <param name="UpdatedAt">When the user was last updated.</param>
/// <param name="Registered">Whether the user has completed registration (initial state is unregistered).</param>
public record UserState(
    UserId Id,
    EmailAddress Email,
    Username Username,
    PasswordHash PasswordHash,
    Bio Bio,
    ImageUrl Image,
    IReadOnlySet<UserId> Following,
    Timestamp CreatedAt,
    Timestamp UpdatedAt,
    bool Registered)
{
    /// <summary>
    /// The initial (unregistered) user state.
    /// </summary>
    public static readonly UserState Initial = new(
        Id: new UserId(Guid.Empty),
        Email: new EmailAddress(string.Empty),
        Username: new Username(string.Empty),
        PasswordHash: new PasswordHash(string.Empty),
        Bio: Bio.Empty,
        Image: ImageUrl.Empty,
        Following: new HashSet<UserId>(),
        CreatedAt: new Timestamp(DateTimeOffset.MinValue),
        UpdatedAt: new Timestamp(DateTimeOffset.MinValue),
        Registered: false);
}
