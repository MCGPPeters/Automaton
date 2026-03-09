// =============================================================================
// ConduitStaticFixture — E2E Test Infrastructure for Static Conduit
// =============================================================================
// Starts a Kestrel server hosting the Conduit app in Static mode.
//
// Static mode: the server runs ConduitProgram.Initialize() once to produce the
// initial model, renders the view to HTML, and serves it as a static page.
// No WebSocket, no WASM, no interactivity.
//
// The initial render triggers API calls (FetchArticles, FetchTags) through
// the ConduitInterpreter, so the reverse proxy is still needed for port 5000.
// =============================================================================

using Abies.Conduit.App;
using Abies.Server;
using Abies.Server.Kestrel;
using Automaton;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Playwright;

namespace Abies.Conduit.Testing.E2E.Fixtures;

/// <summary>
/// Shared fixture that starts the Conduit app in Static mode for E2E testing.
/// </summary>
public sealed class ConduitStaticFixture : IAsyncLifetime
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
            Console.WriteLine($"[Browser:Static {msg.Type}] {msg.Text}");

        page.PageError += (_, error) =>
            Console.WriteLine($"[Browser:Static ERROR] {error}");

        return page;
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _infra = await SharedInfra.GetAsync();

        // All Conduit fixtures share port 5000 because ConduitProgram.Initialize()
        // hardcodes apiUrl = "http://localhost:5000". The xunit.runner.json
        // disables collection parallelism so only one fixture runs at a time.
        const int port = 5000;
        BaseUrl = $"http://localhost:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(BaseUrl);

        _app = builder.Build();

        // Same reverse proxy as server mode — the interpreter calls localhost:5002
        ConduitServerFixture.AddApiReverseProxy(_app, _infra.ApiUrl);

        // Static mode — no WebSocket, no WASM files
        _app.MapAbies<ConduitProgram, Model, Unit>(
            "/{**catch-all}",
            new RenderMode.Static(),
            interpreter: ConduitInterpreter.Interpret);

        _ = Task.Run(async () =>
        {
            try
            {
                await _app.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConduitStaticFixture] Kestrel error: {ex}");
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

    private static async Task WaitForServerReady(string url, int timeoutSeconds = 60)
    {
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await http.GetAsync(url);
                var status = (int)response.StatusCode;
                Console.WriteLine($"[ConduitStaticFixture] Health check {url} → {status}");
                if (status < 500)
                    return;
            }
            catch
            {
                // Server not ready yet
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"Conduit server at {url} did not start within {timeoutSeconds} seconds.");
    }
}
