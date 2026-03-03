// =============================================================================
// Hedging — Resilience Strategy
// =============================================================================
// Sends parallel requests and takes the first successful result. Based on
// Dean & Barroso (2013): "The Tail at Scale" — hedged requests reduce
// tail latency by racing multiple attempts.
//
// State machine:
//
//   ┌──────────┐  first success   ┌───────────┐
//   │ Racing   │─────────────────▶│ Succeeded │
//   └────┬─────┘                  └───────────┘
//        │ all failed
//        ▼
//   ┌──────────┐
//   │ AllFailed│  (terminal)
//   └──────────┘
//
// The hedging strategy launches the primary request immediately, then after
// a configurable delay, launches additional hedged requests. The first
// success wins; remaining requests are cancelled.
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Automaton.Resilience.Hedging;

// =============================================================================
// Options
// =============================================================================

/// <summary>
/// Configuration for the hedging strategy.
/// </summary>
/// <param name="MaxHedgedAttempts">Total number of attempts (including primary). Defaults to 2.</param>
/// <param name="Delay">Delay before launching each hedged attempt. Defaults to 2 seconds.</param>
public record HedgingOptions(
    int MaxHedgedAttempts = 2,
    TimeSpan? Delay = null)
{
    /// <summary>The effective delay between hedged attempts (defaults to 2 seconds).</summary>
    public TimeSpan EffectiveDelay => Delay ?? TimeSpan.FromSeconds(2);
}

// =============================================================================
// Ergonomic API — Hedging.Execute()
// =============================================================================

/// <summary>
/// Ergonomic entry point for the hedging resilience strategy.
/// </summary>
/// <remarks>
/// <para>
/// Hedging reduces tail latency by racing multiple attempts. The first
/// successful result wins; all other in-flight requests are cancelled.
/// </para>
/// <para>
/// This is the "hedged requests" pattern from Dean &amp; Barroso (2013),
/// "The Tail at Scale", Communications of the ACM, 56(2):74-80.
/// </para>
/// </remarks>
public static class Hedging
{
    /// <summary>
    /// Executes an operation with hedged requests.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="operation">
    /// The operation factory. Called once per attempt with the attempt number (0-based)
    /// and a cancellation token.
    /// </param>
    /// <param name="options">Hedging configuration.</param>
    /// <param name="cancellationToken">Token for external cancellation.</param>
    /// <returns>
    /// <c>Ok(T)</c> from the first successful attempt, or
    /// <c>Err(ResilienceError)</c> with <see cref="FailureReason.HedgingFailed"/>.
    /// </returns>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<Result<T, ResilienceError>> Execute<T>(
        Func<int, CancellationToken, ValueTask<T>> operation,
        HedgingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new HedgingOptions();

        using var activity = ResilienceDiagnostics.Source.StartActivity("Hedging.Execute");
        activity?.SetTag("hedging.max_attempts", opts.MaxHedgedAttempts);
        activity?.SetTag("hedging.delay_ms", opts.EffectiveDelay.TotalMilliseconds);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var tasks = new List<Task<T>>(opts.MaxHedgedAttempts);
        var exceptions = new List<Exception>();

        try
        {
            // Launch primary immediately
            tasks.Add(RunAttempt(operation, 0, linkedCts.Token));

            // Launch hedged attempts with delays
            for (var i = 1; i < opts.MaxHedgedAttempts; i++)
            {
                try
                {
                    await Task.Delay(opts.EffectiveDelay, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break; // A previous attempt succeeded or external cancellation
                }

                // Check if any task already completed successfully
                var completed = FindCompletedSuccess(tasks);
                if (completed is not null)
                {
                    await linkedCts.CancelAsync().ConfigureAwait(false);
                    activity?.SetTag("hedging.winning_attempt", tasks.IndexOf(completed));
                    activity?.SetStatus(ActivityStatusCode.Ok);

                    return Result<T, ResilienceError>.Ok(await completed.ConfigureAwait(false));
                }

                tasks.Add(RunAttempt(operation, i, linkedCts.Token));
            }

            // Wait for the first successful completion
            while (tasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(tasks).ConfigureAwait(false);
                if (completedTask.IsCompletedSuccessfully)
                {
                    await linkedCts.CancelAsync().ConfigureAwait(false);
                    activity?.SetTag("hedging.winning_attempt", tasks.IndexOf(completedTask));
                    activity?.SetStatus(ActivityStatusCode.Ok);

                    return Result<T, ResilienceError>.Ok(await completedTask.ConfigureAwait(false));
                }

                // Task faulted — record exception, remove from list
                try
                {
                    await completedTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

                    return Result<T, ResilienceError>.Err(new ResilienceError(
                        "Operation was cancelled.",
                        "Hedging",
                        FailureReason.Cancelled));
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }

                tasks.Remove(completedTask);
            }

            // All attempts failed
            var lastEx = exceptions.Count > 0 ? exceptions[^1] : null;
            activity?.SetStatus(ActivityStatusCode.Error, "All hedged attempts failed");

            return Result<T, ResilienceError>.Err(new ResilienceError(
                $"All {opts.MaxHedgedAttempts} hedged attempt(s) failed. Last: {lastEx?.Message}",
                "Hedging",
                FailureReason.HedgingFailed,
                lastEx));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

            return Result<T, ResilienceError>.Err(new ResilienceError(
                "Operation was cancelled.",
                "Hedging",
                FailureReason.Cancelled));
        }
    }

    private static async Task<T> RunAttempt<T>(
        Func<int, CancellationToken, ValueTask<T>> operation,
        int attempt,
        CancellationToken cancellationToken) =>
        await operation(attempt, cancellationToken).ConfigureAwait(false);

    private static Task<T>? FindCompletedSuccess<T>(List<Task<T>> tasks)
    {
        for (var i = 0; i < tasks.Count; i++)
        {
            if (tasks[i].IsCompletedSuccessfully)
                return tasks[i];
        }

        return null;
    }
}
