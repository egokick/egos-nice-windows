using StayActive.RemoteHub.Domain;

namespace StayActive.RemoteHub.Persistence;

public interface IRemoteInventoryStore
{
    Task<IReadOnlyList<RemoteInventoryRecord>> ListAsync(bool verifiedOnly, CancellationToken cancellationToken);

    Task<InventoryUpsertResult> UpsertAsync(
        string headscaleNodeId,
        NormalizedInventoryUpdate update,
        string actorSubject,
        string correlationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteHubAuditEvent>> ReadAuditAsync(int take, CancellationToken cancellationToken);
}
