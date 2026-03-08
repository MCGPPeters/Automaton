// =============================================================================
// Abies Runtime — The MVU Execution Loop
// =============================================================================
// The Runtime orchestrates the MVU loop by composing the Automaton kernel's
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
//     Server  (Abies.Server):   Apply produces binary patches sent over transport
//     Tests:                    Apply captures patches for assertions
//
// Each runtime instance owns its own HandlerRegistry for event handler
// registration and dispatch. This enables concurrent server-side sessions
// to have isolated handler state. In WASM (single-threaded), the browser
// runtime simply uses its single instance.
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
// Server-side sessions use threadSafe: true for async I/O thread safety.
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

// =============================================================================
// Head Content Diffing
// =============================================================================
// Pure functions for computing the delta between two HeadContent arrays.
// Head patches are standard Patch types (AddHeadElement, UpdateHeadElement,
// RemoveHeadElement) that flow through the same binary batch protocol as
// body patches — a single interop call per render cycle.
// =============================================================================

/// <summary>
/// Pure functions for diffing <see cref="HeadContent"/> arrays between renders.
/// </summary>
public static class HeadDiff
{
    /// <summary>
    /// Computes the delta between old and new head content arrays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses the <see cref="HeadContent.Key"/> property for identity matching.
    /// Elements present in both old and new are compared by value equality
    /// (record equality). Elements only in old are removed; only in new are added.
    /// </para>
    /// <para>
    /// Returns standard <see cref="Patch"/> types (<see cref="AddHeadElement"/>,
    /// <see cref="UpdateHeadElement"/>, <see cref="RemoveHeadElement"/>) so head
    /// changes flow through the same binary batch protocol as body patches.
    /// </para>
    /// </remarks>
    /// <param name="oldHead">The previous head content (empty array if first render).</param>
    /// <param name="newHead">The desired head content.</param>
    /// <returns>A list of patches to apply via the binary protocol.</returns>
    public static IReadOnlyList<Patch> Diff(
        ReadOnlySpan<HeadContent> oldHead,
        ReadOnlySpan<HeadContent> newHead)
    {
        // Fast path: both empty — no changes
        if (oldHead.Length == 0 && newHead.Length == 0)
            return [];

        // Fast path: old empty — add all new
        if (oldHead.Length == 0)
        {
            var adds = new Patch[newHead.Length];
            for (var i = 0; i < newHead.Length; i++)
                adds[i] = new AddHeadElement(newHead[i]);
            return adds;
        }

        // Fast path: new empty — remove all old
        if (newHead.Length == 0)
        {
            var removes = new Patch[oldHead.Length];
            for (var i = 0; i < oldHead.Length; i++)
                removes[i] = new RemoveHeadElement(oldHead[i].Key);
            return removes;
        }

        // Build lookup from old head by key
        var oldByKey = new Dictionary<string, HeadContent>(oldHead.Length);
        for (var i = 0; i < oldHead.Length; i++)
            oldByKey[oldHead[i].Key] = oldHead[i];

        var patches = new List<Patch>();
        var seenKeys = new HashSet<string>(newHead.Length);

        // Walk new head: add or update
        for (var i = 0; i < newHead.Length; i++)
        {
            var item = newHead[i];
            seenKeys.Add(item.Key);

            if (oldByKey.TryGetValue(item.Key, out var existing))
            {
                // Key exists — update only if content changed
                if (!existing.Equals(item))
                    patches.Add(new UpdateHeadElement(item));
            }
            else
            {
                // New key — add
                patches.Add(new AddHeadElement(item));
            }
        }

        // Walk old head: remove keys not in new
        for (var i = 0; i < oldHead.Length; i++)
        {
            if (!seenKeys.Contains(oldHead[i].Key))
                patches.Add(new RemoveHeadElement(oldHead[i].Key));
        }

        return patches;
    }
}

