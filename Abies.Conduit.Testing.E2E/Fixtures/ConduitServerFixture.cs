// =============================================================================
// ConduitServerFixture — E2E Test Infrastructure for InteractiveServer Conduit
// =============================================================================
// Starts a Kestrel server hosting the Conduit app in InteractiveServer mode.
//
// Key challenge: The ConduitInterpreter uses HttpClient to call the API at
// http://localhost:5000 (hardcoded in ConduitProgram.Initialize). In server
// mode, the interpreter runs server-side, so Playwright route interception
// cannot redirect these calls.
//
// Solution: Start Kestrel ON port 5000 with reverse proxy middleware that
// forwards /api/** requests to the Aspire-managed backend. This way, when
// ConduitInterpreter calls http://localhost:5000/api/articles, the request
// hits our Kestrel middleware which proxies it to the real Aspire API.
//
// Flow:
//   Browser ←→ Kestrel:5000 (InteractiveServer, WebSocket MVU session)
//   MVU session → ConduitInterpreter → HttpClient → http://localhost:5000/api/**
//   Kestrel middleware catches /api/** → forwards to Aspire API
// =============================================================================

using Abies.Conduit.App;
using Abies.Server;
using Abies.Server.Kestrel;
using Automaton;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

namespace Abies.Conduit.Testing.E2E.Fixtures;

/// <summary>
/// Shared fixture that starts the Conduit app in InteractiveServer mode
/// with API reverse proxying to the shared Aspire backend.
/// </summary>
public sealed class ConduitServerFixture : IAsyncLifetime
{
    private ConduitInfraFixture? _infra;
    private WebApplication? _app;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary>The base URL of the Kestrel server hosting the Conduit app.</summary>
    public string BaseUrl { get; private set; } = "";

    /// <summary>The Aspire-managed API URL (for ApiSeeder).</summary>
    public string ApiUrl => _infra?.ApiUrl ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>
    /// Creates a new Playwright browser context with an isolated page.
    /// No route interception needed — API calls go through the Kestrel proxy.
    /// </summary>
    public async Task<IPage> CreatePageAsync()
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true
        });

        var page = await context.NewPageAsync();

        page.Console += (_, msg) =>
            Console.WriteLine($"[Browser:Server {msg.Type}] {msg.Text}");

        page.PageError += (_, error) =>
            Console.WriteLine($"[Browser:Server ERROR] {error}");

        return page;
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        // Get the shared Aspire backend (starts once per test run)
        _infra = await SharedInfra.GetAsync();

        // The ConduitProgram hardcodes apiUrl = "http://localhost:5000".
        // We must start Kestrel on port 5000 so the interpreter's HTTP
        // calls resolve to our server, where the proxy middleware
        // forwards them to the actual Aspire API.
        const int port = 5000;
        BaseUrl = $"http://localhost:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(BaseUrl);

        _app = builder.Build();

        // Reverse proxy: forward /api/** to the Aspire-managed API.
        AddApiReverseProxy(_app, _infra.ApiUrl);

        _app.UseWebSockets();
        _app.UseAbiesStaticFiles();
        _app.MapAbies<ConduitProgram, Model, Unit>(
            "/{**catch-all}",
            new RenderMode.InteractiveServer(),
            interpreter: ConduitInterpreter.Interpret);

        _ = Task.Run(async () =>
        {
            try
            {
                await _app.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServerFixture] Kestrel server crashed: {ex}");
            }
        });
        await WaitForServerReady(BaseUrl);

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("HEADED") != "1",
            SlowMo = Environment.GetEnvironmentVariable("HEADED") == "1" ? 300 : 0
        });
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();

        _playwright?.Dispose();

        if (_app is not null)
            await _app.DisposeAsync();
    }

    /// <summary>
    /// Adds reverse proxy middleware that forwards /api/** to the Aspire backend.
    /// Used by server-hosted modes where the interpreter runs server-side.
    /// </summary>
    internal static void AddApiReverseProxy(WebApplication app, string targetApiUrl)
    {
        app.Map("/api/{**remainder}", async (HttpContext context) =>
        {
            // Extract the remainder from the route values manually to avoid
            // minimal API parameter binding, which can interfere with body reading
            var remainder = context.Request.RouteValues["remainder"]?.ToString() ?? "";
            using var proxyClient = new HttpClient();
            var targetUrl = $"{targetApiUrl}/api/{remainder}{context.Request.QueryString}";

            var proxyRequest = new HttpRequestMessage(
                new HttpMethod(context.Request.Method), targetUrl);

            // Read the full body first (before forwarding headers)
            byte[]? bodyBytes = null;
            if (context.Request.ContentLength > 0 || context.Request.ContentType is not null)
            {
                using var ms = new MemoryStream();
                await context.Request.Body.CopyToAsync(ms);
                bodyBytes = ms.ToArray();
            }

            // Forward select request headers
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.StartsWith("Host", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    continue; // Content headers go on Content, not on request
                proxyRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            // Set the body with proper content headers
            if (bodyBytes is { Length: > 0 })
            {
                proxyRequest.Content = new ByteArrayContent(bodyBytes);
                if (context.Request.ContentType is not null)
                    proxyRequest.Content.Headers.ContentType =
                        System.Net.Http.Headers.MediaTypeHeaderValue.Parse(context.Request.ContentType);
            }

            Console.WriteLine($"[Proxy] {context.Request.Method} {targetUrl} (body: {bodyBytes?.Length ?? 0} bytes, ct: {context.Request.ContentType})");
            if (bodyBytes is { Length: > 0 })
                Console.WriteLine($"[Proxy] Body: {System.Text.Encoding.UTF8.GetString(bodyBytes)}");

            var proxyResponse = await proxyClient.SendAsync(proxyRequest);

            var statusCode = (int)proxyResponse.StatusCode;
            Console.WriteLine($"[Proxy] → {statusCode}");
            if (statusCode >= 400)
            {
                var body = await proxyResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"[Proxy] Error body: {body}");
            }

            context.Response.StatusCode = statusCode;

            foreach (var header in proxyResponse.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();

            foreach (var header in proxyResponse.Content.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();

            // Remove transfer-encoding since we buffer the response
            context.Response.Headers.Remove("transfer-encoding");

            await proxyResponse.Content.CopyToAsync(context.Response.Body);
        });
    }

    private static async Task WaitForServerReady(string url, int timeoutSeconds = 60)
    {
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        string lastError = "no attempts made";

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await http.GetAsync(url);
                Console.WriteLine($"[ServerFixture] Health check {url} → {(int)response.StatusCode} {response.StatusCode}");
                if ((int)response.StatusCode < 500)
                    return; // Any non-server-error means the server is up
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Console.WriteLine($"[ServerFixture] Health check {url} → {ex.GetType().Name}: {ex.Message}");
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Conduit server at {url} did not start within {timeoutSeconds} seconds. Last error: {lastError}");
    }
}
