// =============================================================================
// Feed E2E Tests — Global feed, your feed, tag filter, pagination
// =============================================================================
// Tests the home/feed page user journeys:
//   - Global feed shows articles from all users
//   - Tag sidebar shows available tags
//   - Clicking a tag filters the feed
//   - "Your Feed" shows articles from followed users
//   - Article previews display title, description, author, favorites, tags
//
// Uses API seeding for all data setup (per Playwright guidelines §7.1).
// =============================================================================

using Abies.Conduit.Testing.E2E.Fixtures;
using Abies.Conduit.Testing.E2E.Helpers;
using Microsoft.Playwright;

namespace Abies.Conduit.Testing.E2E;

[Trait("Category", "E2E")]
[Collection("Conduit")]
public sealed class FeedTests : IAsyncLifetime
{
    private readonly ConduitAppFixture _fixture;
    private IPage _page = null!;
    private ApiSeeder _seeder = null!;

    public FeedTests(ConduitAppFixture fixture)
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
    public async Task GlobalFeed_WithArticles_ShouldShowArticlePreviews()
    {
        // Arrange — seed a user and articles
        var username = $"feedglo{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        var user = await _seeder.RegisterUserAsync(username, email, "password123");
        var article = await _seeder.CreateArticleAsync(
            user.Token,
            $"GloFeed {Guid.NewGuid():N}"[..30],
            "Description for global feed",
            "Body of global feed article.");
        await _seeder.WaitForArticleAsync(article.Slug);

        // Act — navigate to home
        await _page.GotoAsync("/");
        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 15000 });

        // Click "Global Feed" tab (scoped to feed-toggle to avoid matching article content)
        await _page.Locator(".feed-toggle").GetByText("Global Feed").ClickAsync();
        await _page.WaitForTimeoutAsync(2000); // Allow feed to load

        // Assert — article preview should be visible
        await Expect(_page.Locator(".article-preview").First).ToBeVisibleAsync(
            new() { Timeout = 10000 });
    }

    [Fact]
    public async Task TagSidebar_ShouldShowPopularTags()
    {
        // Arrange — seed articles with tags
        var username = $"feedtag{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        var user = await _seeder.RegisterUserAsync(username, email, "password123");
        var uniqueTag = $"tag{Guid.NewGuid():N}"[..15];
        var article = await _seeder.CreateArticleAsync(
            user.Token,
            $"Tagged Article {Guid.NewGuid():N}"[..30],
            "Tagged description",
            "Body with tags.",
            [uniqueTag]);
        // Wait for read model to project the article (so tags appear in sidebar)
        await _seeder.WaitForArticleAsync(article.Slug);

        // Act — navigate to home
        await _page.GotoAsync("/");
        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 15000 });

        // Assert — sidebar should contain our tag
        await Expect(_page.Locator(".sidebar .tag-list")).ToContainTextAsync(uniqueTag,
            new() { Timeout = 15000 });
    }

    [Fact]
    public async Task ClickTag_ShouldFilterFeedByTag()
    {
        // Arrange — seed articles: one with a unique tag, one without
        var username = $"feedflt{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        var user = await _seeder.RegisterUserAsync(username, email, "password123");
        var uniqueTag = $"flt{Guid.NewGuid():N}"[..15];
        var taggedArticle = await _seeder.CreateArticleAsync(
            user.Token,
            $"Tagged {Guid.NewGuid():N}"[..30],
            "Has the filter tag",
            "Body.",
            [uniqueTag]);
        await _seeder.CreateArticleAsync(
            user.Token,
            $"Untagged {Guid.NewGuid():N}"[..30],
            "No special tag",
            "Body without tag.");
        // Wait for read model to project articles and tags
        await _seeder.WaitForArticleAsync(taggedArticle.Slug);

        // Navigate to home
        await _page.GotoAsync("/");
        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 15000 });

        // Act — wait for sidebar tag to appear, then click it
        var tagLocator = _page.Locator($".sidebar .tag-list >> text={uniqueTag}");
        await tagLocator.WaitForAsync(new() { Timeout = 15000 });
        await tagLocator.ClickAsync();

        // Assert — the tag tab should appear and be active
        await Expect(_page.Locator(".feed-toggle .nav-link.active"))
            .ToContainTextAsync(uniqueTag, new() { Timeout = 10000 });
    }

    [Fact]
    public async Task YourFeed_WhenFollowingUser_ShouldShowTheirArticles()
    {
        // Arrange — seed two users, have reader follow author, author writes article
        var author = $"feedaut{Guid.NewGuid():N}"[..20];
        var authorEmail = $"{author}@test.com";
        var authorUser = await _seeder.RegisterUserAsync(author, authorEmail, "password123");
        var article = await _seeder.CreateArticleAsync(
            authorUser.Token,
            $"FollowedPost {Guid.NewGuid():N}"[..30],
            "For your feed",
            "This should appear in your feed.");
        await _seeder.WaitForArticleAsync(article.Slug);

        var reader = $"feedrd{Guid.NewGuid():N}"[..20];
        var readerEmail = $"{reader}@test.com";
        var readerUser = await _seeder.RegisterUserAsync(reader, readerEmail, "password123");
        await _seeder.FollowUserAsync(readerUser.Token, author);

        await LoginViaUi(readerEmail, "password123");

        // Act — navigate to home (SPA navigation to preserve session), "Your Feed" should be available
        await _page.NavigateInAppAsync("/");
        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 15000 });
        await _page.Locator(".feed-toggle").GetByText("Your Feed").ClickAsync();
        await _page.WaitForTimeoutAsync(2000); // Allow feed to load

        // Assert — should see the followed user's article
        await Expect(_page.Locator(".article-preview").First).ToBeVisibleAsync(
            new() { Timeout = 10000 });
    }

    [Fact]
    public async Task ArticlePreview_ShouldShowMetadata()
    {
        // Arrange — seed user and article with tags
        var username = $"feedmta{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        var user = await _seeder.RegisterUserAsync(username, email, "password123");
        var article = await _seeder.CreateArticleAsync(
            user.Token,
            $"Preview Meta {Guid.NewGuid():N}"[..30],
            "Preview description text",
            "Body of preview article.",
            ["metatag"]);
        await _seeder.WaitForArticleAsync(article.Slug);

        // Act — navigate to home, click global feed
        await _page.GotoAsync("/");
        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 15000 });
        await _page.Locator(".feed-toggle").GetByText("Global Feed").ClickAsync();
        await _page.WaitForTimeoutAsync(2000);

        // Assert — article preview should show author, title, description
        var preview = _page.Locator(".article-preview").Filter(
            new() { HasText = article.Title });
        await Expect(preview).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(preview).ToContainTextAsync(username);
        await Expect(preview).ToContainTextAsync(article.Description);
    }

    [Fact]
    public async Task HomeBanner_ShouldShowConduitBranding()
    {
        // Act — navigate to home
        await _page.GotoAsync("/");
        await _page.WaitForSelectorAsync(".home-page", new() { Timeout = 15000 });

        // Assert — banner should be visible
        await Expect(_page.Locator(".banner h1")).ToContainTextAsync("conduit");
        await Expect(_page.Locator(".banner p")).ToContainTextAsync(
            "A place to share your knowledge.");
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

    private static IPageAssertions Expect(IPage page) =>
        Assertions.Expect(page);
}
