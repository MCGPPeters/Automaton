// =============================================================================
// Authentication E2E Tests — Register, Login, Logout
// =============================================================================
// Tests the full authentication user journeys through the browser:
//   - Register a new account → navigate to home with authenticated nav
//   - Login with existing credentials → navigate to home with authenticated nav
//   - Logout from settings → navigate to home with anonymous nav
//
// Uses API seeding for Login/Logout (per Playwright guidelines §7.1).
// Register is tested through the UI since that IS the feature under test.
// =============================================================================

using Abies.Conduit.Testing.E2E.Fixtures;
using Abies.Conduit.Testing.E2E.Helpers;
using Microsoft.Playwright;

namespace Abies.Conduit.Testing.E2E;

[Trait("Category", "E2E")]
[Collection("Conduit")]
public sealed class AuthenticationTests : IAsyncLifetime
{
    private readonly ConduitAppFixture _fixture;
    private IPage _page = null!;
    private ApiSeeder _seeder = null!;

    public AuthenticationTests(ConduitAppFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _page = await _fixture.CreatePageAsync();
        _seeder = new ApiSeeder(_fixture.ApiUrl);
    }

    public async Task DisposeAsync()
    {
        await _page.Context.DisposeAsync();
    }

    [Fact]
    public async Task Register_WithValidCredentials_ShouldNavigateToHomeWithAuthenticatedNav()
    {
        // Arrange — navigate to register page
        await _page.GotoAsync("/register");
        await _page.WaitForSelectorAsync("h1:has-text('Sign up')");

        // Act — fill and submit the registration form
        var uniqueName = $"e2euser{Guid.NewGuid():N}"[..20];
        var email = $"{uniqueName}@test.com";

        await _page.GetByPlaceholder("Your Name").FillAsync(uniqueName);
        await _page.GetByPlaceholder("Email").FillAsync(email);
        await _page.GetByPlaceholder("Password").FillAsync("password123");
        await _page.GetByRole(AriaRole.Button, new() { Name = "Sign up" }).ClickAsync();

        // Assert — should navigate to home and show authenticated nav
        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 15000 });

        // Wait for the authenticated navbar to fully render
        await Expect(_page.Locator(".navbar").GetByText(uniqueName))
            .ToBeVisibleAsync(new() { Timeout = 10000 });

        await Expect(_page.Locator(".navbar")).ToContainTextAsync("Settings");
        await Expect(_page.Locator(".navbar")).ToContainTextAsync("New Article");
    }

    [Fact]
    public async Task Login_WithValidCredentials_ShouldNavigateToHomeWithAuthenticatedNav()
    {
        // Arrange — seed a user via API
        var username = $"loginuser{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        const string password = "password123";
        await _seeder.RegisterUserAsync(username, email, password);

        // Navigate to login page
        await _page.GotoAsync("/login");
        await _page.WaitForSelectorAsync("h1:has-text('Sign in')");

        // Act — fill and submit the login form
        await _page.GetByPlaceholder("Email").FillAsync(email);
        await _page.GetByPlaceholder("Password").FillAsync(password);
        await _page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();

        // Assert — should navigate to home and show authenticated nav
        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 15000 });
        await Expect(_page.Locator(".navbar")).ToContainTextAsync(username);
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ShouldShowErrors()
    {
        // Arrange — navigate to login page
        await _page.GotoAsync("/login");
        await _page.WaitForSelectorAsync("h1:has-text('Sign in')");

        // Act — submit with wrong credentials
        await _page.GetByPlaceholder("Email").FillAsync("nonexistent@test.com");
        await _page.GetByPlaceholder("Password").FillAsync("wrongpassword");
        await _page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();

        // Assert — should show error messages
        await Expect(_page.Locator(".error-messages")).ToBeVisibleAsync(
            new() { Timeout = 10000 });
    }

    [Fact]
    public async Task Logout_FromSettings_ShouldClearSessionAndNavigateToHome()
    {
        // Arrange — register and login through the UI to establish a session
        var username = $"logoutuser{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        await _seeder.RegisterUserAsync(username, email, "password123");

        await _page.GotoAsync("/login");
        await _page.WaitForSelectorAsync("h1:has-text('Sign in')");
        await _page.GetByPlaceholder("Email").FillAsync(email);
        await _page.GetByPlaceholder("Password").FillAsync("password123");
        await _page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 15000 });

        // Navigate to settings
        await _page.Locator(".navbar").GetByText("Settings").ClickAsync();
        await _page.WaitForSelectorAsync(".settings-page", new() { Timeout = 10000 });

        // Act — click logout
        await _page.GetByRole(AriaRole.Button, new() { Name = "Or click here to logout." })
            .ClickAsync();

        // Assert — should navigate to home with anonymous nav
        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 10000 });
        await Expect(_page.Locator(".navbar")).ToContainTextAsync("Sign in");
        await Expect(_page.Locator(".navbar")).ToContainTextAsync("Sign up");
    }

    private static ILocatorAssertions Expect(ILocator locator) =>
        Assertions.Expect(locator);

    private static IPageAssertions Expect(IPage page) =>
        Assertions.Expect(page);
}
