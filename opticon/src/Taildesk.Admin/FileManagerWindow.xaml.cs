using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Taildesk.Shared;

namespace Taildesk.Admin;

public partial class FileManagerWindow : Window
{
    private const int MaximumBatchDownloadFiles = 10_000;
    private const int MaximumBatchDownloadDepth = 256;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff"
    };

    private readonly DeviceRecord _device;
    private readonly string _token;
    private readonly AgentClient _client;
    private readonly TransferManager _transfers;
    private IReadOnlyList<RootDto> _roots = [];
    private IReadOnlyList<FileBrowserItem> _items = [];
    private string _currentPath = string.Empty;
    private bool _loading;
    private bool _working;
    private bool _suppressRootChange;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _thumbnailCancellation;

    public FileManagerWindow(DeviceRecord device, string token, AgentClient client, TransferManager transfers)
    {
        _device = device;
        _token = token;
        _client = client;
        _transfers = transfers;
        InitializeComponent();
        Title = $"Files - {device.Name}";
        DeviceText.Text = $"{device.Name}  {device.TailscaleIp}";
        Loaded += FileManagerWindow_Loaded;
        // Browser operations belong to this window. File transfers belong to
        // the application-wide TransferManager and deliberately survive close.
        Closed += (_, _) =>
        {
            _operationCancellation?.Cancel();
            _thumbnailCancellation?.Cancel();
        };
    }

    private async void FileManagerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            _roots = await _client.GetRootsAsync(_device, _token);
            RootCombo.ItemsSource = _roots;
            RootCombo.SelectedItem = _roots.FirstOrDefault(root =>
                root.PathHint.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
                ?? _roots.FirstOrDefault();
            if (RootCombo.SelectedItem is null)
                throw new InvalidOperationException("The device did not report an accessible local volume.");
        });
    }

    private RootDto CurrentRoot => RootCombo.SelectedItem as RootDto
        ?? throw new InvalidOperationException("Select a device location.");

    private IReadOnlyList<FileEntryDto> SelectedEntries =>
        (ShowThumbnailsCheck.IsChecked == true ? ThumbnailList.SelectedItems : FileGrid.SelectedItems)
        .Cast<FileBrowserItem>()
        .Select(item => item.Entry)
        .ToList();

    private FileEntryDto SelectedEntry
    {
        get
        {
            var selected = SelectedEntries;
            return selected.Count == 1
                ? selected[0]
                : throw new InvalidOperationException("Select exactly one file or folder for this action.");
        }
    }

    private async void RootCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRootChange || RootCombo.SelectedItem is null) return;
        _currentPath = string.Empty;
        await LoadFilesAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadFilesAsync();

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        _currentPath = string.Join('/', _currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1));
        await LoadFilesAsync();
    }

    private async void Go_Click(object sender, RoutedEventArgs e) => await NavigateToAddressAsync();

    private async void PathText_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await NavigateToAddressAsync();
    }

    private async Task NavigateToAddressAsync()
    {
        try
        {
            var entered = PathText.Text.Trim();
            if (string.IsNullOrWhiteSpace(entered)) return;
            var absolute = Path.GetFullPath(entered);
            var selectedRoot = IsWithin(CurrentRoot.PathHint, absolute)
                ? CurrentRoot
                : _roots.FirstOrDefault(root => root.Id.StartsWith("drive-", StringComparison.OrdinalIgnoreCase)
                                                && IsWithin(root.PathHint, absolute));
            if (selectedRoot is null)
                throw new InvalidOperationException("That path is not on an accessible local volume reported by the device.");

            var relative = Path.GetRelativePath(selectedRoot.PathHint, absolute);
            if (relative == ".") relative = string.Empty;
            if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                throw new InvalidOperationException("That path is outside the selected device location.");

            if (!ReferenceEquals(RootCombo.SelectedItem, selectedRoot))
            {
                _suppressRootChange = true;
                RootCombo.SelectedItem = selectedRoot;
                _suppressRootChange = false;
            }
            _currentPath = Normalize(relative);
            await LoadFilesAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
            UpdateAddressBar();
        }
    }

    private async void FileGrid_DoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedItemAsync();
    private async void ThumbnailList_DoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedItemAsync();

    private async Task OpenSelectedItemAsync()
    {
        if ((ShowThumbnailsCheck.IsChecked == true ? ThumbnailList.SelectedItem : FileGrid.SelectedItem) is not FileBrowserItem item)
            return;
        if (item.Entry.IsDirectory)
        {
            _currentPath = item.Entry.RelativePath;
            await LoadFilesAsync();
        }
        else
        {
            await OpenEntryAsync(item.Entry);
        }
    }

    private void ShowThumbnailsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (FileGrid is null || ThumbnailList is null) return;
        var showThumbnails = ShowThumbnailsCheck.IsChecked == true;
        if (showThumbnails)
        {
            CopySelection(FileGrid.SelectedItems, ThumbnailList.SelectedItems);
            FileGrid.Visibility = Visibility.Collapsed;
            ThumbnailList.Visibility = Visibility.Visible;
            _ = LoadThumbnailsAsync(_items);
        }
        else
        {
            CopySelection(ThumbnailList.SelectedItems, FileGrid.SelectedItems);
            ThumbnailList.Visibility = Visibility.Collapsed;
            FileGrid.Visibility = Visibility.Visible;
            _thumbnailCancellation?.Cancel();
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entries = SelectedEntries;
            if (entries.Count == 0) throw new InvalidOperationException("Select one or more files or folders to download.");
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose a local download folder", UseDescriptionForTitle = true };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var destinationRoot = Path.GetFullPath(dialog.SelectedPath);
            var rootId = CurrentRoot.Id;
            await RunAsync(async () =>
            {
                var cancellationToken = _operationCancellation!.Token;
                var batch = new DownloadBatch();
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var localPath = GetSafeDownloadPath(destinationRoot, entry.Name);
                    if (entry.IsDirectory)
                    {
                        await PlanFolderDownloadsAsync(
                            rootId,
                            entry.RelativePath,
                            localPath,
                            destinationRoot,
                            batch,
                            depth: 0,
                            cancellationToken);
                    }
                    else
                    {
                        AddPlannedDownload(rootId, entry.RelativePath, localPath, batch);
                    }
                }

                foreach (var localDirectory in batch.LocalDirectories.OrderBy(path => path.Length))
                    Directory.CreateDirectory(localDirectory);
                foreach (var download in batch.Downloads)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _transfers.StartDownload(
                        _client, _device, _token, download.RootId, download.RemotePath, download.LocalPath);
                }

                StatusText.Text = batch.FileCount == 0
                    ? $"Created {batch.FolderCount} local folder(s); the selected folders contained no downloadable files."
                    : $"Started {batch.FileCount} download(s) from {entries.Count} selected item(s) in Transfers; you can close this window.";
            });
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task PlanFolderDownloadsAsync(
        string rootId,
        string remoteDirectory,
        string localDirectory,
        string destinationRoot,
        DownloadBatch batch,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > MaximumBatchDownloadDepth)
            throw new InvalidDataException($"The selected folder tree exceeds the supported depth of {MaximumBatchDownloadDepth} folders.");

        batch.LocalDirectories.Add(localDirectory);
        var listing = await _client.GetFilesAsync(_device, _token, rootId, remoteDirectory, cancellationToken);
        foreach (var child in listing.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childLocalPath = GetSafeDownloadPath(localDirectory, child.Name, destinationRoot);
            if (child.IsDirectory)
            {
                await PlanFolderDownloadsAsync(
                    rootId,
                    child.RelativePath,
                    childLocalPath,
                    destinationRoot,
                    batch,
                    depth + 1,
                    cancellationToken);
            }
            else
            {
                AddPlannedDownload(rootId, child.RelativePath, childLocalPath, batch);
            }
        }
    }

    private static void AddPlannedDownload(string rootId, string remotePath, string localPath, DownloadBatch batch)
    {
        if (batch.FileCount >= MaximumBatchDownloadFiles)
            throw new InvalidDataException($"A download batch can contain at most {MaximumBatchDownloadFiles:N0} files.");
        batch.Downloads.Add(new PlannedDownload(rootId, remotePath, localPath));
    }

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = $"Send files to {_device.Name}" };
            if (dialog.ShowDialog(this) != true) return;
            foreach (var file in dialog.FileNames)
                _transfers.StartUpload(
                    _client, _device, _token, file, CurrentRoot.Id, _currentPath, overwrite: false);
            StatusText.Text = $"Started {dialog.FileNames.Length} upload(s) in Transfers; you can close this window.";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entry = SelectedEntry;
            if (entry.IsDirectory) throw new InvalidOperationException("Select a media file or document.");
            await OpenEntryAsync(entry);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task OpenEntryAsync(FileEntryDto entry)
    {
        await RunAsync(async () =>
        {
            var uri = await _client.CreateMediaUriAsync(_device, _token, CurrentRoot.Id, entry.RelativePath, _operationCancellation!.Token);
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }, $"Opened a five-minute stream for {entry.Name}.");
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new PromptWindow("New remote folder", "Folder name") { Owner = this };
        if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Value)) return;
        var name = Path.GetFileName(prompt.Value.Trim());
        if (name != prompt.Value.Trim()) { ShowError(new InvalidOperationException("Enter a folder name, not a path.")); return; }
        await RunAsync(() => _client.CreateDirectoryAsync(_device, _token, CurrentRoot.Id, Combine(_currentPath, name),
            _operationCancellation!.Token), "Folder created.");
        await LoadFilesAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entry = SelectedEntry;
            if (MessageBox.Show($"Permanently delete '{entry.Name}' on {_device.Name}?",
                    "Delete remote item", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await RunAsync(() => _client.DeleteAsync(_device, _token, CurrentRoot.Id, entry.RelativePath, entry.IsDirectory,
                _operationCancellation!.Token), "Remote item deleted.");
            await LoadFilesAsync();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task LoadFilesAsync()
    {
        if (_loading || RootCombo.SelectedItem is null) return;
        _loading = true;
        _thumbnailCancellation?.Cancel();
        try
        {
            StatusText.Text = "Loading...";
            var listing = await _client.GetFilesAsync(_device, _token, CurrentRoot.Id, _currentPath);
            _currentPath = listing.RelativePath;
            _items = listing.Entries.Select(entry => new FileBrowserItem(entry)).ToList();
            FileGrid.ItemsSource = _items;
            ThumbnailList.ItemsSource = _items;
            UpdateAddressBar();
            StatusText.Text = $"{listing.Entries.Count} items";
            if (ShowThumbnailsCheck.IsChecked == true) _ = LoadThumbnailsAsync(_items);
        }
        catch (Exception exception) { ShowError(exception); StatusText.Text = "Load failed"; }
        finally { _loading = false; }
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<FileBrowserItem> items)
    {
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation?.Dispose();
        var cancellation = _thumbnailCancellation = new CancellationTokenSource();
        var imageItems = items.Where(item => item.IsImage).Take(100).ToList();
        if (imageItems.Count == 0) return;
        try
        {
            var links = await _client.CreateMediaUrisAsync(
                _device,
                _token,
                CurrentRoot.Id,
                imageItems.Select(item => item.RelativePath).ToList(),
                cancellation.Token);
            foreach (var item in imageItems)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!links.TryGetValue(item.RelativePath, out var uri)) continue;
                var thumbnail = new BitmapImage();
                thumbnail.BeginInit();
                thumbnail.DecodePixelWidth = 240;
                thumbnail.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                thumbnail.UriSource = uri;
                thumbnail.EndInit();
                item.Thumbnail = thumbnail;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested)
                StatusText.Text = $"{items.Count} items - thumbnails unavailable: {exception.GetBaseException().Message}";
        }
    }

    private void UpdateAddressBar()
    {
        if (RootCombo.SelectedItem is not RootDto root) return;
        PathText.Text = string.IsNullOrWhiteSpace(_currentPath)
            ? root.PathHint
            : Path.Combine(root.PathHint, _currentPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private async Task RunAsync(Func<Task> action, string? success = null)
    {
        if (_working) return;
        try
        {
            _working = true;
            _operationCancellation = new CancellationTokenSource();
            StatusText.Text = "Working...";
            await action();
            if (success is not null) StatusText.Text = success;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operation canceled"; }
        catch (Exception exception) { ShowError(exception); }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            _working = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

    private static void CopySelection(System.Collections.IList source, System.Collections.IList destination)
    {
        var selected = source.Cast<object>().ToList();
        destination.Clear();
        foreach (var item in selected) destination.Add(item);
    }

    private static string GetSafeDownloadPath(string parent, string remoteName, string? destinationRoot = null)
    {
        if (string.IsNullOrWhiteSpace(remoteName)
            || remoteName is "." or ".."
            || !Path.GetFileName(remoteName).Equals(remoteName, StringComparison.Ordinal)
            || remoteName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("The Agent returned an unsafe file or folder name.");
        }

        var root = Path.GetFullPath(destinationRoot ?? parent);
        var candidate = Path.GetFullPath(Path.Combine(parent, remoteName));
        if (!IsWithin(root, candidate))
            throw new InvalidDataException("The Agent returned a path outside the selected download folder.");
        return candidate;
    }

    private static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var prefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Combine(string left, string right) => string.IsNullOrWhiteSpace(left) ? right : $"{left.TrimEnd('/')}/{right}";
    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
    private static void ShowError(Exception exception) => MessageBox.Show(exception.Message, "Opticon Files", MessageBoxButton.OK, MessageBoxImage.Error);

    private sealed class FileBrowserItem : INotifyPropertyChanged
    {
        private ImageSource? _thumbnail;

        public FileBrowserItem(FileEntryDto entry) => Entry = entry;

        public FileEntryDto Entry { get; }
        public string Name => Entry.Name;
        public string RelativePath => Entry.RelativePath;
        public DateTimeOffset LastWriteTime => Entry.LastWriteTime;
        public string Kind => Entry.IsDirectory ? "Folder" : string.IsNullOrWhiteSpace(Path.GetExtension(Name)) ? "File" : $"{Path.GetExtension(Name)[1..].ToUpperInvariant()} file";
        public string DisplaySize => Entry.IsDirectory ? string.Empty : FormatSize(Entry.Size);
        public string TileDetail => Entry.IsDirectory ? "Folder" : DisplaySize;
        public string IconGlyph => Entry.IsDirectory ? "\uE8B7" : "\uE8A5";
        public bool IsImage => !Entry.IsDirectory && ImageExtensions.Contains(Path.GetExtension(Name));

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value)) return;
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string FormatSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            var value = (double)Math.Max(0, bytes);
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
        }
    }

    private sealed class DownloadBatch
    {
        public List<PlannedDownload> Downloads { get; } = [];
        public HashSet<string> LocalDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int FileCount => Downloads.Count;
        public int FolderCount => LocalDirectories.Count;
    }

    private sealed record PlannedDownload(string RootId, string RemotePath, string LocalPath);
}
