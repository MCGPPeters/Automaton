// =============================================================================
// Editor E2E Tests — Create and edit articles
// =============================================================================

using Abies.Conduit.Testing.E2E.Fixtures;
using Abies.Conduit.Testing.E2E.Helpers;
using Microsoft.Playwright;

namespace Abies.Conduit.Testing.E2E;

[Trait("Category", "E2E")]
[Collection("Conduit")]
public sealed class EditorTests : IAsyncLifetime
{
    private readonly ConduitAppFixture _fixture;
    private IPage _page = null!;
    private ApiSeeder _seeder = null!;

    public EditorTests(ConduitAppFixture fixture)
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
    public async Task CreateArticle_WithAllFields_ShouldNavigateToArticlePage()
    {
        // Arrange — seed user and login
        var username = $"editor{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        await _seeder.RegisterUserAsync(username, email, "password123");
        await LoginViaUi(email, "password123");

        // Intercept network requests for diagnostics
        var apiRequests = new System.Collections.Concurrent.ConcurrentBag<(string Method, string Url, int Status, string Body)>();
        _page.Response += async (_, response) =>
        {
            if (response.Url.Contains("/api/"))
            {
                var body = "";
                try { body = await response.TextAsync(); } catch { body = "(unreadable)"; }
                apiRequests.Add((response.Request.Method, response.Url, response.Status, body.Length > 500 ? body[..500] : body));
            }
        };

        // Navigate to new article editor (SPA navigation to preserve session)
        await _page.NavigateInAppAsync("/editor");
        await _page.WaitForSelectorAsync(".editor-page", new() { Timeout = 10000 });
        await _page.GetByPlaceholder("Article Title").WaitForAsync(new() { Timeout = 10000 });

        // Act — fill the article form
        var title = $"E2E Test Article {Guid.NewGuid():N}"[..40];
        const string description = "A description for E2E testing";
        const string body = "This article was created by an E2E test.";

        await _page.GetByPlaceholder("Article Title").FillAsync(title);
        await _page.GetByPlaceholder("What's this article about?").FillAsync(description);
        await _page.GetByPlaceholder("Write your article (in markdown)").FillAsync(body);

        // Add tags
        var tagInput = _page.GetByPlaceholder("Enter tags");
        await tagInput.FillAsync("e2e");
        await tagInput.PressAsync("Enter");
        await tagInput.FillAsync("testing");
        await tagInput.PressAsync("Enter");

        // Submit — click the publish button
        var publishBtn = _page.GetByRole(AriaRole.Button, new() { Name = "Publish Article" });
        await publishBtn.WaitForAsync(new() { Timeout = 10000 });
        await publishBtn.ClickAsync();

        // Wait for things to settle, then capture state
        await _page.WaitForTimeoutAsync(8000);
        var currentUrl = _page.Url;
        var currentPath = new Uri(currentUrl).AbsolutePath;

        // Dump all captured API requests for debugging
        var requestLog = string.Join("\n", apiRequests.Select(r => $"  {r.Method} {r.Url} => {r.Status}: {r.Body}"));

        if (!currentPath.StartsWith("/article/"))
        {
            var bodyText = await _page.EvaluateAsync<string>(
                "() => document.body?.innerText?.substring(0, 800) || 'empty'");
            throw new Exception(
                $"After clicking Publish, expected /article/*, got: {currentPath}\n" +
                $"Full URL: {currentUrl}\n" +
                $"API traffic:\n{requestLog}\n" +
                $"Page body (first 800 chars): {bodyText}");
        }

        // Extract slug from URL and wait for read model to project
        var slug = currentPath.Split('/').Last();
        await _seeder.WaitForArticleAsync(slug);

        // Re-navigate so the WASM re-fetches the now-available article
        await _page.NavigateInAppAsync($"/article/{slug}");

        // Assert — should show the article title
        await Expect(_page.Locator("h1")).ToContainTextAsync(title, new() { Timeout = 15000 });
    }

    [Fact]
    public async Task EditArticle_ChangeTitle_ShouldReflectUpdatedTitle()
    {
        // Arrange — seed user, login, and create an article via API
        var username = $"editart{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        var user = await _seeder.RegisterUserAsync(username, email, "password123");
        var article = await _seeder.CreateArticleAsync(
            user.Token,
            $"Original Title {Guid.NewGuid():N}"[..30],
            "Original description",
            "Original body content");
        await _seeder.WaitForArticleAsync(article.Slug);

        await LoginViaUi(email, "password123");

        // Navigate to the editor directly (avoids relying on <a> click interception
        // to transition from /article/ to /editor/ which could race with WaitForFunction)
        await _page.NavigateInAppAsync($"/editor/{article.Slug}");
        await _page.WaitForSelectorAsync(".editor-page", new() { Timeout = 10000 });

        // Wait for the editor form to populate with the existing article data
        await _page.WaitForFunctionAsync(
            "() => document.querySelector('[placeholder=\"Article Title\"]')?.value?.length > 0",
            null, new() { Timeout = 15000 });

        // Act — change the title
        var newTitle = $"Updated Title {Guid.NewGuid():N}"[..30];
        await _page.GetByPlaceholder("Article Title").FillAsync(newTitle);
        await _page.GetByRole(AriaRole.Button, new() { Name = "Publish Article" }).ClickAsync();

        // Wait for the SPA to navigate from /editor/ to /article/{newSlug}
        await _page.WaitForFunctionAsync(
            "() => window.location.pathname.startsWith('/article/')",
            null, new() { Timeout = 15000 });

        // The new slug is derived from the new title (same algorithm as server-side Slug.FromTitle)
        // Extract from URL since the server returns it and HandleArticleSaved pushes it
        var updatedSlug = new Uri(_page.Url).AbsolutePath.Split('/').Last();
        await _seeder.WaitForArticleWithTitleAsync(updatedSlug, newTitle);

        // Re-navigate to force a fresh fetch of the updated article
        await _page.NavigateInAppAsync($"/article/{updatedSlug}");

        // Assert — should show the updated title on the article page
        await Expect(_page.Locator("h1")).ToContainTextAsync(newTitle, new() { Timeout = 15000 });
    }

    [Fact]
    public async Task CreateArticle_WithTags_ShouldShowTagPillsBeforePublish()
    {
        // Arrange — seed user and login
        var username = $"tagart{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        await _seeder.RegisterUserAsync(username, email, "password123");
        await LoginViaUi(email, "password123");

        await _page.NavigateInAppAsync("/editor");
        await _page.WaitForSelectorAsync(".editor-page", new() { Timeout = 10000 });

        // Act — add tags
        var tagInput = _page.GetByPlaceholder("Enter tags");
        await tagInput.FillAsync("alpha");
        await tagInput.PressAsync("Enter");
        await tagInput.FillAsync("beta");
        await tagInput.PressAsync("Enter");

        // Assert — tag pills should be visible
        await Expect(_page.Locator(".tag-list .tag-default").Nth(0)).ToContainTextAsync("alpha");
        await Expect(_page.Locator(".tag-list .tag-default").Nth(1)).ToContainTextAsync("beta");
    }

    private async Task LoginViaUi(string email, string password)
    {
        await _page.GotoAsync("/login");
        await _page.WaitForSelectorAsync("h1:has-text('Sign in')");
        await _page.GetByPlaceholder("Email").FillAsync(email);
        await _page.GetByPlaceholder("Password").FillAsync(password);
        await _page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 15000 });
    }

    private static ILocatorAssertions Expect(ILocator locator) =>
        Assertions.Expect(locator);
}
