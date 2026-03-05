// =============================================================================
// Command — Effects in the MVU Loop
// =============================================================================
// Commands are the effect channel of the MVU architecture. When the Update
// function processes a Message, it returns (Model, Command) where the Command
// describes a side effect to be executed by the runtime.
//
// Commands map to the Effect type parameter in the Automaton kernel:
//
//     Automaton<TState, TEvent, TEffect, TParameters>
//        ≡ Automaton<Model,   Message, Command, Flags>
//
// The runtime's Interpreter converts Commands into optional Messages
// (feedback events) that re-enter the MVU loop.
//
// The type hierarchy:
//   Command
//   ├── None        — no side effect
//   └── Batch(...)  — multiple commands to execute
//
// Domain-specific commands are defined by the application as records
// implementing the Command interface, e.g.:
//
//     public record FetchArticles(int Page) : Command;
//     public record SaveToLocalStorage(string Key, string Value) : Command;
// =============================================================================

namespace Abies;

/// <summary>
/// Marker interface for all commands (side effects) in the MVU loop.
/// </summary>
/// <remarks>
/// <para>
/// Commands are data descriptions of side effects. They are inert values —
/// the runtime's command interpreter converts them into feedback messages
/// that re-enter the MVU loop.
/// </para>
/// <para>
/// Use <see cref="None"/> for transitions that produce no side effects,
/// and <see cref="Batch"/> to combine multiple commands.
/// </para>
/// <example>
/// <code>
/// // Update returns a command to fetch data
/// static (Model, Command) Update(Model model, Message message) =>
///     message switch
///     {
///         FetchClicked => (model with { Loading = true }, new FetchArticles(1)),
///         DataLoaded(var articles) => (model with { Articles = articles }, Command.None),
///         _ => (model, Command.None)
///     };
/// </code>
/// </example>
/// </remarks>
public interface Command
{
    /// <summary>
    /// A command that does nothing. The unit element of the command monoid.
    /// </summary>
    sealed record None : Command;

    /// <summary>
    /// A batch of commands to execute. The binary operation of the command monoid.
    /// </summary>
    /// <param name="Commands">The commands to execute.</param>
    sealed record Batch(IReadOnlyList<Command> Commands) : Command;
}

/// <summary>
/// Factory methods for creating <see cref="Command"/> values.
/// </summary>
/// <remarks>
/// <para>
/// Commands form a <b>monoid</b>:
/// <list type="bullet">
///   <item><b>Identity</b>: <see cref="None"/> — does nothing.</item>
///   <item><b>Binary operation</b>: <see cref="Batch(Command[])"/> — combines commands.</item>
/// </list>
/// This means commands can be freely composed and accumulated.
/// </para>
/// </remarks>
public static class Commands
{
    /// <summary>
    /// A command that does nothing. Singleton instance.
    /// </summary>
    public static readonly Command None = new Command.None();

    /// <summary>
    /// Combines multiple commands into a single batch command.
    /// </summary>
    /// <param name="commands">The commands to batch together.</param>
    /// <returns>
    /// A <see cref="Command.Batch"/> if there are multiple commands,
    /// the single command if there is exactly one,
    /// or <see cref="None"/> if the collection is empty.
    /// </returns>
    public static Command Batch(params Command[] commands) =>
        commands.Length switch
        {
            0 => None,
            1 => commands[0],
            _ => new Command.Batch(commands)
        };

    /// <summary>
    /// Combines multiple commands into a single batch command.
    /// </summary>
    /// <param name="commands">The commands to batch together.</param>
    public static Command Batch(IEnumerable<Command> commands)
    {
        var list = commands as IReadOnlyList<Command> ?? commands.ToArray();
        return list.Count switch
        {
            0 => None,
            1 => list[0],
            _ => new Command.Batch(list)
        };
    }
}
