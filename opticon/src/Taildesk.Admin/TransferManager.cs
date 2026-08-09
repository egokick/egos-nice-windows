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
    public TransferState State
    {
        get => _state;
        internal set
        {
            _state = value;
            Changed();
            Changed(nameof(Progress));
            Changed(nameof(CanCancel));
            Changed(nameof(CanResume));
        }
    }
    public long Current { get => _current; internal set { _current = value; Changed(); Changed(nameof(Progress)); } }
    public long Total { get => _total; internal set { _total = value; Changed(); Changed(nameof(Progress)); } }
    public string Error { get => _error; internal set { _error = value; Changed(); } }
    public string Progress => Total <= 0 ? State.ToString() : $"{Current * 100 / Math.Max(1, Total)}%";
    public bool CanCancel => State is TransferState.Queued or TransferState.Running;
    public bool CanResume => State is TransferState.Cancelled or TransferState.Failed;

    internal TransferOperation? Operation { get; init; }
    internal CancellationTokenSource? Cancellation { get; set; }
    internal int Executing;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

internal abstract record TransferOperation(AgentClient Client, DeviceRecord Device, string Token);

internal sealed record DownloadTransferOperation(
    AgentClient Client,
    DeviceRecord Device,
    string Token,
    string Root,
    string RemotePath,
    string LocalRoot,
    string LocalRelativePath) : TransferOperation(Client, Device, Token);

internal sealed record UploadTransferOperation(
    AgentClient Client,
    DeviceRecord Device,
    string Token,
    string LocalPath,
    string Root,
    string DestinationDirectory,
    bool Overwrite,
    long SourceLength,
    DateTime SourceLastWriteTimeUtc) : TransferOperation(Client, Device, Token);

public sealed class TransferManager
{
    private const int MaximumConcurrentTransfers = 4;
    private readonly SemaphoreSlim _transferSlots = new(MaximumConcurrentTransfers, MaximumConcurrentTransfers);

    public ObservableCollection<TransferRow> Items { get; } = [];

    public TransferRow StartDownload(
        AgentClient client,
        DeviceRecord device,
        string token,
        string root,
        string remotePath,
        string localRoot,
        string localRelativePath)
    {
        var row = new TransferRow
        {
            Device = device.Name,
            File = Path.GetFileName(remotePath),
            Direction = TransferDirection.Download,
            Operation = new DownloadTransferOperation(client, device, token, root, remotePath, localRoot, localRelativePath)
        };
        Items.Insert(0, row);
        Start(row);
        return row;
    }

    public TransferRow StartUpload(
        AgentClient client,
        DeviceRecord device,
        string token,
        string localPath,
        string root,
        string destinationDirectory,
        bool overwrite)
    {
        var source = new FileInfo(localPath);
        var row = new TransferRow
        {
            Device = device.Name,
            File = Path.GetFileName(localPath),
            Direction = TransferDirection.Upload,
            Total = source.Length,
            Operation = new UploadTransferOperation(
                client, device, token, localPath, root, destinationDirectory, overwrite,
                source.Length, source.LastWriteTimeUtc)
        };
        Items.Insert(0, row);
        Start(row);
        return row;
    }

    public void Cancel(TransferRow row)
    {
        if (!Items.Contains(row) || !row.CanCancel) return;
        row.Cancellation?.Cancel();
    }

    public void Resume(TransferRow row)
    {
        if (!Items.Contains(row)) throw new InvalidOperationException("The selected transfer is no longer available.");
        if (!row.CanResume) throw new InvalidOperationException("Only a cancelled or failed transfer can be resumed.");
        Start(row);
    }

    private void Start(TransferRow row)
    {
        if (row.Operation is null) throw new InvalidOperationException("The transfer no longer has resumable operation details.");
        if (Interlocked.CompareExchange(ref row.Executing, 1, 0) != 0)
            throw new InvalidOperationException("The selected transfer is already running.");
        _ = RunAsync(row);
    }

    private async Task RunAsync(TransferRow row)
    {
        using var cancellation = new CancellationTokenSource();
        row.Cancellation = cancellation;
        row.Error = string.Empty;
        row.State = TransferState.Queued;
        var slotAcquired = false;
        var progress = new Progress<(long Current, long Total)>(value =>
        {
            row.Current = value.Current;
            row.Total = value.Total;
        });
        try
        {
            await _transferSlots.WaitAsync(cancellation.Token);
            slotAcquired = true;
            row.State = TransferState.Running;
            switch (row.Operation)
            {
                case DownloadTransferOperation download:
                    await download.Client.DownloadToRootAsync(
                        download.Device, download.Token, download.Root, download.RemotePath,
                        download.LocalRoot, download.LocalRelativePath, progress, cancellation.Token);
                    break;
                case UploadTransferOperation upload:
                    var source = new FileInfo(upload.LocalPath);
                    if (!source.Exists
                        || source.Length != upload.SourceLength
                        || source.LastWriteTimeUtc != upload.SourceLastWriteTimeUtc)
                        throw new IOException(
                            "The local source file changed after this transfer started. Start a new upload instead of resuming it.");
                    await upload.Client.UploadAsync(
                        upload.Device, upload.Token, row.Id, upload.LocalPath, upload.Root,
                        upload.DestinationDirectory, upload.Overwrite, progress, cancellation.Token);
                    break;
                default:
                    throw new InvalidOperationException("The transfer operation type is unsupported.");
            }
            row.State = TransferState.Completed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            row.State = TransferState.Cancelled;
        }
        catch (Exception exception)
        {
            row.Error = exception.Message;
            row.State = TransferState.Failed;
        }
        finally
        {
            if (slotAcquired) _transferSlots.Release();
            row.Cancellation = null;
            Interlocked.Exchange(ref row.Executing, 0);
        }
    }
}
