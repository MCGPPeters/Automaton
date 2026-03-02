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
// Also used by Observer and Interpreter pipelines to propagate errors as
// values instead of exceptions (railway-oriented programming).
//
// Algebraic structure:
//     Result<T, E> ≅ T + E    (coproduct / sum type)
//     Map    : (T → U) → Result<T, E> → Result<U, E>     (functor)
//     Bind   : (T → Result<U, E>) → Result<T, E> → Result<U, E>  (monad)
//
// Implementation note:
//     Result is a readonly struct to avoid heap allocation. Each Ok/Err
//     is stack-allocated, avoiding per-result heap allocations on every Decide
//     and Handle call. The bool discriminator replaces the virtual dispatch
//     of the previous abstract record hierarchy.
// =============================================================================

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
/// <para>
/// Result is a <c>readonly struct</c> to avoid heap allocation on every Decide
/// and Handle call. Use the static factory methods <see cref="Ok"/> and
/// <see cref="Err"/> to create instances.
/// </para>
/// <example>
/// <code>
/// Result&lt;int, string&gt; result = Result&lt;int, string&gt;.Ok(42);
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
public readonly struct Result<TSuccess, TError>
{
    private readonly bool _isOk;
    private readonly TSuccess _value;
    private readonly TError _error;

    private Result(bool isOk, TSuccess value, TError error)
    {
        _isOk = isOk;
        _value = value;
        _error = error;
    }

    /// <summary>
    /// Creates a successful result containing a value.
    /// </summary>
    public static Result<TSuccess, TError> Ok(TSuccess value) =>
        new(true, value, default!);

    /// <summary>
    /// Creates a failed result containing an error.
    /// </summary>
    public static Result<TSuccess, TError> Err(TError error) =>
        new(false, default!, error);

    /// <summary>
    /// Whether this result is a success.
    /// </summary>
    public bool IsOk => _isOk;

    /// <summary>
    /// Whether this result is an error.
    /// </summary>
    public bool IsErr => !_isOk;

    /// <summary>
    /// The success value. Throws if this is an error result.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when accessing Value on an Err result.</exception>
    public TSuccess Value => _isOk
        ? _value
        : throw new InvalidOperationException("Cannot access Value on an Err result.");

    /// <summary>
    /// The error value. Throws if this is a success result.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when accessing Error on an Ok result.</exception>
    public TError Error => !_isOk
        ? _error
        : throw new InvalidOperationException("Cannot access Error on an Ok result.");

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
        _isOk ? onOk(_value) : onErr(_error);

    /// <summary>
    /// Async exhaustive pattern match over both cases.
    /// </summary>
    public Task<TResult> Match<TResult>(
        Func<TSuccess, Task<TResult>> onOk,
        Func<TError, Task<TResult>> onErr) =>
        _isOk ? onOk(_value) : onErr(_error);

    /// <summary>
    /// Maps a function over the success value (functor).
    /// </summary>
    /// <remarks>
    /// If this is Ok, applies <paramref name="f"/> to the value.
    /// If this is Err, propagates the error unchanged.
    /// </remarks>
    public Result<TNew, TError> Map<TNew>(Func<TSuccess, TNew> f) =>
        _isOk
            ? Result<TNew, TError>.Ok(f(_value))
            : Result<TNew, TError>.Err(_error);

    /// <summary>
    /// Chains a function that returns a Result over the success value (monad bind).
    /// </summary>
    /// <remarks>
    /// Enables railway-oriented programming: if this is Ok,
    /// applies <paramref name="f"/>; if Err, short-circuits.
    /// </remarks>
    public Result<TNew, TError> Bind<TNew>(Func<TSuccess, Result<TNew, TError>> f) =>
        _isOk
            ? f(_value)
            : Result<TNew, TError>.Err(_error);

    /// <summary>
    /// Maps a function over the error value.
    /// </summary>
    public Result<TSuccess, TNew> MapError<TNew>(Func<TError, TNew> f) =>
        _isOk
            ? Result<TSuccess, TNew>.Ok(_value)
            : Result<TSuccess, TNew>.Err(f(_error));

    /// <inheritdoc/>
    public override string ToString() =>
        _isOk ? $"Ok({_value})" : $"Err({_error})";
}

/// <summary>
/// The unit type — a type with exactly one value, used where a success type
/// is required but no meaningful value exists.
/// </summary>
/// <remarks>
/// <para>
/// In functional programming, <c>Unit</c> replaces <c>void</c> in generic contexts.
/// Since <c>void</c> is not a first-class type in C#, <c>Result&lt;void, E&gt;</c>
/// is not expressible. <c>Result&lt;Unit, E&gt;</c> fills this gap.
/// </para>
/// <para>
/// <c>Unit</c> is a readonly struct with zero size — the JIT optimizes it away
/// entirely. There is no runtime cost compared to <c>void</c>.
/// </para>
/// <example>
/// <code>
/// // Observer returns Result&lt;Unit, ObserverError&gt; — "I succeeded" or "I failed"
/// Result&lt;Unit, string&gt;.Ok(Unit.Value)   // success with no payload
/// Result&lt;Unit, string&gt;.Err("failed")     // failure with error
/// </code>
/// </example>
/// </remarks>
public readonly record struct Unit
{
    /// <summary>
    /// The singleton value of the unit type.
    /// </summary>
    public static readonly Unit Value = default;

    /// <inheritdoc/>
    public override string ToString() => "()";
}
