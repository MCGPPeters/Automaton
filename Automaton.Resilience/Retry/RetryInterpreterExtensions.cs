// =============================================================================
// Retry Interpreter Extensions — compose retry into Automaton pipelines
// =============================================================================
// Provides interpreter combinators that wrap effect interpretation with
// retry logic, integrating the retry strategy into the existing Automaton
// runtime Observer/Interpreter pipeline architecture.
// =============================================================================

using System.Runtime.CompilerServices;
using Automaton.Resilience.Retry;

namespace Automaton.Resilience;

/// <summary>
/// Interpreter combinators for integrating retry logic into Automaton pipelines.
/// </summary>
public static class RetryInterpreterExtensions
{
    /// <summary>
    /// Wraps an interpreter with retry logic: if the interpreter returns
    /// <c>Err</c>, retries according to the specified options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This combinator composes with the existing <c>Then</c>/<c>Where</c>/<c>Catch</c>
    /// interpreter pipeline. Place it before or after other combinators to control
    /// the retry boundary.
    /// </para>
    /// <para>
    /// Only <see cref="PipelineError"/>s with a non-null <see cref="PipelineError.Exception"/>
    /// are considered retryable by default. Override this via <see cref="RetryOptions.ShouldRetry"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// Interpreter&lt;MyEffect, MyEvent&gt; resilientInterpreter =
    ///     httpInterpreter.WithRetry(new RetryOptions(MaxAttempts: 3));
    /// </code>
    /// </example>
    public static Interpreter<TEffect, TEvent> WithRetry<TEffect, TEvent>(
        this Interpreter<TEffect, TEvent> interpreter,
        RetryOptions? options = null) =>
        effect =>
        {
            var result = Retry.Retry.Execute(
                async ct =>
                {
                    var interpreterResult = await interpreter(effect).ConfigureAwait(false);
                    if (interpreterResult.IsErr)
                    {
                        // Surface the PipelineError as an exception so the retry loop can catch it
                        throw new PipelineErrorException(interpreterResult.Error);
                    }

                    return interpreterResult.Value;
                },
                options);

            if (result.IsCompletedSuccessfully)
            {
                var r = result.Result;
                return r.IsOk
                    ? new ValueTask<Result<TEvent[], PipelineError>>(
                        Result<TEvent[], PipelineError>.Ok(r.Value))
                    : new ValueTask<Result<TEvent[], PipelineError>>(
                        Result<TEvent[], PipelineError>.Err(
                            new PipelineError(r.Error.Message, "Retry", r.Error.Exception)));
            }

            return AwaitRetryResult(result);
        };

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Result<TEvent[], PipelineError>> AwaitRetryResult<TEvent>(
        ValueTask<Result<TEvent[], ResilienceError>> retryTask)
    {
        var r = await retryTask.ConfigureAwait(false);

        return r.IsOk
            ? Result<TEvent[], PipelineError>.Ok(r.Value)
            : Result<TEvent[], PipelineError>.Err(
                new PipelineError(r.Error.Message, "Retry", r.Error.Exception));
    }
}

/// <summary>
/// Internal exception wrapper to bridge PipelineError through the retry loop.
/// </summary>
/// <remarks>
/// The retry loop catches exceptions to determine retry eligibility. Since
/// <see cref="PipelineError"/> is a value type (not an exception), we wrap it
/// temporarily. The wrapper is never exposed to callers.
/// </remarks>
internal sealed class PipelineErrorException(PipelineError error) : Exception(error.Message)
{
    /// <summary>The wrapped pipeline error.</summary>
    public PipelineError PipelineError { get; } = error;
}
