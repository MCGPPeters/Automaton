// =============================================================================
// Authentication E2E Tests — InteractiveServer Mode
// =============================================================================
// Tests the same authentication user journeys as the WASM tests, but running
// the MVU loop server-side via WebSocket. The interpreter's HTTP calls go
// through the Kestrel reverse proxy to the Aspire backend.
// =============================================================================

using Abies.Conduit.Testing.E2E.Fixtures;
using Abies.Conduit.Testing.E2E.Helpers;
using Microsoft.Playwright;

namespace Abies.Conduit.Testing.E2E;

[Trait("Category", "E2E")]
[Collection("ConduitServer")]
public sealed class AuthenticationServerTests : IAsyncLifetime
{
    private readonly ConduitServerFixture _fixture;
    private IPage _page = null!;
    private ApiSeeder _seeder = null!;

    public AuthenticationServerTests(ConduitServerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _page = await _fixture.CreatePageAsync();
        _seeder = new ApiSeeder(_fixture.ApiUrl);
    }

    public async Task DisposeAsync() => await _page.Context.DisposeAsync();

    [Fact]
    public async Task Register_WithValidCredentials_ShouldNavigateToHomeWithAuthenticatedNav()
    {
        await _page.GotoAsync("/register");
        await _page.WaitForSelectorAsync("h1:has-text('Sign up')");

        var uniqueName = $"srvreguser{Guid.NewGuid():N}"[..20];
        var email = $"{uniqueName}@test.com";

        // In InteractiveServer mode, each input event round-trips through
        // the WebSocket and causes a DOM patch. We must wait for the server
        // to process and patch back before filling the next field.
        await _page.GetByPlaceholder("Your Name").FillAndWaitForPatchAsync(uniqueName);
        await _page.GetByPlaceholder("Email").FillAndWaitForPatchAsync(email);
        await _page.GetByPlaceholder("Password").FillAndWaitForPatchAsync("password123");
        await _page.GetByRole(AriaRole.Button, new() { Name = "Sign up" }).ClickAsync();

        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 15000 });

        await Expect(_page.Locator(".navbar").GetByText(uniqueName))
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(_page.Locator(".navbar")).ToContainTextAsync("Settings");
        await Expect(_page.Locator(".navbar")).ToContainTextAsync("New Article");
    }

    [Fact]
    public async Task Login_WithValidCredentials_ShouldNavigateToHomeWithAuthenticatedNav()
    {
        var username = $"srvlogin{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        const string password = "password123";
        await _seeder.RegisterUserAsync(username, email, password);

        await _page.GotoAsync("/login");
        await _page.WaitForSelectorAsync("h1:has-text('Sign in')");

        await _page.GetByPlaceholder("Email").FillAndWaitForPatchAsync(email);
        await _page.GetByPlaceholder("Password").FillAndWaitForPatchAsync(password);
        await _page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();

        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 15000 });
        await Expect(_page.Locator(".navbar")).ToContainTextAsync(username);
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ShouldShowErrors()
    {
        await _page.GotoAsync("/login");
        await _page.WaitForSelectorAsync("h1:has-text('Sign in')");

        await _page.GetByPlaceholder("Email").FillAndWaitForPatchAsync("nonexistent@test.com");
        await _page.GetByPlaceholder("Password").FillAndWaitForPatchAsync("wrongpassword");
        await _page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();

        await Expect(_page.Locator(".error-messages")).ToBeVisibleAsync(
            new() { Timeout = 10000 });
    }

    [Fact]
    public async Task Logout_FromSettings_ShouldClearSessionAndNavigateToHome()
    {
        var username = $"srvlogout{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        await _seeder.RegisterUserAsync(username, email, "password123");

        await LoginViaUi(email, "password123");

        await _page.Locator(".navbar").GetByText("Settings").ClickAsync();
        await _page.WaitForSelectorAsync(".settings-page", new() { Timeout = 10000 });

        await _page.GetByRole(AriaRole.Button, new() { Name = "Or click here to logout." })
            .ClickAsync();

        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 10000 });
        await Expect(_page.Locator(".navbar")).ToContainTextAsync("Sign in");
        await Expect(_page.Locator(".navbar")).ToContainTextAsync("Sign up");
    }

    private async Task LoginViaUi(string email, string password)
    {
        await _page.GotoAsync("/login");
        await _page.WaitForSelectorAsync("h1:has-text('Sign in')");
        await _page.GetByPlaceholder("Email").FillAndWaitForPatchAsync(email);
        await _page.GetByPlaceholder("Password").FillAndWaitForPatchAsync(password);
        await _page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 15000 });
    }

    private static ILocatorAssertions Expect(ILocator locator) =>
        Assertions.Expect(locator);
}
