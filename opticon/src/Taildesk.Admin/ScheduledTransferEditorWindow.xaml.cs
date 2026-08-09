using System.Windows;
using System.Windows.Controls;
using Taildesk.Shared;

namespace Taildesk.Admin;

public partial class ScheduledTransferEditorWindow : Window
{
    private readonly AdminState _state;
    private readonly AgentClient _agents;
    private readonly ScheduledTransferDefinition _source;
    private bool _loading = true;

    public ScheduledTransferEditorWindow(AdminState state, AgentClient agents, ScheduledTransferDefinition? source = null)
    {
        _state = state;
        _agents = agents;
        _source = source?.Copy() ?? new ScheduledTransferDefinition();
        InitializeComponent();
        DeviceCombo.ItemsSource = state.Config.Devices.OrderBy(item => item.Name).ToArray();
        TimeZoneCombo.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        LoadValues();
        Loaded += async (_, _) =>
        {
            _loading = false;
            await LoadRootsAsync();
            UpdateScheduleControls();
            UpdateFilterControls();
            UpdatePreview();
        };
    }

    public ScheduledTransferDefinition? Result { get; private set; }

    private void LoadValues()
    {
        NameText.Text = _source.Name;
        DeviceCombo.SelectedValue = _source.DeviceId == Guid.Empty ? _state.Config.Devices.FirstOrDefault()?.Id : _source.DeviceId;
        DirectionCombo.SelectedIndex = _source.Direction == ScheduledTransferDirection.Download ? 1 : 0;
        LocalFolderText.Text = _source.LocalFolder;
        RemoteFolderText.Text = _source.RemoteFolder;
        FilterCombo.SelectedIndex = (int)_source.Filter;
        FilterPatternText.Text = _source.FilterPattern;
        RecursiveCheck.IsChecked = _source.Recursive;
        ModeCombo.SelectedIndex = _source.Mode == ScheduledTransferMode.Move ? 1 : 0;
        OverwriteCheck.IsChecked = _source.Overwrite;
        CronText.Text = _source.CronExpression;
        TimeZoneCombo.SelectedValue = string.IsNullOrWhiteSpace(_source.TimeZoneId) ? TimeZoneInfo.Local.Id : _source.TimeZoneId;
        EnabledCheck.IsChecked = _source.Enabled;
        WeekdayCombo.SelectedIndex = 1;
        SelectFrequency(_source.CronExpression);
    }

    private void SelectFrequency(string cron)
    {
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (cron == "* * * * *") FrequencyCombo.SelectedIndex = 0;
        else if (cron == "0 * * * *") FrequencyCombo.SelectedIndex = 1;
        else if (fields.Length == 5 && int.TryParse(fields[0], out var minute) && int.TryParse(fields[1], out var hour)
                 && fields[2] == "*" && fields[3] == "*" && fields[4] == "*")
        { FrequencyCombo.SelectedIndex = 2; TimeText.Text = $"{hour:00}:{minute:00}"; }
        else if (fields.Length == 5 && int.TryParse(fields[0], out minute) && int.TryParse(fields[1], out hour)
                 && fields[2] == "*" && fields[3] == "*" && int.TryParse(fields[4], out var weekday))
        { FrequencyCombo.SelectedIndex = 3; TimeText.Text = $"{hour:00}:{minute:00}"; WeekdayCombo.SelectedIndex = weekday % 7; }
        else FrequencyCombo.SelectedIndex = 4;
    }

