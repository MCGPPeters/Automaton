// =============================================================================
// PageExtensions — Playwright Page Helpers for SPA Navigation
// =============================================================================
// The Conduit WASM app is a single-page application that uses
// history.pushState for client-side routing. Calling page.GotoAsync()
// triggers a full page reload, which destroys the in-memory session
// (auth token, user state). These helpers navigate within the running
// WASM app without a page reload, preserving session state.
// =============================================================================

using Microsoft.Playwright;

namespace Abies.Conduit.Testing.E2E.Helpers;

/// <summary>
/// Extension methods for Playwright <see cref="IPage"/> to support SPA navigation.
/// </summary>
public static class PageExtensions
{
    /// <summary>
    /// Navigates within the SPA by calling history.pushState and dispatching
    /// a popstate event. This preserves the WASM app's in-memory state (session,
    /// loaded data) unlike <see cref="IPage.GotoAsync"/> which causes a full reload.
    /// </summary>
    /// <param name="page">The Playwright page.</param>
    /// <param name="path">The target path (e.g. "/article/my-slug").</param>
    public static async Task NavigateInAppAsync(this IPage page, string path)
    {
        await page.EvaluateAsync(
            "path => { history.pushState(null, '', path); window.dispatchEvent(new PopStateEvent('popstate')); }",
            path);
    }
}
