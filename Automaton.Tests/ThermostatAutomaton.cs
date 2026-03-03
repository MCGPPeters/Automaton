// =============================================================================
// Thermostat Automaton — Smart Thermostat Domain Logic
// =============================================================================
// A smart thermostat domain that monitors temperature, controls a heater,
// and validates commands.
//
// Demonstrates:
// - Multiple events from a single command (RecordReading → TemperatureRecorded + HeaterTurnedOn)
// - Command validation with rich errors (target range, inactive system)
// - Terminal state (Shutdown → IsTerminal)
// - Effects as data (ActivateHeater, DeactivateHeater, SendNotification)
//
// Used by the Event Sourcing tutorial to show a compelling ES aggregate.
// =============================================================================

using System.Diagnostics;

namespace Automaton.Tests;

// ── State ─────────────────────────────────────────────────────

/// <summary>
/// The state of the thermostat.
/// </summary>
public record ThermostatState(
    decimal CurrentTemp,
    decimal TargetTemp,
    bool Heating,
    bool Active);

// ── Commands ──────────────────────────────────────────────────

/// <summary>
/// Commands representing user/sensor intent for the thermostat.
/// </summary>
public interface ThermostatCommand
{
    /// <summary>Sensor submits a temperature reading.</summary>
    record struct RecordReading(decimal Temperature) : ThermostatCommand;

    /// <summary>User changes the target temperature.</summary>
    record struct SetTarget(decimal Target) : ThermostatCommand;

    /// <summary>Graceful shutdown of the thermostat.</summary>
    record struct Shutdown : ThermostatCommand;
}

// ── Events ────────────────────────────────────────────────────

/// <summary>
/// Events that can occur in the thermostat domain.
/// </summary>
public interface ThermostatEvent
{
    record struct TemperatureRecorded(decimal Temperature) : ThermostatEvent;
    record struct TargetSet(decimal Target) : ThermostatEvent;
    record struct HeaterTurnedOn : ThermostatEvent;
    record struct HeaterTurnedOff : ThermostatEvent;
    record struct AlertRaised(string Message) : ThermostatEvent;
    record struct ShutdownCompleted : ThermostatEvent;
}

// ── Errors ────────────────────────────────────────────────────

/// <summary>
/// Errors produced when thermostat command validation fails.
/// </summary>
public interface ThermostatError
{
    /// <summary>Target temperature is outside the allowed range.</summary>
    record struct InvalidTarget(decimal Target, decimal Min, decimal Max) : ThermostatError;

    /// <summary>Cannot operate — the thermostat has been shut down.</summary>
    record struct SystemInactive : ThermostatError;

    /// <summary>Shutdown requested when already shut down.</summary>
    record struct AlreadyShutdown : ThermostatError;
}

// ── Effects ───────────────────────────────────────────────────

/// <summary>
/// Effects produced by thermostat transitions.
/// </summary>
public interface ThermostatEffect
{
    record struct None : ThermostatEffect;
    record struct ActivateHeater : ThermostatEffect;
    record struct DeactivateHeater : ThermostatEffect;
    record struct SendNotification(string Message) : ThermostatEffect;
}

// ── Decider ───────────────────────────────────────────────────

