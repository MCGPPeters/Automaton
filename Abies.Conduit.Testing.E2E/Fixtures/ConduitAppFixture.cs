// =============================================================================
// ConduitAppFixture — Aspire-Based E2E Test Infrastructure
// =============================================================================
// Starts the full Conduit stack via Aspire:
//   - KurrentDB (container) — event store
//   - PostgreSQL (container) — read model
//   - Conduit API — REST backend
//   - Static file server — WASM frontend
//   - Playwright browser — test automation
//
// Uses DistributedApplicationTestingBuilder for full-stack orchestration.
// The WASM app is published once and served as static files.
// API calls from the WASM app are intercepted via Playwright route
// and redirected to the Aspire-managed API endpoint.
// =============================================================================

using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Playwright;

namespace Abies.Conduit.Testing.E2E.Fixtures;

/// <summary>
/// Shared fixture that starts the full Conduit stack for E2E testing.
/// Implements <see cref="IAsyncLifetime"/> for xUnit lifecycle management.
/// </summary>
public sealed class ConduitAppFixture : IAsyncLifetime
{
    private DistributedApplication? _app;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary>The base URL of the Conduit API (Aspire-managed).</summary>
    public string ApiUrl { get; private set; } = "";

    /// <summary>The base URL of the WASM frontend (static file server).</summary>
    public string FrontendUrl { get; private set; } = "";

