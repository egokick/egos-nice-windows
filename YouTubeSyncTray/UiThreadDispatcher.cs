namespace YouTubeSyncTray;

internal sealed class UiThreadDispatcher : IDisposable
{
    private readonly Control _control;

    public UiThreadDispatcher()
    {
        _control = new Control();
        _ = _control.Handle;
    }

    public Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Execute()
        {
            _ = ExecuteCoreAsync();

            async Task ExecuteCoreAsync()
            {
                try
                {
                    await action();
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }
        }

        if (_control.IsDisposed)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(UiThreadDispatcher)));
            return tcs.Task;
        }

        if (_control.InvokeRequired)
        {
            _control.BeginInvoke((MethodInvoker)Execute);
        }
        else
        {
            Execute();
        }

        return tcs.Task;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Execute()
        {
            _ = ExecuteCoreAsync();

            async Task ExecuteCoreAsync()
            {
                try
                {
                    tcs.TrySetResult(await action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }
        }

        if (_control.IsDisposed)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(UiThreadDispatcher)));
            return tcs.Task;
        }

        if (_control.InvokeRequired)
        {
            _control.BeginInvoke((MethodInvoker)Execute);
        }
        else
        {
            Execute();
        }

        return tcs.Task;
    }

    public void Dispose()
    {
        _control.Dispose();
    }
}
