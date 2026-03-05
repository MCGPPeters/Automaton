// =============================================================================
// Abies Runtime — The MVU Execution Loop
// =============================================================================
// The AbiesRuntime orchestrates the MVU loop by composing the Automaton kernel's
// AutomatonRuntime with three MVU-specific concerns:
//
//     1. View:          Model → Document → Diff → Patches → Apply
//     2. Subscriptions: Model → Subscription → SubscriptionManager.Update
//     3. Commands:      Command → Interpreter → feedback Messages
//
// The runtime is platform-agnostic. The Apply delegate is the seam between
// the pure Abies core and the platform-specific renderer:
//
//     Browser (Abies.Browser):  Apply calls JS interop to patch the real DOM
//     Server  (Abies.Server):   Apply produces an HTML string for SSR
//     Tests:                    Apply captures patches for assertions
//
// Architecturally, this is the Automaton kernel's Observer/Interpreter pattern
// specialized for MVU:
//
//     Observer   = Render view + Diff + Apply patches + Update subscriptions
//     Interpreter = Caller-supplied (handles Commands, returns feedback Messages)
//
// The runtime is single-threaded by design (WASM constraint). The Automaton
// kernel's SemaphoreSlim serialization is disabled (threadSafe: false) and
// event tracking is disabled (trackEvents: false) to minimize overhead.
//
// OpenTelemetry instrumentation is provided via System.Diagnostics.ActivitySource.
// =============================================================================

using System.Diagnostics;
using Abies.DOM;
using Abies.Subscriptions;
using Automaton;

namespace Abies;

/// <summary>
/// Applies a list of DOM patches to the platform's rendering surface.
/// </summary>
/// <remarks>
/// <para>
/// This delegate is the boundary between the pure Abies core and
/// platform-specific rendering. Each platform provides its own
/// implementation:
/// </para>
/// <list type="bullet">
///   <item><b>Browser</b>: calls JavaScript interop to mutate the real DOM</item>
///   <item><b>Server</b>: produces an HTML string for server-side rendering</item>
///   <item><b>Tests</b>: captures patches for assertions</item>
/// </list>
/// </remarks>
/// <param name="patches">The DOM patches to apply.</param>
public delegate void Apply(IReadOnlyList<Patch> patches);

