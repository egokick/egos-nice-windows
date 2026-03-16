namespace YouTubeSyncTray;

internal readonly record struct BrowserLoginPromptRequest(
    IReadOnlyList<BrowserCookieSource> AvailableBrowsers,
    BrowserCookieSource SelectedBrowser,
    string Profile);

internal readonly record struct BrowserLoginPromptResult(BrowserCookieSource SelectedBrowser);

internal interface IBrowserLoginPrompt
{
    Task<BrowserLoginPromptResult?> ConfirmAsync(BrowserLoginPromptRequest request, CancellationToken cancellationToken);
}
