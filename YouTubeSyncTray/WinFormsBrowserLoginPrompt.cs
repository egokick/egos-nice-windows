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
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (form.IsDisposed)
                {
                    return;
                }

                if (form.IsHandleCreated)
                {
                    _ = form.BeginInvoke(new Action(() =>
                    {
                        if (!form.IsDisposed)
                        {
                            form.DialogResult = DialogResult.Cancel;
                            form.Close();
                        }
                    }));
                }
            }
            catch
            {
                // Best-effort close during shutdown.
            }
        });
        var owner = _ownerProvider();
        var dialogResult = owner is null ? form.ShowDialog() : form.ShowDialog(owner);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult<BrowserLoginPromptResult?>(null);
        }

        BrowserLoginPromptResult? result = dialogResult == DialogResult.OK
            ? new BrowserLoginPromptResult(form.SelectedBrowser)
            : null;
        return Task.FromResult(result);
    }
}
