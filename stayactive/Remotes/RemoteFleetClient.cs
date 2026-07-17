namespace StayActive.Remotes;

internal interface IRemoteFleetClient : IDisposable
{
    RemoteFleetSnapshot GetCachedSnapshot();

    Task RefreshAsync(CancellationToken cancellationToken);
}
