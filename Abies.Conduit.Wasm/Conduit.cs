// =============================================================================
// Conduit — Main MVU Program
// =============================================================================
// Implements the Abies Program<Model, Unit> contract:
//   - Initialize: creates initial model from the current URL
//   - Transition: pure state machine handling all messages
//   - View: renders the current page as virtual DOM
//   - Subscriptions: manages active subscriptions (URL changes)
//
// This is the heart of the application — a pure functional state machine
// that transforms (Model, Message) → (Model, Command).
// =============================================================================

using Abies.DOM;
using Abies.Subscriptions;
using Automaton;
using static Abies.Html.Elements;

namespace Abies.Conduit.Wasm;

/// <summary>
/// The Conduit MVU program — implements the full Abies contract.
/// </summary>
public sealed class ConduitProgram : Program<Model, Unit>
{
    /// <summary>
    /// Initializes the application by routing the current URL to a page.
    /// </summary>
    public static (Model, Command) Initialize(Unit _)
    {
        // The API URL is configured via a <meta> tag in index.html.
        // In WASM, we read it via JavaScript interop at startup time.
        // For now, we use a sensible default that can be overridden.
        var apiUrl = "http://localhost:5000";

        var initialUrl = Url.Root;
        var (page, command) = Route.FromUrl(initialUrl, session: null, apiUrl);
        var model = new Model(page, Session: null, ApiUrl: apiUrl);
        return (model, command);
    }

    /// <summary>
    /// Pure transition function — pattern matches on messages to produce new state.
    /// </summary>
    public static (Model, Command) Transition(Model model, Message message) =>
        message switch
        {
            // ─── Navigation ───────────────────────────────────────────────
            UrlChanged url => HandleUrlChanged(model, url),

            // ─── Login Form ───────────────────────────────────────────────
            LoginEmailChanged msg when model.Page is Page.Login login =>
                (model with { Page = new Page.Login(login.Data with { Email = msg.Value }) },
                 Commands.None),

            LoginPasswordChanged msg when model.Page is Page.Login login =>
                (model with { Page = new Page.Login(login.Data with { Password = msg.Value }) },
                 Commands.None),

            LoginSubmitted when model.Page is Page.Login login =>
                (model with { Page = new Page.Login(login.Data with { IsSubmitting = true, Errors = [] }) },
                 new LoginUser(model.ApiUrl, login.Data.Email, login.Data.Password)),

            // ─── Register Form ────────────────────────────────────────────
            RegisterUsernameChanged msg when model.Page is Page.Register reg =>
                (model with { Page = new Page.Register(reg.Data with { Username = msg.Value }) },
                 Commands.None),

            RegisterEmailChanged msg when model.Page is Page.Register reg =>
                (model with { Page = new Page.Register(reg.Data with { Email = msg.Value }) },
                 Commands.None),

            RegisterPasswordChanged msg when model.Page is Page.Register reg =>
                (model with { Page = new Page.Register(reg.Data with { Password = msg.Value }) },
                 Commands.None),

            RegisterSubmitted when model.Page is Page.Register reg =>
                (model with { Page = new Page.Register(reg.Data with { IsSubmitting = true, Errors = [] }) },
                 new RegisterUser(model.ApiUrl, reg.Data.Username, reg.Data.Email, reg.Data.Password)),

            // ─── Comment Form ─────────────────────────────────────────────
            CommentBodyChanged msg when model.Page is Page.Article art =>
                (model with { Page = new Page.Article(art.Data with { CommentBody = msg.Value }) },
                 Commands.None),

            CommentSubmitted when model.Page is Page.Article art && model.Session is not null =>
                (model with { Page = new Page.Article(art.Data with { CommentBody = "" }) },
                 new AddComment(model.ApiUrl, model.Session.Token, art.Data.Slug, art.Data.CommentBody)),

            // ─── Feed Tab / Pagination ────────────────────────────────────
            FeedTabChanged msg when model.Page is Page.Home home =>
                HandleFeedTabChanged(model, home.Data, msg),

            PageChanged msg when model.Page is Page.Home home =>
                HandlePageChanged(model, home.Data, msg),

            // ─── Favorite / Follow ────────────────────────────────────────
            ToggleFavorite msg when model.Session is not null =>
                (model, msg.Favorited
                    ? new UnfavoriteArticle(model.ApiUrl, model.Session.Token, msg.Slug)
                    : new FavoriteArticle(model.ApiUrl, model.Session.Token, msg.Slug)),

            ToggleFollow msg when model.Session is not null =>
                (model, msg.Following
                    ? new UnfollowUser(model.ApiUrl, model.Session.Token, msg.Username)
                    : new FollowUser(model.ApiUrl, model.Session.Token, msg.Username)),

            // ─── Delete ───────────────────────────────────────────────────
            DeleteArticle msg when model.Session is not null =>
                (model, new DeleteArticleCommand(model.ApiUrl, model.Session.Token, msg.Slug)),

            DeleteComment msg when model.Session is not null =>
                (model, new DeleteCommentCommand(model.ApiUrl, model.Session.Token, msg.Slug, msg.CommentId)),

            // ─── API Responses ────────────────────────────────────────────
            ArticlesLoaded msg => HandleArticlesLoaded(model, msg),
            ArticleLoaded msg => HandleArticleLoaded(model, msg),
            CommentsLoaded msg => HandleCommentsLoaded(model, msg),
            TagsLoaded msg => HandleTagsLoaded(model, msg),
            ProfileLoaded msg => HandleProfileLoaded(model, msg),
            UserAuthenticated msg => HandleUserAuthenticated(model, msg),
            FavoriteToggled msg => HandleFavoriteToggled(model, msg),
            FollowToggled msg => HandleFollowToggled(model, msg),
            CommentAdded msg => HandleCommentAdded(model, msg),
            CommentDeleted msg => HandleCommentDeleted(model, msg),
            ArticleDeleted => HandleArticleDeleted(model),

            // ─── Auth ─────────────────────────────────────────────────────
            Logout =>
                (model with { Session = null, Page = new Page.Home(new HomeModel(FeedTab.Global, null, [], 0, 1, [], true)) },
                 Commands.Batch(
                     new FetchArticles(model.ApiUrl, null, Constants.ArticlesPerPage, 0),
                     new FetchTags(model.ApiUrl))),

            // ─── Error Handling ───────────────────────────────────────────
            ApiError msg => HandleApiError(model, msg),

            // ─── Catch-all ────────────────────────────────────────────────
            _ => (model, Commands.None)
        };

