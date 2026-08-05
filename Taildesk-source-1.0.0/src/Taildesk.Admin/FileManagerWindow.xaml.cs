using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Taildesk.Shared;

namespace Taildesk.Admin;

public partial class FileManagerWindow : Window
{
    private readonly DeviceRecord _device;
    private readonly string _token;
    private readonly AgentClient _client;
    private readonly TransferManager _transfers;
    private string _currentPath = string.Empty;
    private bool _loading;
    private bool _working;
    private bool _operationWasCancelled;
    private CancellationTokenSource? _operationCancellation;

    public FileManagerWindow(DeviceRecord device, string token, AgentClient client, TransferManager transfers)
    {
        _device = device;
        _token = token;
        _client = client;
        _transfers = transfers;
        InitializeComponent();
        Title = $"Files — {device.Name}";
        DeviceText.Text = $"{device.Name}  {device.TailscaleIp}";
        Loaded += FileManagerWindow_Loaded;
        Closed += (_, _) => _operationCancellation?.Cancel();
    }

    private async void FileManagerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            RootCombo.ItemsSource = await _client.GetRootsAsync(_device, _token);
            RootCombo.SelectedIndex = 0;
        });
    }

    private RootDto CurrentRoot => RootCombo.SelectedItem as RootDto ?? throw new InvalidOperationException("Select a shared root.");
    private FileEntryDto SelectedEntry => FileGrid.SelectedItem as FileEntryDto ?? throw new InvalidOperationException("Select a file or folder.");

    private async void RootCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RootCombo.SelectedItem is null) return;
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

    private async void FileGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FileGrid.SelectedItem is not FileEntryDto entry) return;
        if (entry.IsDirectory)
        {
            _currentPath = entry.RelativePath;
            await LoadFilesAsync();
        }
        else
        {
            await OpenEntryAsync(entry);
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entry = SelectedEntry;
            if (entry.IsDirectory) throw new InvalidOperationException("Select a file to download.");
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose a local download folder", UseDescriptionForTitle = true };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var destination = Path.Combine(dialog.SelectedPath, entry.Name);
            await RunAsync(() => _transfers.DownloadAsync(_client, _device, _token, CurrentRoot.Id, entry.RelativePath, destination,
                    _operationCancellation!.Token),
                $"Downloaded {entry.Name}.");
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = $"Send files to {_device.Name}" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            await RunAsync(() => _transfers.UploadAsync(_client, _device, _token, file, CurrentRoot.Id, _currentPath, overwrite: false,
                    _operationCancellation!.Token),
                $"Uploaded {Path.GetFileName(file)}.");
            if (_operationWasCancelled) break;
        }
        await LoadFilesAsync();
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
        try
        {
            StatusText.Text = "Loading…";
            var listing = await _client.GetFilesAsync(_device, _token, CurrentRoot.Id, _currentPath);
            FileGrid.ItemsSource = listing.Entries;
            PathText.Text = $"{CurrentRoot.DisplayName}:/{listing.RelativePath}";
            StatusText.Text = $"{listing.Entries.Count} items";
        }
        catch (Exception exception) { ShowError(exception); StatusText.Text = "Load failed"; }
        finally { _loading = false; }
    }

    private async Task RunAsync(Func<Task> action, string? success = null)
    {
        if (_working) return;
        try
        {
            _working = true;
            _operationWasCancelled = false;
            _operationCancellation = new CancellationTokenSource();
            StatusText.Text = "Working…";
            await action();
            if (success is not null) StatusText.Text = success;
        }
        catch (OperationCanceledException) { _operationWasCancelled = true; StatusText.Text = "Operation canceled"; }
        catch (Exception exception) { ShowError(exception); }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            _working = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

    private static string Combine(string left, string right) => string.IsNullOrWhiteSpace(left) ? right : $"{left.TrimEnd('/')}/{right}";
    private static void ShowError(Exception exception) => MessageBox.Show(exception.Message, "Opticon Files", MessageBoxButton.OK, MessageBoxImage.Error);
}
