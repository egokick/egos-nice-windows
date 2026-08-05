using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed class TransferRow : INotifyPropertyChanged
{
    private TransferState _state = TransferState.Queued;
    private long _current;
    private long _total;
    private string _error = string.Empty;

    public Guid Id { get; } = Guid.NewGuid();
    public string Device { get; init; } = string.Empty;
    public string File { get; init; } = string.Empty;
    public TransferDirection Direction { get; init; }
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    public TransferState State { get => _state; set { _state = value; Changed(); Changed(nameof(Progress)); } }
    public long Current { get => _current; set { _current = value; Changed(); Changed(nameof(Progress)); } }
    public long Total { get => _total; set { _total = value; Changed(); Changed(nameof(Progress)); } }
    public string Error { get => _error; set { _error = value; Changed(); } }
    public string Progress => Total <= 0 ? State.ToString() : $"{Current * 100 / Math.Max(1, Total)}%";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed class TransferManager
{
    public ObservableCollection<TransferRow> Items { get; } = [];

    public async Task DownloadAsync(AgentClient client, DeviceRecord device, string token, string root, string remotePath,
        string localPath, CancellationToken cancellationToken = default)
    {
        var row = new TransferRow { Device = device.Name, File = Path.GetFileName(remotePath), Direction = TransferDirection.Download };
        Items.Insert(0, row);
        row.State = TransferState.Running;
        try
        {
            await client.DownloadAsync(device, token, root, remotePath, localPath,
                new Progress<(long Current, long Total)>(value => { row.Current = value.Current; row.Total = value.Total; }), cancellationToken);
            row.State = TransferState.Completed;
        }
        catch (OperationCanceledException)
        {
            row.State = TransferState.Cancelled;
            throw;
        }
        catch (Exception exception)
        {
            row.Error = exception.Message;
            row.State = TransferState.Failed;
            throw;
        }
    }

    public async Task UploadAsync(AgentClient client, DeviceRecord device, string token, string localPath, string root,
        string destinationDirectory, bool overwrite, CancellationToken cancellationToken = default)
    {
        var row = new TransferRow { Device = device.Name, File = Path.GetFileName(localPath), Direction = TransferDirection.Upload };
        Items.Insert(0, row);
        row.State = TransferState.Running;
        try
        {
            await client.UploadAsync(device, token, localPath, root, destinationDirectory, overwrite,
                new Progress<(long Current, long Total)>(value => { row.Current = value.Current; row.Total = value.Total; }), cancellationToken);
            row.State = TransferState.Completed;
        }
        catch (OperationCanceledException)
        {
            row.State = TransferState.Cancelled;
            throw;
        }
        catch (Exception exception)
        {
            row.Error = exception.Message;
            row.State = TransferState.Failed;
            throw;
        }
    }
}
