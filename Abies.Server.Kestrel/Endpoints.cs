// =============================================================================
// Endpoints — MapAbies Extension Method for ASP.NET Core
// =============================================================================
// Provides the single-call entry point for hosting an Abies application
// in a Kestrel-based ASP.NET Core server:
//
//     app.MapAbies<MyApp, MyModel, Unit>("/", new RenderMode.InteractiveServer());
//
// This one call wires up:
//   1. A GET endpoint at {path} that serves the server-rendered HTML page
//   2. A WebSocket endpoint at {wsPath} for interactive modes
//
// For Static mode, only the GET endpoint is created (no WebSocket).
// For InteractiveServer/InteractiveAuto, both endpoints are created.
// For InteractiveWasm, only the GET endpoint is created (WASM handles interactivity).
//
// Architecture: This is the "adapter" side of Ports & Adapters. It maps
// ASP.NET Core's HTTP/WebSocket primitives to the pure Abies.Server
// computational library via the delegate types in Transport.cs.
//
// See also:
//   - Abies.Server/Page.cs — pure HTML rendering
//   - Abies.Server/Session.cs — pure MVU session
//   - WebSocketTransport.cs — WebSocket ↔ delegate adapter
// =============================================================================

using System.Diagnostics;
using Automaton;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Abies.Server.Kestrel;

/// <summary>
/// Extension methods for mapping Abies applications to ASP.NET Core endpoints.
/// </summary>
public static class Endpoints
{
    private static readonly ActivitySource _activitySource = new("Abies.Server.Kestrel.Endpoints");

    /// <summary>
    /// Maps an Abies MVU application to ASP.NET Core endpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Based on the <paramref name="mode"/>, this method creates:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Static</b>: A single GET endpoint serving pre-rendered HTML</item>
    ///   <item><b>InteractiveServer</b>: GET endpoint + WebSocket endpoint for live updates</item>
    ///   <item><b>InteractiveWasm</b>: GET endpoint only (WASM bootstrap script included)</item>
    ///   <item><b>InteractiveAuto</b>: GET endpoint + WebSocket endpoint (transitions to WASM)</item>
    /// </list>
    /// <example>
    /// <code>
    /// var app = builder.Build();
    /// app.MapAbies&lt;CounterProgram, CounterModel, Unit&gt;(
    ///     "/", new RenderMode.InteractiveServer());
    /// app.Run();
    /// </code>
    /// </example>
    /// </remarks>
    /// <typeparam name="TProgram">The Abies program type.</typeparam>
    /// <typeparam name="TModel">The application model.</typeparam>
    /// <typeparam name="TArgument">Initialization parameters.</typeparam>
    /// <param name="endpoints">The endpoint route builder (typically <c>WebApplication</c>).</param>
    /// <param name="path">The URL path to serve the application at (e.g., <c>"/"</c>).</param>
    /// <param name="mode">The render mode determining interactivity strategy.</param>
    /// <param name="interpreter">
    /// Command interpreter for side effects. Required for interactive modes;
    /// ignored for static mode. Defaults to a no-op interpreter.
    /// </param>
    /// <param name="argument">Initialization parameters for the program.</param>
    /// <returns>The endpoint route builder for further chaining.</returns>
    public static IEndpointRouteBuilder MapAbies<TProgram, TModel, TArgument>(
        this IEndpointRouteBuilder endpoints,
        string path,
        RenderMode mode,
        Interpreter<Command, Message>? interpreter = null,
        TArgument argument = default!)
        where TProgram : Program<TModel, TArgument>
    {
        var effectiveInterpreter = interpreter ?? NoOpInterpreter;

        // Always map the HTML page endpoint
        MapPageEndpoint<TProgram, TModel, TArgument>(endpoints, path, mode, argument);

        // Map WebSocket endpoint for interactive server modes
        switch (mode)
        {
            case RenderMode.InteractiveServer server:
                MapWebSocketEndpoint<TProgram, TModel, TArgument>(
                    endpoints, server.WebSocketPath, effectiveInterpreter, argument);
                break;

            case RenderMode.InteractiveAuto auto:
                MapWebSocketEndpoint<TProgram, TModel, TArgument>(
                    endpoints, auto.WebSocketPath, effectiveInterpreter, argument);
                break;
        }

        return endpoints;
    }

    /// <summary>
    /// Maps the HTML page GET endpoint that serves the initial server-rendered page.
    /// </summary>
    private static void MapPageEndpoint<TProgram, TModel, TArgument>(
        IEndpointRouteBuilder endpoints,
        string path,
        RenderMode mode,
        TArgument argument)
        where TProgram : Program<TModel, TArgument>
    {
        endpoints.MapGet(path, (HttpContext context) =>
        {
            using var activity = _activitySource.StartActivity("Abies.Kestrel.ServePage");
            activity?.SetTag("abies.program", typeof(TProgram).Name);
            activity?.SetTag("abies.path", path);
            activity?.SetTag("abies.renderMode", mode.GetType().Name);

            // Parse the request URL for route-aware rendering
            var requestUrl = Url.FromUri(new Uri(
                $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}"));

            // Render the full HTML page using the pure computation
            var html = Page.Render<TProgram, TModel, TArgument>(
                mode, argument, initialUrl: requestUrl);

            activity?.SetStatus(ActivityStatusCode.Ok);

            return Results.Content(html, "text/html; charset=utf-8");
        });
    }

    /// <summary>
    /// Maps the WebSocket endpoint that handles interactive server sessions.
    /// Each WebSocket connection gets its own Session with isolated state.
    /// </summary>
    private static void MapWebSocketEndpoint<TProgram, TModel, TArgument>(
        IEndpointRouteBuilder endpoints,
        string wsPath,
        Interpreter<Command, Message> interpreter,
        TArgument argument)
        where TProgram : Program<TModel, TArgument>
    {
        endpoints.Map(wsPath, async (HttpContext context) =>
        {
            using var activity = _activitySource.StartActivity("Abies.Kestrel.WebSocketSession");
            activity?.SetTag("abies.program", typeof(TProgram).Name);
            activity?.SetTag("abies.wsPath", wsPath);

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // Accept the WebSocket upgrade
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

            // Create the transport adapter
            using var transport = new WebSocketTransport(webSocket);

            // Parse the initial URL from the Referer header (the page URL)
            Url? initialUrl = null;
            if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
                Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var originUri))
            {
                initialUrl = Url.FromUri(originUri);
            }

            activity?.SetTag("abies.initialUrl", initialUrl?.ToString());

            // Start a new session — each connection gets its own Runtime
            using var session = await Session.Start<TProgram, TModel, TArgument>(
                sendPatches: transport.CreateSendPatches(),
                receiveEvent: transport.CreateReceiveEvent(),
                interpreter: interpreter,
                argument: argument,
                initialUrl: initialUrl);

            // Run the event loop until the client disconnects
            await session.RunEventLoop(context.RequestAborted);

            // Graceful close
            await transport.CloseAsync();

            activity?.SetStatus(ActivityStatusCode.Ok);
        });
    }

    /// <summary>
    /// Default no-op interpreter: returns empty message arrays for all commands.
    /// </summary>
    private static ValueTask<Result<Message[], PipelineError>> NoOpInterpreter(Command _) =>
        new(Result<Message[], PipelineError>.Ok([]));
}