    private async void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) await LoadRootsAsync();
    }

    private async Task LoadRootsAsync()
    {
        var selectedRoot = RemoteRootCombo.SelectedValue as string ?? _source.RemoteRoot;
        if (DeviceCombo.SelectedItem is not DeviceRecord device) return;
        try
        {
            var token = SecretProtector.Unprotect(device.AgentTokenProtected);
            var roots = await _agents.GetRootsAsync(device, token);
            RemoteRootCombo.ItemsSource = roots;
            RemoteRootCombo.SelectedValue = selectedRoot;
            if (RemoteRootCombo.SelectedIndex < 0) RemoteRootCombo.SelectedIndex = 0;
        }
        catch
        {
            RemoteRootCombo.ItemsSource = string.IsNullOrWhiteSpace(selectedRoot)
                ? Array.Empty<RootDto>() : new[] { new RootDto { Id = selectedRoot, DisplayName = selectedRoot } };
            RemoteRootCombo.SelectedValue = selectedRoot;
        }
    }

    private void BrowseLocal_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        { Description = "Choose the local source or destination folder", UseDescriptionForTitle = true };
        if (Directory.Exists(LocalFolderText.Text)) dialog.InitialDirectory = LocalFolderText.Text;
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) LocalFolderText.Text = dialog.SelectedPath;
    }

    private void DirectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFilterControls();

    private void UpdateFilterControls()
    {
        if (FilterPatternText is null || FilterCombo is null) return;
        FilterPatternText.IsEnabled = FilterCombo.SelectedIndex is 1 or 2;
        FilterPatternText.ToolTip = FilterCombo.SelectedIndex == 1 ? "Example: .pdf" : "Matched against the relative path, for example ^invoice-.*\\.csv$";
    }

    private void Frequency_Changed(object sender, EventArgs e)
    {
        if (_loading || FrequencyCombo is null) return;
        UpdateScheduleControls();
        if (FrequencyCombo.SelectedIndex != 4 && TryBuildFriendlyCron(out var cron)) CronText.Text = cron;
        UpdatePreview();
    }

    private void UpdateScheduleControls()
    {
        if (FrequencyCombo is null) return;
        TimeText.IsEnabled = FrequencyCombo.SelectedIndex is 2 or 3;
        WeekdayCombo.IsEnabled = FrequencyCombo.SelectedIndex == 3;
        CronText.IsReadOnly = FrequencyCombo.SelectedIndex != 4;
    }

    private bool TryBuildFriendlyCron(out string cron)
    {
        cron = string.Empty;
        if (FrequencyCombo.SelectedIndex == 0) { cron = "* * * * *"; return true; }
        if (FrequencyCombo.SelectedIndex == 1) { cron = "0 * * * *"; return true; }
        if (!TimeSpan.TryParseExact(TimeText.Text.Trim(), @"hh\:mm", null, out var time) || time >= TimeSpan.FromDays(1)) return false;
        if (FrequencyCombo.SelectedIndex == 2) { cron = $"{time.Minutes} {time.Hours} * * *"; return true; }
        if (FrequencyCombo.SelectedIndex == 3 && WeekdayCombo.SelectedItem is ComboBoxItem { Tag: string day })
        { cron = $"{time.Minutes} {time.Hours} * * {day}"; return true; }
        return false;
    }

    private void CronText_Changed(object sender, EventArgs e)
    {
        if (!_loading) UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (PreviewText is null || string.IsNullOrWhiteSpace(CronText?.Text)) return;
        try
        {
            var zone = TimeZoneCombo.SelectedItem as TimeZoneInfo ?? TimeZoneInfo.Local;
            var next = CronSchedule.Parse(CronText.Text).GetNextOccurrence(DateTimeOffset.UtcNow, zone);
            var local = TimeZoneInfo.ConvertTime(next, zone);
            PreviewText.Text = $"{CronSchedule.Describe(CronText.Text)} · next: {local:g} ({zone.StandardName})";
        }
        catch (Exception exception) { PreviewText.Text = exception.Message; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DeviceCombo.SelectedItem is not DeviceRecord device) throw new InvalidDataException("Choose a device.");
            if (RemoteRootCombo.SelectedValue is not string remoteRoot || string.IsNullOrWhiteSpace(remoteRoot))
                throw new InvalidDataException("Choose a remote shared folder. The device may need to be online once to load its folders.");
            var definition = _source.Copy();
            definition.Name = NameText.Text;
            definition.DeviceId = device.Id;
            definition.Direction = DirectionCombo.SelectedIndex == 1 ? ScheduledTransferDirection.Download : ScheduledTransferDirection.Upload;
            definition.LocalFolder = LocalFolderText.Text;
            definition.RemoteRoot = remoteRoot;
            definition.RemoteFolder = RemoteFolderText.Text;
            definition.Filter = (ScheduledTransferFilter)Math.Max(0, FilterCombo.SelectedIndex);
            definition.FilterPattern = FilterPatternText.Text;
            definition.Recursive = RecursiveCheck.IsChecked == true;
            definition.Mode = ModeCombo.SelectedIndex == 1 ? ScheduledTransferMode.Move : ScheduledTransferMode.Copy;
            definition.Overwrite = OverwriteCheck.IsChecked == true;
            definition.CronExpression = CronText.Text.Trim();
            definition.TimeZoneId = (TimeZoneCombo.SelectedItem as TimeZoneInfo)?.Id ?? TimeZoneInfo.Local.Id;
            definition.Enabled = EnabledCheck.IsChecked == true;
            ScheduledTransferRules.Validate(definition);
            if (definition.Mode == ScheduledTransferMode.Move && MessageBox.Show(
                    "Transfer files deletes each original only after Opticon confirms that file reached the destination. Continue with move mode?",
                    "Confirm transfer-and-delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Result = definition;
            DialogResult = true;
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, "Cannot save schedule", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
