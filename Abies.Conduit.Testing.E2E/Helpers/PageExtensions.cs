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

    /// <summary>
    /// Fills a form field and waits for the server-side DOM patch to settle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In InteractiveServer mode, each input event round-trips through the
    /// WebSocket: browser → server MVU transition → diff → binary patch → browser DOM.
    /// Without waiting for the patch to arrive, rapid sequential fills race against
    /// DOM mutations — the server re-renders the form after each input event,
    /// potentially replacing DOM elements that Playwright is about to interact with.
    /// </para>
    /// <para>
    /// This method fills the input and then asserts that the value attribute
    /// matches the expected value. The value attribute is updated by the server's
    /// DOM patch, so a successful assertion confirms the full round-trip completed.
    /// </para>
    /// <para>
    /// For WASM mode, this overhead is unnecessary (the MVU loop runs in-process
    /// with synchronous DOM updates), but the assertion is still valid and harmless.
    /// </para>
    /// </remarks>
    /// <param name="locator">The input field locator.</param>
    /// <param name="value">The value to fill.</param>
    /// <param name="timeoutMs">Timeout for the patch to settle (default 5s).</param>
    public static async Task FillAndWaitForPatchAsync(this ILocator locator, string value, int timeoutMs = 5000)
    {
        await locator.FillAsync(value);
        await Assertions.Expect(locator).ToHaveValueAsync(value, new() { Timeout = timeoutMs });
    }

    /// <summary>
    /// Waits for the WASM runtime to finish taking over from the server-rendered page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In InteractiveAuto mode, the page is initially server-rendered and interactive
    /// via WebSocket. The WASM bundle downloads in the background and, once ready,
    /// calls <c>renderInitial</c> which replaces the entire DOM tree and sets
    /// <c>data-abies-mode="wasm"</c> on the body element.
    /// </para>
    /// <para>
    /// Tests that interact with form fields must wait for this takeover to complete.
    /// Otherwise, a fill operation may target a server-rendered element that is about
    /// to be replaced by WASM, causing the value to be lost.
    /// </para>
    /// </remarks>
    /// <param name="page">The Playwright page.</param>
    /// <param name="timeoutMs">Timeout for WASM readiness (default 30s — WASM can be slow to download).</param>
    public static async Task WaitForWasmReadyAsync(this IPage page, int timeoutMs = 30000)
    {
        await page.WaitForSelectorAsync("[data-abies-mode='wasm']",
            new() { State = WaitForSelectorState.Attached, Timeout = timeoutMs });
    }
}
