// =============================================================================
// Profile E2E Tests — View profile, follow/unfollow, article tabs
// =============================================================================
// Tests the profile page user journeys:
//   - View another user's profile (username, bio, image)
//   - Follow and unfollow a user
//   - Switch between "My Articles" and "Favorited Articles" tabs
//   - Own profile shows "Edit Profile Settings" link instead of follow
//
// Uses API seeding for user/article creation (per Playwright guidelines §7.1).
// =============================================================================

using Abies.Conduit.Testing.E2E.Fixtures;
using Abies.Conduit.Testing.E2E.Helpers;
using Microsoft.Playwright;

namespace Abies.Conduit.Testing.E2E;

[Trait("Category", "E2E")]
[Collection("Conduit")]
public sealed class ProfileTests : IAsyncLifetime
{
    private readonly ConduitAppFixture _fixture;
    private IPage _page = null!;
    private ApiSeeder _seeder = null!;

    public ProfileTests(ConduitAppFixture fixture)
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
    public async Task ViewProfile_ShouldShowUsernameAndArticles()
    {
        // Arrange — seed a user with an article
        var username = $"profvw{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        var user = await _seeder.RegisterUserAsync(username, email, "password123");
        var article = await _seeder.CreateArticleAsync(
            user.Token,
            $"Profile Article {Guid.NewGuid():N}"[..30],
            "For profile test",
            "Article body.");

        // Wait for read model to catch up (event sourcing eventual consistency)
        await _seeder.WaitForProfileAsync(username);
        await _seeder.WaitForArticleAsync(article.Slug);

        // Act — navigate to the profile (no login needed)
        await _page.GotoAsync($"/profile/{username}");

        // Wait for profile content to actually load
        await Expect(_page.Locator(".user-info h4")).ToContainTextAsync(username, new() { Timeout = 15000 });
    }

    [Fact]
    public async Task FollowUser_WhenLoggedIn_ShouldToggleFollowButton()
    {
        // Arrange — seed two users: one to follow, one as the follower
        var target = $"proftgt{Guid.NewGuid():N}"[..20];
        var targetEmail = $"{target}@test.com";
        await _seeder.RegisterUserAsync(target, targetEmail, "password123");

        var follower = $"profflw{Guid.NewGuid():N}"[..20];
        var followerEmail = $"{follower}@test.com";
        await _seeder.RegisterUserAsync(follower, followerEmail, "password123");

        // Wait for read model to catch up
        await _seeder.WaitForProfileAsync(target);
        await _seeder.WaitForProfileAsync(follower);

        await LoginViaUi(followerEmail, "password123");

        // Navigate to target's profile (SPA navigation to preserve session)
        await _page.NavigateInApp($"/profile/{target}");

        // Wait for profile content to load
        await Expect(_page.Locator(".user-info h4")).ToContainTextAsync(target, new() { Timeout = 15000 });

        // Act — click follow
        var followBtn = _page.Locator("button:has-text('Follow')").First;
        await followBtn.ClickAsync();

        // Assert — button should change to "Unfollow"
        await Expect(_page.Locator($"button:has-text('Unfollow {target}')").First)
            .ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [Fact]
    public async Task UnfollowUser_WhenFollowing_ShouldToggleBackToFollow()
    {
        // Arrange — seed users and follow via API
        var target = $"profunf{Guid.NewGuid():N}"[..20];
        var targetEmail = $"{target}@test.com";
        await _seeder.RegisterUserAsync(target, targetEmail, "password123");

        var follower = $"profuf2{Guid.NewGuid():N}"[..20];
        var followerEmail = $"{follower}@test.com";
        var followerUser = await _seeder.RegisterUserAsync(follower, followerEmail, "password123");
        await _seeder.FollowUserAsync(followerUser.Token, target);

        // Wait for read model to catch up
        await _seeder.WaitForProfileAsync(target);
        await _seeder.WaitForProfileAsync(follower);

        await LoginViaUi(followerEmail, "password123");

        // Navigate to target's profile (SPA navigation to preserve session)
        await _page.NavigateInApp($"/profile/{target}");

        // Wait for profile content to load with unfollow button visible
        await Expect(_page.Locator($"button:has-text('Unfollow {target}')").First)
            .ToBeVisibleAsync(new() { Timeout = 15000 });

        // Act — click unfollow
        var unfollowBtn = _page.Locator($"button:has-text('Unfollow {target}')").First;
        await unfollowBtn.ClickAsync();

        // Assert — button should change back to "Follow"
        await Expect(_page.Locator($"button:has-text('Follow {target}')").First)
            .ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [Fact]
    public async Task ProfileTabs_ShouldSwitchBetweenMyArticlesAndFavorited()
    {
        // Arrange — seed user with an article, and favorite another user's article
        var author = $"prftab{Guid.NewGuid():N}"[..20];
        var authorEmail = $"{author}@test.com";
        var authorUser = await _seeder.RegisterUserAsync(author, authorEmail, "password123");
        await _seeder.CreateArticleAsync(
            authorUser.Token,
            $"My Article {Guid.NewGuid():N}"[..30],
            "Author's own",
            "Body of author's article.");

        // Create another user's article and have our user favorite it
        var other = $"prfoth{Guid.NewGuid():N}"[..20];
        var otherEmail = $"{other}@test.com";
        var otherUser = await _seeder.RegisterUserAsync(other, otherEmail, "password123");
        var otherArticle = await _seeder.CreateArticleAsync(
            otherUser.Token,
            $"Other Article {Guid.NewGuid():N}"[..30],
            "Other's article",
            "Body of other's article.");
        await _seeder.FavoriteArticleAsync(authorUser.Token, otherArticle.Slug);

        // Wait for read model to catch up
        await _seeder.WaitForProfileAsync(author);
        await _seeder.WaitForProfileAsync(other);

        await LoginViaUi(authorEmail, "password123");

        // Navigate to own profile (SPA navigation to preserve session)
        await _page.NavigateInApp($"/profile/{author}");

        // Wait for profile content to load
        await Expect(_page.Locator(".user-info h4")).ToContainTextAsync(author, new() { Timeout = 15000 });

        // Assert — "My Articles" tab should be active with the author's article
        await Expect(_page.Locator(".articles-toggle .nav-link.active"))
            .ToContainTextAsync("My Articles");

        // Act — click "Favorited Articles" tab
        await _page.GetByText("Favorited Articles").ClickAsync();

        // Assert — should show the favorited article
        await Expect(_page.Locator(".articles-toggle .nav-link.active"))
            .ToContainTextAsync("Favorited Articles");
        await _page.WaitForTimeoutAsync(2000); // Allow feed to load
    }

    [Fact]
    public async Task OwnProfile_ShouldShowEditSettingsLink()
    {
        // Arrange — seed user and login
        var username = $"prfown{Guid.NewGuid():N}"[..20];
        var email = $"{username}@test.com";
        await _seeder.RegisterUserAsync(username, email, "password123");

        // Wait for read model to catch up
        await _seeder.WaitForProfileAsync(username);

        await LoginViaUi(email, "password123");

        // Act — navigate to own profile (SPA navigation to preserve session)
        await _page.NavigateInApp($"/profile/{username}");

        // Wait for profile content to load
        await Expect(_page.Locator(".user-info h4")).ToContainTextAsync(username, new() { Timeout = 15000 });

        // Assert — should show edit settings link, not follow button
        await Expect(_page.GetByText("Edit Profile Settings"))
            .ToBeVisibleAsync(new() { Timeout = 10000 });
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
