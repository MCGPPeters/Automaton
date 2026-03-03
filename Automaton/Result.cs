// =============================================================================
// Result — Success or Error
// =============================================================================
// A discriminated union for computations that can fail. Provides functor (Map/Select),
// monad (Bind/SelectMany), and LINQ query syntax support.
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
//     Map/Select       : (T → U) → Result<T, E> → Result<U, E>        (functor)
//     Bind/SelectMany  : (T → Result<U, E>) → Result<T, E> → Result<U, E>  (monad)
//
// LINQ query syntax (monad comprehension):
//     from x in result
//     from y in f(x)
//     select g(x, y)
//
//   desugars to:
//     result.SelectMany(x => f(x), (x, y) => g(x, y))
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
/// Callers handle both cases via <see cref="IsOk"/>/<see cref="IsErr"/> properties,
/// C# pattern matching, or LINQ query syntax.
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
/// <para>
/// Supports LINQ query syntax (monad comprehension) via <see cref="Select{TNew}"/>
/// and <see cref="SelectMany{TIntermediate,TNew}"/>. Errors short-circuit the chain.
/// </para>
/// <example>
/// <code>
/// // Pattern matching
/// var message = result.IsOk
///     ? $"Got {result.Value}"
///     : $"Failed: {result.Error}";
///
/// // LINQ query syntax (railway-oriented programming)
/// var final =
///     from x in parseInput(raw)
///     from y in validate(x)
///     select x + y;
///
/// // Fluent API
/// result.Map(v =&gt; v * 2)
///       .Bind(v =&gt; validate(v));
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
    /// Maps a function over the success value. LINQ-compatible alias for <see cref="Map{TNew}"/>.
    /// Enables <c>from x in result select f(x)</c> query syntax.
    /// </summary>
    /// <example>
    /// <code>
    /// var doubled = from v in Result&lt;int, string&gt;.Ok(21)
    ///               select v * 2;
    /// // doubled is Ok(42)
    /// </code>
    /// </example>
    public Result<TNew, TError> Select<TNew>(Func<TSuccess, TNew> selector) =>
        Map(selector);

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
    /// Chains a function that returns a Result, then projects the pair.
    /// LINQ-compatible overload that enables multi-<c>from</c> query syntax.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the method the C# compiler requires for:
    /// <code>
    /// from x in result1
    /// from y in f(x)
    /// select g(x, y)
    /// </code>
    /// which desugars to <c>result1.SelectMany(x =&gt; f(x), (x, y) =&gt; g(x, y))</c>.
    /// </para>
    /// <para>
    /// Errors short-circuit: if either <c>this</c> or the intermediate result is Err,
    /// the entire chain returns Err without invoking subsequent functions.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var result =
    ///     from order in Order.Create(cmd)
    ///     from payment in ProcessPayment(order)
    ///     select new OrderConfirmed(order.Id, payment.Id);
    /// </code>
    /// </example>
    /// <typeparam name="TIntermediate">The success type of the intermediate result.</typeparam>
    /// <typeparam name="TNew">The success type of the final projected result.</typeparam>
    /// <param name="bind">A function from the current success value to an intermediate Result.</param>
    /// <param name="project">A projection combining the original and intermediate success values.</param>
    public Result<TNew, TError> SelectMany<TIntermediate, TNew>(
        Func<TSuccess, Result<TIntermediate, TError>> bind,
        Func<TSuccess, TIntermediate, TNew> project) =>
        _isOk
            ? bind(_value) switch
            {
                { IsOk: true } intermediate => Result<TNew, TError>.Ok(project(_value, intermediate.Value)),
                var err => Result<TNew, TError>.Err(err.Error)
            }
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
/// <c>Unit</c> is a readonly struct with no fields — the JIT can optimize away
/// copies and storage. There is effectively no runtime cost compared to <c>void</c>.
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
