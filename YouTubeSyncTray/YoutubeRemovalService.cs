using System.Text.Json;

namespace YouTubeSyncTray;

internal sealed class YoutubeRemovalService
{
    private const string WatchLaterUrl = "https://www.youtube.com/playlist?list=WL";

    private readonly ChromiumManagedBrowser _managedBrowser;

    public YoutubeRemovalService(YoutubeSyncPaths paths)
    {
        _managedBrowser = new ChromiumManagedBrowser(paths);
    }

    public async Task RemoveFromWatchLaterAsync(
        AppSettings settings,
        IEnumerable<string> videoIds,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        settings.Normalize();
        var ids = videoIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        if (!_managedBrowser.Supports(settings.BrowserCookies))
        {
            throw new InvalidOperationException("Watch Later removal currently supports Google Chrome and Microsoft Edge only.");
        }

        if (!ChromiumBrowserLocator.TryGetExecutablePath(settings.BrowserCookies, out _))
        {
            throw new InvalidOperationException(
                $"Could not find {ChromiumBrowserLocator.GetDisplayName(settings.BrowserCookies)} on this PC.");
        }

        progress?.Report($"Opening managed {ChromiumBrowserLocator.GetDisplayName(settings.BrowserCookies)} to remove {ids.Length} video(s) from Watch Later...");
        var result = await _managedBrowser.RunWithAuthenticatedSessionAsync(
            settings.BrowserCookies,
            settings.BrowserProfile,
            progress,
            async (session, token) =>
            {
                await session.NavigateAsync(WatchLaterUrl, token);
                await Task.Delay(TimeSpan.FromSeconds(4), token);
                return await session.EvaluateValueAsync<RemovalResult>(BuildRemovalScript(ids), token);
            },
            cancellationToken);

        if (result.Missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Could not remove all requested items from Watch Later. Missing: {string.Join(", ", result.Missing)}");
        }

        progress?.Report($"Removed {result.Removed.Count} video(s) from Watch Later.");
    }

    private static string BuildRemovalScript(IEnumerable<string> videoIds)
    {
        var payload = JsonSerializer.Serialize(videoIds);
        return $$"""
        (async () => {
          const wanted = new Set({{payload}});
          const removed = [];
          const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

          const extractId = (row) => {
            const link = row.querySelector('a[href*="watch?v="]');
            if (!link || !link.href) {
              return null;
            }

            const match = link.href.match(/[?&]v=([^&]+)/);
            return match ? match[1] : null;
          };

          const isVisible = (element) => !!element && !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);

          const clickRemoveMenuItem = async () => {
            for (let attempt = 0; attempt < 12; attempt++) {
              const candidates = [...document.querySelectorAll('ytd-menu-service-item-renderer,tp-yt-paper-item,[role="menuitem"]')];
              const item = candidates.find((candidate) => {
                const text = (candidate.innerText || candidate.textContent || '').trim().toLowerCase();
                return text.includes('remove from watch later') && isVisible(candidate);
              });

              if (item) {
                item.click();
                return true;
              }

              await sleep(150);
            }

            return false;
          };

          const getRows = () => [...document.querySelectorAll('ytd-playlist-video-renderer')];

          window.scrollTo({ top: 0, behavior: 'instant' });
          await sleep(1200);

          let idleLoops = 0;
          while (wanted.size > 0 && idleLoops < 80) {
            let progress = false;
            for (const row of getRows()) {
              const id = extractId(row);
              if (!id || !wanted.has(id)) {
                continue;
              }

              row.scrollIntoView({ block: 'center', behavior: 'instant' });
              await sleep(250);

              const menuButton = row.querySelector('ytd-menu-renderer yt-icon-button button, ytd-menu-renderer button');
              if (!menuButton) {
                continue;
              }

              menuButton.click();
              await sleep(350);
              const clicked = await clickRemoveMenuItem();
              if (!clicked) {
                document.body.click();
                await sleep(150);
                continue;
              }

              await sleep(900);
              wanted.delete(id);
              removed.push(id);
              progress = true;
            }

            if (progress) {
              idleLoops = 0;
              continue;
            }

            const before = window.scrollY;
            window.scrollBy({ top: Math.max(window.innerHeight * 0.9, 700), behavior: 'instant' });
            await sleep(800);
            idleLoops = window.scrollY === before ? idleLoops + 1 : 0;
          }

          return {
            removed,
            missing: [...wanted]
          };
        })()
        """;
    }

    private sealed class RemovalResult
    {
        public List<string> Removed { get; set; } = [];
        public List<string> Missing { get; set; } = [];
    }
}
