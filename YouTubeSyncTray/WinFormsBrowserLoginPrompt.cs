namespace YouTubeSyncTray;

internal sealed class WinFormsBrowserLoginPrompt : IBrowserLoginPrompt
{
    private readonly Func<IWin32Window?> _ownerProvider;

    public WinFormsBrowserLoginPrompt(Func<IWin32Window?>? ownerProvider = null)
    {
        _ownerProvider = ownerProvider ?? (() => Form.ActiveForm);
    }

    public Task<BrowserLoginPromptResult?> ConfirmAsync(BrowserLoginPromptRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var form = new BrowserLoginPromptForm(request);
        var owner = _ownerProvider();
        var dialogResult = owner is null ? form.ShowDialog() : form.ShowDialog(owner);
        BrowserLoginPromptResult? result = dialogResult == DialogResult.OK
            ? new BrowserLoginPromptResult(form.SelectedBrowser)
            : null;
        return Task.FromResult(result);
    }
}
