using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Taildesk.Admin;

/// <summary>
/// A user-visible checkpoint in the release deployment transaction. Keeping this
/// in the view model means navigation cannot hide an operation that is still
/// running in the background.
/// </summary>
public sealed class ReleaseDeploymentProgress : INotifyPropertyChanged
{
    private string _status = "WAITING";
    private string _detail = "Waiting to start.";

    public ReleaseDeploymentProgress(string name) => Name = name;

    public string Name { get; }
    public string Status { get => _status; set { if (_status == value) return; _status = value; Changed(); } }
    public string Detail { get => _detail; set { if (_detail == value) return; _detail = value; Changed(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