/// <summary>
/// The MVU runtime: wires the Automaton kernel to View, Diff, and Subscriptions.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Runtime{TProgram,TModel,TArgument}"/> is a thin orchestration
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
/// <b>Navigation commands</b> (<see cref="NavigationCommand"/>) are handled by the
/// runtime's built-in interpreter before falling through to the caller-supplied
/// interpreter. This means applications never need to handle navigation commands
/// manually — they are framework concerns.
/// </para>
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
/// var runtime = await Runtime&lt;Counter, CounterModel, Unit&gt;.Start(
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
public sealed class Runtime<TProgram, TModel, TArgument> : IDisposable
    where TProgram : Program<TModel, TArgument>
{
    private static readonly ActivitySource _activitySource = new("Abies.Runtime");

    private AutomatonRuntime<TProgram, TModel, Message, Command, TArgument> _core = null!;
    private readonly Apply _apply;
    private readonly Action<string>? _titleChanged;
    private readonly Action<NavigationCommand>? _navigationExecutor;
    private readonly HandlerRegistry _handlerRegistry;
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
    /// The handler registry for this runtime instance.
    /// </summary>
    /// <remarks>
    /// Exposed so hosting adapters (browser interop, server session) can look up
    /// handlers by commandId to create messages from DOM events.
    /// </remarks>
    public HandlerRegistry Handlers => _handlerRegistry;

    /// <summary>
    /// Private constructor — use <see cref="Start"/> to create instances.
    /// The runtime uses two-phase initialization: construct first, then
    /// wire the kernel runtime via field assignment in <see cref="Start"/>.
    /// </summary>
    private Runtime(Apply apply, Action<string>? titleChanged, Action<NavigationCommand>? navigationExecutor) =>
        (_apply, _titleChanged, _navigationExecutor, _handlerRegistry) =
            (apply, titleChanged, navigationExecutor, new HandlerRegistry());

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

        // Diff body against previous
        var patches = Operations.Diff(_currentDocument?.Body, newDocument.Body);

        // Diff head content and merge into the same patch list
        var headPatches = HeadDiff.Diff(
            _currentDocument?.Head ?? [],
            newDocument.Head);

        // Merge body + head patches into a single list for the binary protocol
        List<Patch>? mergedPatches = null;
        if (headPatches.Count > 0)
        {
            mergedPatches = new List<Patch>(patches.Count + headPatches.Count);
            mergedPatches.AddRange(patches);
            mergedPatches.AddRange(headPatches);
        }

        var allPatches = mergedPatches is not null
            ? (IReadOnlyList<Patch>)mergedPatches
            : patches;

        // Update handler registry for new/removed event handlers
        UpdateHandlerRegistry(allPatches);

        // Apply all patches (body + head) to platform via a single binary batch
        if (allPatches.Count > 0)
        {
            _apply(allPatches);
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
    private void UpdateHandlerRegistry(IReadOnlyList<Patch> patches)
    {
        foreach (var patch in patches)
        {
            switch (patch)
            {
                case AddHandler p:
                    _handlerRegistry.Register(p.Handler);
                    break;

                case RemoveHandler p:
                    _handlerRegistry.Unregister(p.Handler.CommandId);
                    break;

                case UpdateHandler p:
                    _handlerRegistry.Unregister(p.OldHandler.CommandId);
                    _handlerRegistry.Register(p.NewHandler);
                    break;

                // When adding children, register all handlers in the new subtree
                case AddChild p:
                    _handlerRegistry.RegisterHandlers(p.Child);
                    break;

                case AddRoot p:
                    _handlerRegistry.RegisterHandlers(p.Element);
                    break;

                case ReplaceChild p:
                    _handlerRegistry.UnregisterHandlers(p.OldElement);
                    _handlerRegistry.RegisterHandlers(p.NewElement);
                    break;

                // When removing children, unregister all handlers in the old subtree
                case RemoveChild p:
                    _handlerRegistry.UnregisterHandlers(p.Child);
                    break;

                case ClearChildren p:
                    foreach (var child in p.OldChildren)
                    {
                        _handlerRegistry.UnregisterHandlers(child);
                    }
                    break;

                // SetChildrenHtml replaces all children — register all new handlers
                case SetChildrenHtml p:
                    foreach (var child in p.Children)
                    {
                        _handlerRegistry.RegisterHandlers(child);
                    }
                    break;

                // AppendChildrenHtml adds children to existing — register new handlers
                case AppendChildrenHtml p:
                    foreach (var child in p.Children)
                    {
                        _handlerRegistry.RegisterHandlers(child);
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
    /// <param name="navigationExecutor">
    /// Optional callback that executes navigation commands (pushState, replaceState, etc.).
    /// In the browser, this calls JS interop. In tests, this can be a no-op or a mock.
    /// When null, navigation commands are silently ignored.
    /// </param>
    /// <param name="initialUrl">
    /// Optional initial URL to dispatch as a <see cref="UrlChanged"/> message after startup.
    /// In the browser, this is <c>window.location</c>. In tests, this can be any URL.
    /// When null, no initial URL message is dispatched.
    /// </param>
    /// <param name="threadSafe">
    /// When <c>true</c>, all dispatch calls are serialized via a semaphore.
    /// Defaults to <c>false</c> for WASM's single-threaded environment.
    /// </param>
    /// <returns>The started runtime, ready to receive messages via <see cref="Dispatch"/>.</returns>
    public static async Task<Runtime<TProgram, TModel, TArgument>> Start(
        Apply apply,
        Interpreter<Command, Message> interpreter,
        TArgument argument = default!,
        Action<string>? titleChanged = null,
        Action<NavigationCommand>? navigationExecutor = null,
        Url? initialUrl = null,
        bool threadSafe = false)
    {
        using var activity = _activitySource.StartActivity("Abies.Start");
        activity?.SetTag("abies.program", typeof(TProgram).Name);

        // Phase 1: Create the runtime shell (observer needs 'this' reference)
        var runtime = new Runtime<TProgram, TModel, TArgument>(apply, titleChanged, navigationExecutor);

        // Wire the handler registry's dispatch to the runtime (needed for event delegation)
        runtime._handlerRegistry.Dispatch = runtime.DispatchFromSubscription;

        // Phase 2: Initialize the program
        var (model, initialCommand) = TProgram.Initialize(argument);

        // Phase 3: Render initial view and apply
        var document = TProgram.View(model);
        var patches = Operations.Diff(null, document.Body);

        // Register all event handlers from the initial view tree
        runtime._handlerRegistry.RegisterHandlers(document.Body);

        apply(patches);
        runtime._currentDocument = document;

        // Set initial title
        titleChanged?.Invoke(document.Title);

        // Apply initial head content via the same binary protocol
        var headPatches = HeadDiff.Diff([], document.Head);
        if (headPatches.Count > 0)
        {
            apply(headPatches);
        }

        // Phase 4: Wire the kernel runtime with the instance-method observer.
        // Wrap the caller-supplied interpreter with structural command handling:
        //   - Command.None  → no-op (identity element of the command monoid)
        //   - Command.Batch → flatten and interpret each sub-command, collecting all feedback messages
        //   - NavigationCommand → handled by the runtime's built-in executor
        //   - All other commands → fall through to the caller-supplied interpreter
        Interpreter<Command, Message> wrappedInterpreter = command =>
            InterpretCommand(command, interpreter, runtime._navigationExecutor);

        static async ValueTask<Result<Message[], PipelineError>> InterpretCommand(
            Command command,
            Interpreter<Command, Message> interpreter,
            Action<NavigationCommand>? navigationExecutor)
        {
            switch (command)
            {
                case Command.None:
                    return Result<Message[], PipelineError>.Ok([]);

                case Command.Batch batch:
                {
                    var allMessages = new List<Message>();
                    foreach (var sub in batch.Commands)
                    {
                        var result = await InterpretCommand(sub, interpreter, navigationExecutor);
                        if (result.IsErr)
                            return result;
                        if (result.Value.Length > 0)
                            allMessages.AddRange(result.Value);
                    }
                    return Result<Message[], PipelineError>.Ok(allMessages.ToArray());
                }

                case NavigationCommand navCommand:
                    navigationExecutor?.Invoke(navCommand);
                    return Result<Message[], PipelineError>.Ok([]);

                default:
                    return await interpreter(command);
            }
        }

        runtime._core = new AutomatonRuntime<TProgram, TModel, Message, Command, TArgument>(
            model, runtime.Observe, wrappedInterpreter,
            threadSafe: threadSafe,
            trackEvents: false);

        // Phase 5: Start initial subscriptions
        var initialSubscriptions = TProgram.Subscriptions(model);
        runtime._subscriptionState = SubscriptionManager.Start(
            initialSubscriptions, runtime.DispatchFromSubscription);

        // Phase 6: Interpret initial command (may produce feedback messages → re-enter loop)
        await runtime._core.InterpretEffect(initialCommand);

        // Phase 7: Dispatch initial URL as UrlChanged message so the application
        // can route based on the current page URL at startup
        if (initialUrl is not null)
        {
            await runtime.Dispatch(new UrlChanged(initialUrl));
        }

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
        _handlerRegistry.Dispatch = null;
        _handlerRegistry.Clear();
        _core.Dispose();

        activity?.SetStatus(ActivityStatusCode.Ok);
    }
}
