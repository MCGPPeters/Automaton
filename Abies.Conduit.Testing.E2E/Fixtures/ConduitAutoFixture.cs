// =============================================================================
// ConduitAutoFixture — E2E Test Infrastructure for InteractiveAuto Conduit
// =============================================================================
// Starts a Kestrel server hosting the Conduit app in InteractiveAuto mode.
//
// InteractiveAuto mode: starts with server-side interactivity (WebSocket),
// then transitions to WASM once the client runtime is ready. This fixture
// sets up both WebSocket and WASM file serving plus the API reverse proxy.
//
// Same port 5000 requirement as InteractiveServer, since the interpreter
// runs server-side initially (before WASM takeover).
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
/// Shared fixture that starts the Conduit app in InteractiveAuto mode for E2E testing.
/// </summary>
public sealed class ConduitAutoFixture : IAsyncLifetime
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
    /// After WASM takeover, the interpreter runs in-browser and calls
    /// localhost:5000/api/** which the Kestrel proxy still forwards.
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
            Console.WriteLine($"[Browser:Auto {msg.Type}] {msg.Text}");

        page.PageError += (_, error) =>
            Console.WriteLine($"[Browser:Auto ERROR] {error}");

        return page;
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _infra = await SharedInfra.GetAsync();

        const int port = 5000;
        BaseUrl = $"http://localhost:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(BaseUrl);

        _app = builder.Build();

        // Reverse proxy for API calls (server-side initially, then WASM)
        ConduitServerFixture.AddApiReverseProxy(_app, _infra.ApiUrl);

        // InteractiveAuto needs both WebSocket and WASM
        _app.UseWebSockets();

        var solutionDir = FindSolutionDirectory();

        var configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif

        var wasmAppBundlePath = Path.GetFullPath(Path.Combine(
            solutionDir,
            "Abies.Conduit.Wasm", "bin", configuration,
            "net10.0", "browser-wasm", "AppBundle"));

        _app.UseAbiesWasmFiles(wasmAppBundlePath);
        _app.UseAbiesStaticFiles();

        _app.MapAbies<ConduitProgram, Model, Unit>(
            "/{**catch-all}",
            new RenderMode.InteractiveAuto(),
            interpreter: ConduitInterpreter.Interpret);

        _ = Task.Run(async () =>
        {
            try
            {
                await _app.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConduitAutoFixture] Kestrel error: {ex}");
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
                Console.WriteLine($"[ConduitAutoFixture] Health check {url} → {status}");
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

    private static string FindSolutionDirectory()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Automaton.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException(
            "Could not find solution directory (containing Automaton.sln).");
    }
}
