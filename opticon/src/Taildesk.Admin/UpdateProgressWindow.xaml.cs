using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;

namespace Taildesk.Admin;

public partial class UpdateProgressWindow : Window
{
    private bool _canClose;

    public UpdateProgressWindow(string deviceName, string currentVersion, string targetVersion)
    {
        InitializeComponent();
        TargetText.Text = $"Updating {deviceName}: {currentVersion} → {targetVersion}";
        Loaded += (_, _) => AttachProgressCollection();
    }

    public void FinishAndClose()
    {
        _canClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_canClose)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    private void AttachProgressCollection()
    {
        if (DataContext is not MainViewModel viewModel) return;
        viewModel.UpdateProgressLines.CollectionChanged += ProgressLines_CollectionChanged;
        Closed += (_, _) => viewModel.UpdateProgressLines.CollectionChanged -= ProgressLines_CollectionChanged;
    }

    private void ProgressLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ProgressList.Items.Count > 0)
            ProgressList.ScrollIntoView(ProgressList.Items[ProgressList.Items.Count - 1]);
    }
}