    /// <summary>
    /// Renders the current page wrapped in the shared layout.
    /// </summary>
    public static Document View(Model model)
    {
        var content = model.Page switch
        {
            Page.Home home => Pages.Home.View(home.Data, model.Session),
            Page.Login login => Pages.Login.View(login.Data),
            Page.Register reg => Pages.Register.View(reg.Data),
            Page.Article art => Pages.Article.View(art.Data, model.Session),
            Page.NotFound => div([], [text("Page not found.")]),
            _ => div([], [text("Coming soon...")])
        };

        var title = model.Page switch
        {
            Page.Home => "Conduit",
            Page.Login => "Sign in — Conduit",
            Page.Register => "Sign up — Conduit",
            Page.Article { Data.Article: not null } art => $"{art.Data.Article.Title} — Conduit",
            _ => "Conduit"
        };

        return new Document(title,
            Views.Layout.Page(model.Page, model.Session, content));
    }

    /// <summary>
    /// Subscribes to URL changes for client-side routing.
    /// </summary>
    public static Subscription Subscriptions(Model model) =>
        Navigation.UrlChanges(url => new UrlChanged(url));

    // ─── Transition Handlers ──────────────────────────────────────────────

    private static (Model, Command) HandleUrlChanged(Model model, UrlChanged msg)
    {
        var (page, command) = Route.FromUrl(msg.Url, model.Session, model.ApiUrl);
        return (model with { Page = page }, command);
    }

    private static (Model, Command) HandleFeedTabChanged(Model model, HomeModel home, FeedTabChanged msg)
    {
        var newHome = home with
        {
            ActiveTab = msg.Tab,
            SelectedTag = msg.Tag,
            Articles = [],
            ArticlesCount = 0,
            CurrentPage = 1,
            IsLoading = true
        };

        var command = msg.Tab switch
        {
            FeedTab.Your when model.Session is not null =>
                (Command)new FetchFeed(model.ApiUrl, model.Session.Token, Constants.ArticlesPerPage, 0),
            FeedTab.Tag when msg.Tag is not null =>
                new FetchArticles(model.ApiUrl, model.Session?.Token, Constants.ArticlesPerPage, 0, Tag: msg.Tag),
            _ => new FetchArticles(model.ApiUrl, model.Session?.Token, Constants.ArticlesPerPage, 0)
        };

        return (model with { Page = new Page.Home(newHome) }, command);
    }

    private static (Model, Command) HandlePageChanged(Model model, HomeModel home, PageChanged msg)
    {
        var offset = (msg.PageNumber - 1) * Constants.ArticlesPerPage;
        var newHome = home with { CurrentPage = msg.PageNumber, IsLoading = true };

        var command = home.ActiveTab switch
        {
            FeedTab.Your when model.Session is not null =>
                (Command)new FetchFeed(model.ApiUrl, model.Session.Token, Constants.ArticlesPerPage, offset),
            FeedTab.Tag when home.SelectedTag is not null =>
                new FetchArticles(model.ApiUrl, model.Session?.Token, Constants.ArticlesPerPage, offset, Tag: home.SelectedTag),
            _ => new FetchArticles(model.ApiUrl, model.Session?.Token, Constants.ArticlesPerPage, offset)
        };

        return (model with { Page = new Page.Home(newHome) }, command);
    }