    /// <summary>
    /// Creates a new Playwright browser context with API route interception configured.
    /// Each test should call this to get an isolated browser context.
    /// </summary>
    public async Task<IPage> CreatePageAsync()
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = FrontendUrl,
            IgnoreHTTPSErrors = true
        });

        var page = await context.NewPageAsync();

        // Log browser console messages for diagnostics
        page.Console += (_, msg) =>
            Console.WriteLine($"[Browser {msg.Type}] {msg.Text}");

        page.PageError += (_, error) =>
            Console.WriteLine($"[Browser ERROR] {error}");

        // Intercept API calls from the WASM app and redirect to the Aspire-managed API.
        // The WASM app hardcodes http://localhost:5000 as the API URL.
        // We rewrite those requests to point at the real Aspire-assigned API endpoint.
        await page.RouteAsync("http://localhost:5000/api/**", async route =>
        {
            var originalUrl = route.Request.Url;
            var redirectedUrl = originalUrl.Replace("http://localhost:5000", ApiUrl);

            // Forward the request to the real API
            var response = await route.FetchAsync(new RouteFetchOptions
            {
                Url = redirectedUrl
            });

            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = response.Status,
                Headers = response.Headers,
                BodyBytes = await response.BodyAsync()
            });
        });

        return page;
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        // ─── Start the Aspire AppHost ──────────────────────────────────
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Abies_AppHost>();

        // Build and start the distributed application
        _app = await builder.BuildAsync();
        await _app.StartAsync();

        // Get the API endpoint from Aspire
        var apiEndpoint = _app.GetEndpoint("conduit-api", "http");
        ApiUrl = apiEndpoint.ToString().TrimEnd('/');

        // Log the KurrentDB endpoint for diagnostics
        try
        {
            var kurrentDbEndpoint = _app.GetEndpoint("kurrentdb", "http");
            Console.WriteLine($"[E2E Fixture] KurrentDB endpoint: {kurrentDbEndpoint}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[E2E Fixture] Could not get KurrentDB endpoint: {ex.Message}");
        }

        // Wait for the API to be healthy
        await WaitForApiHealthy(ApiUrl);

        // ─── Publish and serve the WASM frontend ──────────────────────
        FrontendUrl = await PublishAndServeWasmApp();

        // ─── Set up Playwright ─────────────────────────────────────────
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("HEADED") == "1" ? false : true,
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
    /// Polls the API until it can handle both reads AND writes, or timeout expires.
    /// First checks the read endpoint (/api/tags), then performs a test registration
    /// to verify the event store and read model are fully operational.
    /// </summary>
    private static async Task WaitForApiHealthy(string apiUrl, int timeoutSeconds = 300)
    {
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var started = DateTime.UtcNow;

        // Phase 1: Wait for the API process to respond at all
        Console.WriteLine($"[E2E Fixture] Phase 1: Waiting for API at {apiUrl} to respond...");
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await http.GetAsync($"{apiUrl}/api/tags");
                if (response.IsSuccessStatusCode)
                {
                    var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                    Console.WriteLine($"[E2E Fixture] Phase 1 complete: API responding after {elapsed:F1}s");
                    break;
                }
            }
            catch
            {
                // API not ready yet
            }

            await Task.Delay(500);
        }

        // Phase 2: Wait for write path (event store + read model) to be ready
        // by attempting a probe registration
        Console.WriteLine("[E2E Fixture] Phase 2: Waiting for write path (KurrentDB) to be ready...");
        var probeId = Guid.NewGuid().ToString("N")[..10];
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await http.PostAsJsonAsync(
                    $"{apiUrl}/api/users",
                    new { user = new { username = $"probe{probeId}", email = $"probe{probeId}@test.com", password = "probe12345" } });

                if (response.IsSuccessStatusCode || (int)response.StatusCode == 422)
                {
                    var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                    Console.WriteLine($"[E2E Fixture] Phase 2 complete: Write path ready after {elapsed:F1}s (status: {(int)response.StatusCode})");
                    return; // 200 = success, 422 = validation error (infra is fine)
                }

                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[E2E Fixture] Write probe returned {(int)response.StatusCode} at {(DateTime.UtcNow - started).TotalSeconds:F1}s, retrying...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[E2E Fixture] Write probe error: {ex.GetType().Name}, retrying...");
            }

            await Task.Delay(2000);
        }

        throw new TimeoutException(
            $"Conduit API at {apiUrl} did not become fully healthy within {timeoutSeconds} seconds.");
    }

    /// <summary>
    /// Publishes the WASM app and starts a simple static file server.
    /// Returns the base URL of the server.
    /// </summary>
    private static async Task<string> PublishAndServeWasmApp()
    {
        // Find the WASM project directory
        var solutionDir = FindSolutionDirectory();
        var wasmProject = Path.Combine(solutionDir, "Abies.Conduit.Wasm", "Abies.Conduit.Wasm.csproj");

        if (!File.Exists(wasmProject))
            throw new FileNotFoundException(
                $"WASM project not found at {wasmProject}. Ensure the project exists.");

        // Publish the WASM app (no -o flag — browser-wasm puts AppBundle under bin/)
        var publishProcess = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"publish \"{wasmProject}\" -c Release",
                WorkingDirectory = solutionDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        publishProcess.Start();
        var stdout = await publishProcess.StandardOutput.ReadToEndAsync();
        var stderr = await publishProcess.StandardError.ReadToEndAsync();
        await publishProcess.WaitForExitAsync();

        if (publishProcess.ExitCode != 0)
            throw new InvalidOperationException(
                $"WASM publish failed (exit code {publishProcess.ExitCode}):\n{stderr}\n{stdout}");

        // Find the AppBundle directory under the WASM project bin output
        var wasmProjectDir = Path.Combine(solutionDir, "Abies.Conduit.Wasm");
        var appBundleDir = Path.Combine(wasmProjectDir, "bin", "Release", "net10.0", "browser-wasm", "AppBundle");
        if (!Directory.Exists(appBundleDir))
        {
            // Fallback: search recursively
            var candidates = Directory.GetDirectories(wasmProjectDir, "AppBundle", SearchOption.AllDirectories);
            appBundleDir = candidates.Length > 0
                ? candidates[0]
                : throw new DirectoryNotFoundException(
                    $"AppBundle directory not found under {wasmProjectDir}.");
        }

        // Start a simple static file server using dotnet-serve or a minimal Kestrel host
        var port = GetAvailablePort();
        var serverUrl = $"http://localhost:{port}";

        _ = Task.Run(() => StartStaticFileServer(appBundleDir, port));

        // Wait for the server to be ready
        await WaitForServerReady(serverUrl);

        return serverUrl;
    }

    /// <summary>
    /// Starts a minimal Kestrel static file server for the WASM AppBundle.
    /// Configures MIME types for all Blazor WASM artifacts (.wasm, .dat, .blat, etc.).
    /// </summary>
    private static async Task StartStaticFileServer(string rootPath, int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);

        var app = builder.Build();

        // Configure MIME type provider with all WASM-specific types
        var mimeProvider = new FileExtensionContentTypeProvider();
        mimeProvider.Mappings[".wasm"] = "application/wasm";
        mimeProvider.Mappings[".dat"] = "application/octet-stream";
        mimeProvider.Mappings[".blat"] = "application/octet-stream";
        mimeProvider.Mappings[".dll"] = "application/octet-stream";
        mimeProvider.Mappings[".pdb"] = "application/octet-stream";
        mimeProvider.Mappings[".json"] = "application/json";
        mimeProvider.Mappings[".js"] = "application/javascript";

        // Request logging middleware — logs every request for diagnostics
        app.Use(async (context, next) =>
        {
            await next();
            var path = context.Request.Path.Value;
            var status = context.Response.StatusCode;
            var contentType = context.Response.ContentType ?? "unknown";
            Console.WriteLine($"[Static Server] {context.Request.Method} {path} → {status} ({contentType})");
        });

        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(rootPath)
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(rootPath),
            ContentTypeProvider = mimeProvider,
            ServeUnknownFileTypes = true // Fallback for any other types
        });

        // SPA fallback — serve index.html for unmatched routes
        app.MapFallbackToFile("index.html", new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(rootPath)
        });

        await app.RunAsync();
    }

    /// <summary>
    /// Waits for the static file server to respond.
    /// </summary>
    private static async Task WaitForServerReady(string url, int timeoutSeconds = 30)
    {
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch
            {
                // Server not ready yet
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"Static file server at {url} did not start within {timeoutSeconds} seconds.");
    }

    /// <summary>
    /// Finds an available TCP port.
    /// </summary>
    private static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Walks up directories to find the solution root (containing Automaton.sln).
    /// </summary>
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
