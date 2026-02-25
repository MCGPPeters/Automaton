// =============================================================================
// Result — Success or Error
// =============================================================================
// A discriminated union for computations that can fail. Provides exhaustive
// pattern matching, functor (Map), and monad (Bind) operations.
//
// Used by the Decider to represent the outcome of command validation:
//
//     Decide : Command → State → Result<Events, Error>
//
// Algebraic structure:
//     Result<T, E> ≅ T + E    (coproduct / sum type)
//     Map    : (T → U) → Result<T, E> → Result<U, E>     (functor)
//     Bind   : (T → Result<U, E>) → Result<T, E> → Result<U, E>  (monad)
// =============================================================================

using System.Diagnostics;

namespace Automaton;

/// <summary>
/// A discriminated union representing either a success value or an error.
/// </summary>
/// <remarks>
/// <para>
/// Result is the standard functional approach to error handling without exceptions.
/// It forces callers to handle both cases explicitly via
/// <see cref="Match{TResult}(Func{TSuccess, TResult}, Func{TError, TResult})"/>.
/// </para>
/// <para>
/// Prefer <c>Result</c> over exceptions for expected failures (validation errors,
/// business rule violations). Reserve exceptions for programmer bugs and
/// unrecoverable infrastructure failures.
/// </para>
/// <example>
/// <code>
/// Result&lt;int, string&gt; result = new Result&lt;int, string&gt;.Ok(42);
///
/// string message = result.Match(
///     value =&gt; $"Got {value}",
///     error =&gt; $"Failed: {error}");
/// // message == "Got 42"
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TSuccess">The type of the success value.</typeparam>
/// <typeparam name="TError">The type of the error value.</typeparam>
public abstract record Result<TSuccess, TError>
{
    /// <summary>
    /// Represents a successful result containing a value.
    /// </summary>
    public sealed record Ok(TSuccess Value) : Result<TSuccess, TError>;

    /// <summary>
    /// Represents a failed result containing an error.
    /// </summary>
    public sealed record Err(TError Error) : Result<TSuccess, TError>;

    /// <summary>
    /// Whether this result is a success.
    /// </summary>
    public bool IsOk => this is Ok;

    /// <summary>
    /// Whether this result is an error.
    /// </summary>
    public bool IsErr => this is Err;

    /// <summary>
    /// Exhaustive pattern match over both cases.
    /// </summary>
    /// <example>
    /// <code>
    /// var text = result.Match(
    ///     value =&gt; $"Success: {value}",
    ///     error =&gt; $"Error: {error}");
    /// </code>
    /// </example>
    public TResult Match<TResult>(
        Func<TSuccess, TResult> onOk,
        Func<TError, TResult> onErr) =>
        this switch
        {
            Ok(var value) => onOk(value),
            Err(var error) => onErr(error),
            _ => throw new UnreachableException()
        };

    /// <summary>
    /// Async exhaustive pattern match over both cases.
    /// </summary>
    public async Task<TResult> Match<TResult>(
        Func<TSuccess, Task<TResult>> onOk,
        Func<TError, Task<TResult>> onErr) =>
        this switch
        {
            Ok(var value) => await onOk(value),
            Err(var error) => await onErr(error),
            _ => throw new UnreachableException()
        };

    /// <summary>
    /// Maps a function over the success value (functor).
    /// </summary>
    /// <remarks>
    /// If this is <see cref="Ok"/>, applies <paramref name="f"/> to the value.
    /// If this is <see cref="Err"/>, propagates the error unchanged.
    /// </remarks>
    public Result<TNew, TError> Map<TNew>(Func<TSuccess, TNew> f) =>
        Match<Result<TNew, TError>>(
            value => new Result<TNew, TError>.Ok(f(value)),
            error => new Result<TNew, TError>.Err(error));

    /// <summary>
    /// Chains a function that returns a Result over the success value (monad bind).
    /// </summary>
    /// <remarks>
    /// Enables railway-oriented programming: if this is <see cref="Ok"/>,
    /// applies <paramref name="f"/>; if <see cref="Err"/>, short-circuits.
    /// </remarks>
    public Result<TNew, TError> Bind<TNew>(Func<TSuccess, Result<TNew, TError>> f) =>
        Match(f, error => new Result<TNew, TError>.Err(error));

    /// <summary>
    /// Maps a function over the error value.
    /// </summary>
    public Result<TSuccess, TNew> MapError<TNew>(Func<TError, TNew> f) =>
        Match<Result<TSuccess, TNew>>(
            value => new Result<TSuccess, TNew>.Ok(value),
            error => new Result<TSuccess, TNew>.Err(f(error)));
}