/// <summary>
/// The thermostat Decider — pure domain logic with command validation.
/// </summary>
/// <remarks>
/// <para>
/// As a Decider, the Thermostat validates commands before producing events:
/// <list type="bullet">
///   <item><c>RecordReading</c> — always accepted (sensors don't lie), may trigger heater on/off or alerts</item>
///   <item><c>SetTarget</c> — validates target is in range [<see cref="MinTarget"/>, <see cref="MaxTarget"/>]</item>
///   <item><c>Shutdown</c> — rejected if already shut down; turns off heater if heating</item>
/// </list>
/// </para>
/// <para>
/// A single command can produce multiple events. For example, <c>RecordReading(18)</c>
/// when the target is 22 and the heater is off produces two events:
/// <c>[TemperatureRecorded(18), HeaterTurnedOn]</c>.
/// </para>
/// </remarks>
public class Thermostat
    : Decider<ThermostatState, ThermostatCommand, ThermostatEvent, ThermostatEffect, ThermostatError, Unit>
{
    public const decimal MinTarget = 5.0m;
    public const decimal MaxTarget = 40.0m;
    public const decimal AlertThreshold = 35.0m;

    public static (ThermostatState State, ThermostatEffect Effect) Initialize(Unit _) =>
        (new ThermostatState(
            CurrentTemp: 20.0m,
            TargetTemp: 22.0m,
            Heating: false,
            Active: true),
         new ThermostatEffect.None());

    public static Result<ThermostatEvent[], ThermostatError> Decide(
        ThermostatState state, ThermostatCommand command) =>
        command switch
        {
            // ── Shutdown (must come before the inactive guard) ───
            ThermostatCommand.Shutdown when !state.Active =>
                Result<ThermostatEvent[], ThermostatError>
                    .Err(new ThermostatError.AlreadyShutdown()),

            // ── Inactive guard (all other commands) ──────────────
            _ when !state.Active =>
                Result<ThermostatEvent[], ThermostatError>
                    .Err(new ThermostatError.SystemInactive()),

            // ── RecordReading ────────────────────────────────────
            ThermostatCommand.RecordReading(var temp) when temp > AlertThreshold =>
                Result<ThermostatEvent[], ThermostatError>
                    .Ok(state.Heating
                        ? [new ThermostatEvent.TemperatureRecorded(temp),
                           new ThermostatEvent.HeaterTurnedOff(),
                           new ThermostatEvent.AlertRaised(
                               $"Temperature {temp}°C exceeds alert threshold {AlertThreshold}°C")]
                        : [new ThermostatEvent.TemperatureRecorded(temp),
                           new ThermostatEvent.AlertRaised(
                               $"Temperature {temp}°C exceeds alert threshold {AlertThreshold}°C")]),

            ThermostatCommand.RecordReading(var temp) when temp < state.TargetTemp && !state.Heating =>
                Result<ThermostatEvent[], ThermostatError>
                    .Ok([new ThermostatEvent.TemperatureRecorded(temp),
                         new ThermostatEvent.HeaterTurnedOn()]),

            ThermostatCommand.RecordReading(var temp) when temp >= state.TargetTemp && state.Heating =>
                Result<ThermostatEvent[], ThermostatError>
                    .Ok([new ThermostatEvent.TemperatureRecorded(temp),
                         new ThermostatEvent.HeaterTurnedOff()]),

            ThermostatCommand.RecordReading(var temp) =>
                Result<ThermostatEvent[], ThermostatError>
                    .Ok([new ThermostatEvent.TemperatureRecorded(temp)]),

            // ── SetTarget ────────────────────────────────────────
            ThermostatCommand.SetTarget(var target) when target is < MinTarget or > MaxTarget =>
                Result<ThermostatEvent[], ThermostatError>
                    .Err(new ThermostatError.InvalidTarget(target, MinTarget, MaxTarget)),

            ThermostatCommand.SetTarget(var target) when state.CurrentTemp < target && !state.Heating =>
                Result<ThermostatEvent[], ThermostatError>
                    .Ok([new ThermostatEvent.TargetSet(target),
                         new ThermostatEvent.HeaterTurnedOn()]),

            ThermostatCommand.SetTarget(var target) when state.CurrentTemp >= target && state.Heating =>
                Result<ThermostatEvent[], ThermostatError>
                    .Ok([new ThermostatEvent.TargetSet(target),
                         new ThermostatEvent.HeaterTurnedOff()]),

            ThermostatCommand.SetTarget(var target) =>
                Result<ThermostatEvent[], ThermostatError>
                    .Ok([new ThermostatEvent.TargetSet(target)]),

            // ── Shutdown (active) ────────────────────────────────
            ThermostatCommand.Shutdown when state.Heating =>
                Result<ThermostatEvent[], ThermostatError>
                    .Ok([new ThermostatEvent.HeaterTurnedOff(),
                         new ThermostatEvent.ShutdownCompleted()]),

            ThermostatCommand.Shutdown =>
                Result<ThermostatEvent[], ThermostatError>
                    .Ok([new ThermostatEvent.ShutdownCompleted()]),

            _ => throw new UnreachableException()
        };

    public static (ThermostatState State, ThermostatEffect Effect) Transition(
        ThermostatState state, ThermostatEvent @event) =>
        @event switch
        {
            ThermostatEvent.TemperatureRecorded(var temp) =>
                (state with { CurrentTemp = temp },
                 new ThermostatEffect.None()),

            ThermostatEvent.TargetSet(var target) =>
                (state with { TargetTemp = target },
                 new ThermostatEffect.None()),

            ThermostatEvent.HeaterTurnedOn =>
                (state with { Heating = true },
                 new ThermostatEffect.ActivateHeater()),

            ThermostatEvent.HeaterTurnedOff =>
                (state with { Heating = false },
                 new ThermostatEffect.DeactivateHeater()),

            ThermostatEvent.AlertRaised(var message) =>
                (state,
                 new ThermostatEffect.SendNotification(message)),

            ThermostatEvent.ShutdownCompleted =>
                (state with { Active = false },
                 new ThermostatEffect.SendNotification("Thermostat shut down")),

            _ => throw new UnreachableException()
        };

    /// <summary>
    /// A shut-down thermostat is terminal — no further commands should be processed.
    /// </summary>
    public static bool IsTerminal(ThermostatState state) => !state.Active;
}

// ── Observers ─────────────────────────────────────────────────

