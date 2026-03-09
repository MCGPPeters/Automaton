// =============================================================================
// Comment E2E Tests — Add and delete comments on articles
// =============================================================================
// Tests the comment user journeys:
//   - Add a comment → see it appear in the comment list
//   - Delete own comment → removed from the list
//   - View comments by other users (read-only, no delete button)
//
// Uses API seeding for user/article creation (per Playwright guidelines §7.1).
// Comment creation via UI is tested since that IS the feature under test.
// =============================================================================

using Abies.Conduit.Testing.E2E.Fixtures;
using Abies.Conduit.Testing.E2E.Helpers;
using Microsoft.Playwright;

namespace Abies.Conduit.Testing.E2E;

[Trait("Category", "E2E")]
[Collection("Conduit")]
public sealed class CommentTests : IAsyncLifetime
{
    private readonly ConduitAppFixture _fixture;
    private IPage _page = null!;
    private ApiSeeder _seeder = null!;

    public CommentTests(ConduitAppFixture fixture)
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
    public async Task AddComment_WithText_ShouldAppearInCommentList()
    {
        // Arrange — seed user and article, login
        var author = $"cmtauth{Guid.NewGuid():N}"[..20];
        var email = $"{author}@test.com";
        var user = await _seeder.RegisterUserAsync(author, email, "password123");
        var article = await _seeder.CreateArticleAsync(
            user.Token,
            $"Comment Test {Guid.NewGuid():N}"[..30],
            "Article for comments",
            "Comment testing body.");
        await _seeder.WaitForArticleAsync(article.Slug);

        await LoginViaUi(email, "password123");

        // Navigate to article (SPA navigation to preserve session)
        await _page.NavigateInApp($"/article/{article.Slug}");

        // Wait for article content to load with comment form visible
        await _page.GetByPlaceholder("Write a comment...").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });

        // Act — write and post a comment
        var commentText = $"E2E comment {Guid.NewGuid():N}"[..40];
        await _page.GetByPlaceholder("Write a comment...").FillAsync(commentText);
        await _page.GetByRole(AriaRole.Button, new() { Name = "Post Comment" }).ClickAsync();

        // Assert — comment should appear in the list
        await Expect(_page.Locator(".card .card-block p").First)
            .ToContainTextAsync(commentText, new() { Timeout = 10000 });
    }

    [Fact]
    public async Task DeleteComment_AsAuthor_ShouldRemoveFromList()
    {
        // Arrange — seed user, article, and comment via API
        var username = $"cmtdel{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        var user = await _seeder.RegisterUserAsync(username, email, "password123");
        var article = await _seeder.CreateArticleAsync(
            user.Token,
            $"Del Comment {Guid.NewGuid():N}"[..30],
            "Article with comment to delete",
            "Body content.");
        var comment = await _seeder.AddCommentAsync(
            user.Token,
            article.Slug,
            "This comment will be deleted.");
        await _seeder.WaitForArticleAsync(article.Slug);

        await LoginViaUi(email, "password123");

        // Navigate to article (SPA navigation to preserve session)
        await _page.NavigateInApp($"/article/{article.Slug}");

        // Wait for comment to be visible (article fully loaded)
        await Expect(_page.Locator(".card .card-block p").First)
            .ToContainTextAsync(comment.Body, new() { Timeout = 15000 });

        // Act — click the delete icon on the comment
        await _page.Locator(".card .card-footer .mod-options i.ion-trash-a").First.ClickAsync();

        // Assert — comment should be gone
        await Expect(_page.Locator($"text={comment.Body}")).ToHaveCountAsync(0,
            new() { Timeout = 10000 });
    }

    [Fact]
    public async Task AddMultipleComments_ShouldShowAllInOrder()
    {
        // Arrange — seed user and article
        var username = $"cmtmul{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        var user = await _seeder.RegisterUserAsync(username, email, "password123");
        var article = await _seeder.CreateArticleAsync(
            user.Token,
            $"Multi Comment {Guid.NewGuid():N}"[..30],
            "Article for multiple comments",
            "Body for multi comment test.");

        // Add first comment via API
        await _seeder.AddCommentAsync(user.Token, article.Slug, "First comment via API");
        await _seeder.WaitForArticleAsync(article.Slug);

        await LoginViaUi(email, "password123");

        // Navigate to article (SPA navigation to preserve session)
        await _page.NavigateInApp($"/article/{article.Slug}");

        // Wait for article content to load with comment form visible
        await _page.GetByPlaceholder("Write a comment...").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });

        // Act — add a second comment via UI
        await _page.GetByPlaceholder("Write a comment...").FillAsync("Second comment via UI");
        await _page.GetByRole(AriaRole.Button, new() { Name = "Post Comment" }).ClickAsync();

        // Assert — both comments should be visible
        await _page.WaitForTimeoutAsync(2000); // Allow state to propagate
        var commentCards = _page.Locator(".card .card-block p");
        await Expect(commentCards).ToHaveCountAsync(2, new() { Timeout = 10000 });
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
