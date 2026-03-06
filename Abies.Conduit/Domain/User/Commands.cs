// =============================================================================
// User Commands — Intent Representations
// =============================================================================
// Commands are what users want to do. The Decider validates them against
// the current state and produces events (or rejects them with errors).
//
// Commands carry pre-validated constrained types where possible (e.g.,
// EmailAddress instead of raw string). Validation of the raw input into
// constrained types happens at the API boundary (anti-corruption layer).
// =============================================================================

using Abies.Conduit.Domain.Shared;
using Automaton;

namespace Abies.Conduit.Domain.User;

/// <summary>
/// Commands representing user intent for the User aggregate.
/// </summary>
public interface UserCommand
{
    /// <summary>
    /// Register a new user account.
    /// </summary>
    /// <param name="Id">The pre-generated user ID.</param>
    /// <param name="Email">The validated email address.</param>
    /// <param name="Username">The validated username.</param>
    /// <param name="PasswordHash">The pre-hashed password (hashing is a capability at the boundary).</param>
    /// <param name="CreatedAt">The timestamp of registration (injected as capability).</param>
    record Register(
        UserId Id,
        EmailAddress Email,
        Username Username,
        PasswordHash PasswordHash,
        Timestamp CreatedAt) : UserCommand;

    /// <summary>
    /// Update the user's profile information.
    /// </summary>
    /// <remarks>
    /// All fields are optional — only provided fields are updated.
    /// Uses <see cref="Option{T}"/> to distinguish "not provided" from "set to empty".
    /// </remarks>
    /// <param name="Email">New email address, if changing.</param>
    /// <param name="Username">New username, if changing.</param>
    /// <param name="PasswordHash">New password hash, if changing.</param>
    /// <param name="Bio">New bio, if changing.</param>
    /// <param name="Image">New avatar URL, if changing.</param>
    /// <param name="UpdatedAt">The timestamp of the update.</param>
    record UpdateProfile(
        Option<EmailAddress> Email,
        Option<Username> Username,
        Option<PasswordHash> PasswordHash,
        Option<Bio> Bio,
        Option<ImageUrl> Image,
        Timestamp UpdatedAt) : UserCommand;

    /// <summary>
    /// Follow another user.
    /// </summary>
    /// <param name="FolloweeId">The ID of the user to follow.</param>
    record Follow(UserId FolloweeId) : UserCommand;

    /// <summary>
    /// Unfollow a previously followed user.
    /// </summary>
    /// <param name="FolloweeId">The ID of the user to unfollow.</param>
    record Unfollow(UserId FolloweeId) : UserCommand;
}