/// <summary>
/// The MVU runtime: wires the Automaton kernel to View, Diff, and Subscriptions.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AbiesRuntime{TProgram,TModel,TArgument}"/> is a thin orchestration
/// layer over <see cref="AutomatonRuntime{TAutomaton,TState,TEvent,TEffect,TParameters}"/>.
/// After each transition, the observer:
/// </para>
/// <list type="number">
///   <item>Calls <c>TProgram.View(model)</c> to produce a new virtual DOM</item>
///   <item>Diffs the new DOM against the previous DOM to compute patches</item>
///   <item>Calls the <see cref="Apply"/> delegate to apply patches</item>
///   <item>Calls <c>TProgram.Subscriptions(model)</c> and reconciles with running subscriptions</item>
/// </list>
/// <para>
/// The <see cref="Interpreter{TEffect,TEvent}"/> is supplied by the platform
/// or the test harness. It converts <see cref="Command"/> instances into
/// feedback <see cref="Message"/> arrays. For commands that produce no feedback,
/// return an empty array.
/// </para>
/// <example>
/// <code>
/// // Test usage — capture patches for assertions
/// var patches = new List&lt;IReadOnlyList&lt;Patch&gt;&gt;();
/// var runtime = await AbiesRuntime&lt;Counter, CounterModel, Unit&gt;.Start(
///     apply: p =&gt; patches.Add(p),
///     interpreter: _ =&gt; new ValueTask&lt;Result&lt;Message[], PipelineError&gt;&gt;(
///         Result&lt;Message[], PipelineError&gt;.Ok([])));
///
/// await runtime.Dispatch(new CounterMessage.Increment());
/// // runtime.Model.Count == 1
/// // patches contains the DOM diffs
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TProgram">The program type implementing <see cref="Program{TModel,TArgument}"/>.</typeparam>
/// <typeparam name="TModel">The application model (state).</typeparam>
/// <typeparam name="TArgument">Initialization parameters.</typeparam>
public sealed class AbiesRuntime<TProgram, TModel, TArgument> : IDisposable
    where TProgram : Program<TModel, TArgument>
{
    private static readonly ActivitySource _activitySource = new("Abies.Runtime");

    private AutomatonRuntime<TProgram, TModel, Message, Command, TArgument> _core = null!;
    private readonly Apply _apply;
    private readonly Action<string>? _titleChanged;
    private Document? _currentDocument;
    private SubscriptionState _subscriptionState = SubscriptionState.Empty;

    /// <summary>
    /// The current application model.
    /// </summary>
    public TModel Model => _core.State;

    /// <summary>
    /// The current virtual DOM document (after the most recent render).
    /// </summary>
    public Document? CurrentDocument => _currentDocument;

    /// <summary>
    /// Private constructor — use <see cref="Start"/> to create instances.
    /// The runtime uses two-phase initialization: construct first, then
    /// wire the kernel runtime via field assignment in <see cref="Start"/>.
    /// </summary>
    private AbiesRuntime(Apply apply, Action<string>? titleChanged) =>
        (_apply, _titleChanged) = (apply, titleChanged);

    /// <summary>
    /// The observer callback: renders view, diffs, applies patches, updates subscriptions.
    /// This is an instance method so it accesses the runtime's mutable fields
    /// (<see cref="_currentDocument"/>, <see cref="_subscriptionState"/>) directly,
    /// avoiding the stale-closure problem that arises with captured local variables.
    /// </summary>
    private ValueTask<Result<Unit, PipelineError>> Observe(TModel state, Message _, Command __)
    {
        using var renderActivity = _activitySource.StartActivity("Abies.Render");

        // Render new view
        var newDocument = TProgram.View(state);

        // Diff against previous
        var patches = Operations.Diff(_currentDocument?.Body, newDocument.Body);

        // Update handler registry for new/removed event handlers
        UpdateHandlerRegistry(patches);

        // Apply patches to platform
        if (patches.Count > 0)
        {
            _apply(patches);
        }

        // Update title if changed
        if (_currentDocument is null || _currentDocument.Title != newDocument.Title)
        {
            _titleChanged?.Invoke(newDocument.Title);
        }

        _currentDocument = newDocument;

        // Update subscriptions
        var desiredSubscriptions = TProgram.Subscriptions(state);
        _subscriptionState = SubscriptionManager.Update(
            _subscriptionState, desiredSubscriptions, DispatchFromSubscription);

        renderActivity?.SetTag("abies.patches", patches.Count);
        renderActivity?.SetStatus(ActivityStatusCode.Ok);

        return PipelineResult.Ok;
    }

    /// <summary>
    /// Updates the <see cref="HandlerRegistry"/> based on the patches produced by diff.
    /// Registers new handlers and unregisters removed handlers so event delegation
    /// can dispatch messages correctly.
    /// </summary>
    private static void UpdateHandlerRegistry(IReadOnlyList<Patch> patches)
    {
        foreach (var patch in patches)
        {
            switch (patch)
            {
                case AddHandler p:
                    HandlerRegistry.Register(p.Handler);
                    break;

                case RemoveHandler p:
                    HandlerRegistry.Unregister(p.Handler.CommandId);
                    break;

                case UpdateHandler p:
                    HandlerRegistry.Unregister(p.OldHandler.CommandId);
                    HandlerRegistry.Register(p.NewHandler);
                    break;

                // When adding children, register all handlers in the new subtree
                case AddChild p:
                    HandlerRegistry.RegisterHandlers(p.Child);
                    break;

                case AddRoot p:
                    HandlerRegistry.RegisterHandlers(p.Element);
                    break;

                case ReplaceChild p:
                    HandlerRegistry.UnregisterHandlers(p.OldElement);
                    HandlerRegistry.RegisterHandlers(p.NewElement);
                    break;

                // When removing children, unregister all handlers in the old subtree
                case RemoveChild p:
                    HandlerRegistry.UnregisterHandlers(p.Child);
                    break;

                case ClearChildren p:
                    foreach (var child in p.OldChildren)
                    {
                        HandlerRegistry.UnregisterHandlers(child);
                    }
                    break;

                // SetChildrenHtml replaces all children — register all new handlers
                case SetChildrenHtml p:
                    foreach (var child in p.Children)
                    {
                        HandlerRegistry.RegisterHandlers(child);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Dispatch delegate used by subscription sources to feed messages into the MVU loop.
    /// Fire-and-forget: subscription tasks call this from background threads.
    /// </summary>
    private void DispatchFromSubscription(Message message) =>
        _ = _core.Dispatch(message);

    /// <summary>
    /// Starts the MVU runtime: initializes the model, renders the first view,
    /// applies initial patches, starts initial subscriptions, and interprets
    /// any initial commands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The startup sequence is:
    /// <list type="number">
    ///   <item><c>TProgram.Initialize(argument)</c> → <c>(model, command)</c></item>
    ///   <item><c>TProgram.View(model)</c> → <c>document</c></item>
    ///   <item><c>Operations.Diff(null, document.Body)</c> → initial patches (AddRoot)</item>
    ///   <item><c>apply(patches)</c> → render to platform</item>
    ///   <item><c>TProgram.Subscriptions(model)</c> → start initial subscriptions</item>
    ///   <item>Interpret initial command (may produce feedback messages)</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Two-phase initialization:</b> The runtime instance is constructed before
    /// the kernel runtime so that the observer can be an instance method (accessing
    /// <c>_currentDocument</c> and <c>_subscriptionState</c> fields directly). This
    /// avoids the stale-closure problem where lambda-captured local variables diverge
    /// from the runtime's fields after startup.
    /// </para>
    /// </remarks>
    /// <param name="apply">The platform-specific patch applicator.</param>
    /// <param name="interpreter">
    /// Converts commands into feedback messages. The interpreter is platform-specific:
    /// browser interpreters handle HTTP, localStorage, etc.; test interpreters are no-ops or mocks.
    /// </param>
    /// <param name="argument">Initialization parameters passed to <c>TProgram.Initialize</c>.</param>
    /// <param name="titleChanged">Optional callback invoked when the document title changes.</param>
    /// <param name="threadSafe">
    /// When <c>true</c>, all dispatch calls are serialized via a semaphore.
    /// Defaults to <c>false</c> for WASM's single-threaded environment.
    /// </param>
    /// <returns>The started runtime, ready to receive messages via <see cref="Dispatch"/>.</returns>
    public static async Task<AbiesRuntime<TProgram, TModel, TArgument>> Start(
        Apply apply,
        Interpreter<Command, Message> interpreter,
        TArgument argument = default!,
        Action<string>? titleChanged = null,
        bool threadSafe = false)
    {
        using var activity = _activitySource.StartActivity("Abies.Start");
        activity?.SetTag("abies.program", typeof(TProgram).Name);

        // Phase 1: Create the runtime shell (observer needs 'this' reference)
        var runtime = new AbiesRuntime<TProgram, TModel, TArgument>(apply, titleChanged);

        // Wire the handler registry's dispatch to the runtime (needed for event delegation)
        HandlerRegistry.Dispatch = runtime.DispatchFromSubscription;

        // Phase 2: Initialize the program
        var (model, initialCommand) = TProgram.Initialize(argument);

        // Phase 3: Render initial view and apply
        var document = TProgram.View(model);
        var patches = Operations.Diff(null, document.Body);

        // Register all event handlers from the initial view tree
        HandlerRegistry.RegisterHandlers(document.Body);

        apply(patches);
        runtime._currentDocument = document;

        // Set initial title
        titleChanged?.Invoke(document.Title);

        // Phase 4: Wire the kernel runtime with the instance-method observer
        runtime._core = new AutomatonRuntime<TProgram, TModel, Message, Command, TArgument>(
            model, runtime.Observe, interpreter,
            threadSafe: threadSafe,
            trackEvents: false);

        // Phase 5: Start initial subscriptions
        var initialSubscriptions = TProgram.Subscriptions(model);
        runtime._subscriptionState = SubscriptionManager.Start(
            initialSubscriptions, runtime.DispatchFromSubscription);

        // Phase 6: Interpret initial command (may produce feedback messages → re-enter loop)
        await runtime._core.InterpretEffect(initialCommand);

        activity?.SetStatus(ActivityStatusCode.Ok);

        return runtime;
    }

    /// <summary>
    /// Dispatches a message into the MVU loop: transition → render → diff → apply → subscriptions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <c>Ok(Unit)</c> on success or <c>Err(PipelineError)</c> if the observer
    /// or interpreter pipeline reports a failure. The model has already advanced
    /// regardless of observer/interpreter errors (transitions are pure and committed
    /// immediately).
    /// </para>
    /// </remarks>
    /// <param name="message">The message to dispatch.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public ValueTask<Result<Unit, PipelineError>> Dispatch(
        Message message, CancellationToken cancellationToken = default) =>
        _core.Dispatch(message, cancellationToken);

    /// <summary>
    /// Stops the runtime: cancels all running subscriptions and disposes resources.
    /// </summary>
    /// <remarks>
    /// After disposal, no further messages should be dispatched.
    /// </remarks>
    public void Dispose()
    {
        using var activity = _activitySource.StartActivity("Abies.Stop");

        SubscriptionManager.Stop(_subscriptionState);
        HandlerRegistry.Dispatch = null;
        HandlerRegistry.Clear();
        _core.Dispose();

        activity?.SetStatus(ActivityStatusCode.Ok);
    }
}