/// <summary>
/// Reusable <see cref="Observer{TState,TEvent,TEffect}"/> implementations for the thermostat domain.
/// </summary>
/// <remarks>
/// <para>
/// An observer sees every transition triple <c>(State, Event, Effect)</c> after the automaton steps.
/// It is the extension point for side effects that depend on the transition result:
/// rendering a view (MVU), persisting an event (ES), or logging an audit trail.
/// </para>
/// <para>
/// These factory methods return concrete thermostat observers ready to plug into
/// <see cref="AutomatonRuntime{TAutomaton,TState,TEvent,TEffect,TParameters}"/> or
/// <see cref="DecidingRuntime{TDecider,TState,TCommand,TEvent,TEffect,TError,TParameters}"/>.
/// </para>
/// </remarks>
public static class ThermostatObservers
{
    /// <summary>
    /// An observer that does nothing. Useful when you only care about state,
    /// not about observing individual transitions.
    /// </summary>
    public static readonly Observer<ThermostatState, ThermostatEvent, ThermostatEffect> NoOp =
        (_, _, _) => PipelineResult.Ok;

    /// <summary>
    /// An observer that captures every transition triple into a list.
    /// Useful for asserting the exact sequence of state/event/effect triples.
    /// </summary>
    /// <param name="log">The list to append transition triples to.</param>
    /// <example>
    /// <code>
    /// var log = new List&lt;(ThermostatState, ThermostatEvent, ThermostatEffect)&gt;();
    /// var runtime = new AutomatonRuntime&lt;Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit&gt;(
    ///     Thermostat.Initialize().State, ThermostatObservers.Capture(log), ThermostatInterpreters.NoOp);
    ///
    /// await runtime.Dispatch(new ThermostatEvent.TemperatureRecorded(18m));
    /// // log[0] == (ThermostatState { CurrentTemp = 18 }, TemperatureRecorded(18), None)
    /// </code>
    /// </example>
    public static Observer<ThermostatState, ThermostatEvent, ThermostatEffect> Capture(
        List<(ThermostatState State, ThermostatEvent Event, ThermostatEffect Effect)> log) =>
        (state, @event, effect) =>
        {
            log.Add((state, @event, effect));
            return PipelineResult.Ok;
        };
}

// ── Interpreters ──────────────────────────────────────────────

/// <summary>
/// Reusable <see cref="Interpreter{TEffect,TEvent}"/> implementations for the thermostat domain.
/// </summary>
/// <remarks>
/// <para>
/// An interpreter converts an effect into zero or more feedback events that are
/// dispatched back into the automaton, creating a closed loop. Return an empty
/// sequence for fire-and-forget effects.
/// </para>
/// <para>
/// These factory methods return concrete thermostat interpreters ready to plug into
/// <see cref="AutomatonRuntime{TAutomaton,TState,TEvent,TEffect,TParameters}"/> or
/// <see cref="DecidingRuntime{TDecider,TState,TCommand,TEvent,TEffect,TError,TParameters}"/>.
/// </para>
/// </remarks>
public static class ThermostatInterpreters
{
    /// <summary>
    /// An interpreter that ignores all effects. No feedback events are produced.
    /// This is the simplest interpreter — useful when you only care about state transitions.
    /// </summary>
    public static readonly Interpreter<ThermostatEffect, ThermostatEvent> NoOp =
        _ => new ValueTask<Result<ThermostatEvent[], PipelineError>>(
            Result<ThermostatEvent[], PipelineError>.Ok([]));

    /// <summary>
    /// An interpreter that captures all effects into a list while producing no feedback events.
    /// Useful for asserting that the correct effects were produced by transitions.
    /// </summary>
    /// <param name="effects">The list to append effects to.</param>
    /// <example>
    /// <code>
    /// var effects = new List&lt;ThermostatEffect&gt;();
    /// var runtime = new AutomatonRuntime&lt;Thermostat, ThermostatState, ThermostatEvent, ThermostatEffect, Unit&gt;(
    ///     Thermostat.Initialize().State, ThermostatObservers.NoOp, ThermostatInterpreters.Capture(effects));
    ///
    /// await runtime.Dispatch(new ThermostatEvent.HeaterTurnedOn());
    /// // effects[0] is ThermostatEffect.ActivateHeater
    /// </code>
    /// </example>
    public static Interpreter<ThermostatEffect, ThermostatEvent> Capture(
        List<ThermostatEffect> effects) =>
        effect =>
        {
            effects.Add(effect);
            return new ValueTask<Result<ThermostatEvent[], PipelineError>>(
                Result<ThermostatEvent[], PipelineError>.Ok([]));
        };

    /// <summary>
    /// An interpreter that captures notification messages while producing no feedback events.
    /// Useful for asserting that alerts and shutdown notifications were sent.
    /// </summary>
    /// <param name="notifications">The list to append notification messages to.</param>
    public static Interpreter<ThermostatEffect, ThermostatEvent> CaptureNotifications(
        List<string> notifications) =>
        effect =>
        {
            if (effect is ThermostatEffect.SendNotification notification)
            {
                notifications.Add(notification.Message);
            }

            return new ValueTask<Result<ThermostatEvent[], PipelineError>>(
                Result<ThermostatEvent[], PipelineError>.Ok([]));
        };
}