    private static (Model, Command) HandleArticlesLoaded(Model model, ArticlesLoaded msg) =>
        model.Page switch
        {
            Page.Home home => (model with
            {
                Page = new Page.Home(home.Data with
                {
                    Articles = msg.Articles,
                    ArticlesCount = msg.ArticlesCount,
                    IsLoading = false
                })
            }, Commands.None),

            Page.Profile profile => (model with
            {
                Page = new Page.Profile(profile.Data with
                {
                    Articles = msg.Articles,
                    ArticlesCount = msg.ArticlesCount,
                    IsLoading = false
                })
            }, Commands.None),

            _ => (model, Commands.None)
        };

    private static (Model, Command) HandleArticleLoaded(Model model, ArticleLoaded msg) =>
        model.Page is Page.Article art
            ? (model with { Page = new Page.Article(art.Data with { Article = msg.Article, IsLoading = false }) },
               Commands.None)
            : (model, Commands.None);

    private static (Model, Command) HandleCommentsLoaded(Model model, CommentsLoaded msg) =>
        model.Page is Page.Article art
            ? (model with { Page = new Page.Article(art.Data with { Comments = msg.Comments }) },
               Commands.None)
            : (model, Commands.None);

    private static (Model, Command) HandleTagsLoaded(Model model, TagsLoaded msg) =>
        model.Page is Page.Home home
            ? (model with { Page = new Page.Home(home.Data with { PopularTags = msg.Tags }) },
               Commands.None)
            : (model, Commands.None);

    private static (Model, Command) HandleProfileLoaded(Model model, ProfileLoaded msg) =>
        model.Page is Page.Profile profile
            ? (model with { Page = new Page.Profile(profile.Data with { Profile = msg.Profile, IsLoading = false }) },
               Commands.None)
            : (model, Commands.None);

    private static (Model, Command) HandleUserAuthenticated(Model model, UserAuthenticated msg)
    {
        var newModel = model with { Session = msg.Session };
        var (page, command) = Route.FromUrl(new Url([], new Dictionary<string, string>(), Option<string>.None), msg.Session, model.ApiUrl);
        return (newModel with { Page = page }, Commands.Batch(command, Navigation.PushUrl(Url.Root)));
    }

    private static (Model, Command) HandleFavoriteToggled(Model model, FavoriteToggled msg) =>
        model.Page switch
        {
            Page.Home home => (model with
            {
                Page = new Page.Home(home.Data with
                {
                    Articles = home.Data.Articles
                        .Select(a => a.Slug == msg.Article.Slug ? msg.Article : a)
                        .ToList()
                })
            }, Commands.None),

            Page.Article art when art.Data.Article is not null && art.Data.Article.Slug == msg.Article.Slug =>
                (model with
                {
                    Page = new Page.Article(art.Data with
                    {
                        Article = art.Data.Article with
                        {
                            Favorited = msg.Article.Favorited,
                            FavoritesCount = msg.Article.FavoritesCount
                        }
                    })
                }, Commands.None),

            _ => (model, Commands.None)
        };

    private static (Model, Command) HandleFollowToggled(Model model, FollowToggled msg) =>
        model.Page is Page.Article art && art.Data.Article is not null
            ? (model with
            {
                Page = new Page.Article(art.Data with
                {
                    Article = art.Data.Article with
                    {
                        Author = new AuthorData(
                            msg.Profile.Username, msg.Profile.Bio,
                            msg.Profile.Image, msg.Profile.Following)
                    }
                })
            }, Commands.None)
            : (model, Commands.None);

    private static (Model, Command) HandleCommentAdded(Model model, CommentAdded msg) =>
        model.Page is Page.Article art
            ? (model with
            {
                Page = new Page.Article(art.Data with
                {
                    Comments = art.Data.Comments.Prepend(msg.Comment).ToList()
                })
            }, Commands.None)
            : (model, Commands.None);

    private static (Model, Command) HandleCommentDeleted(Model model, CommentDeleted msg) =>
        model.Page is Page.Article art
            ? (model with
            {
                Page = new Page.Article(art.Data with
                {
                    Comments = art.Data.Comments
                        .Where(c => c.Id != msg.CommentId)
                        .ToList()
                })
            }, Commands.None)
            : (model, Commands.None);

    private static (Model, Command) HandleArticleDeleted(Model model)
    {
        var (page, command) = Route.FromUrl(Url.Root, model.Session, model.ApiUrl);
        return (model with { Page = page }, Commands.Batch(command, Navigation.PushUrl(Url.Root)));
    }

    private static (Model, Command) HandleApiError(Model model, ApiError msg) =>
        model.Page switch
        {
            Page.Login login => (model with
            {
                Page = new Page.Login(login.Data with { Errors = msg.Errors, IsSubmitting = false })
            }, Commands.None),

            Page.Register reg => (model with
            {
                Page = new Page.Register(reg.Data with { Errors = msg.Errors, IsSubmitting = false })
            }, Commands.None),

            _ => (model, Commands.None)
        };
}
