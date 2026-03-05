// =============================================================================
// Message Marker Interface
// =============================================================================
// All messages in the MVU loop implement this marker interface.
// Messages are immutable records describing events that can change state.
// The Update function pattern-matches on message types.
//
// See picea/Abies Types.cs — this is the foundation of the MVU type system.
// =============================================================================

namespace Abies;

/// <summary>
/// Marker interface for all messages in the MVU loop.
/// Messages are immutable records describing events that can change state.
/// The Update function pattern-matches on message types.
/// </summary>
public interface Message;
