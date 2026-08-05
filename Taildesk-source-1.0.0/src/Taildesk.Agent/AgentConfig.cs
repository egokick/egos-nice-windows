namespace Taildesk.Agent;

using Taildesk.Shared;

public sealed class AgentState
{
    private readonly JsonFileStore<AgentConfig> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AgentState(AgentConfig config, JsonFileStore<AgentConfig> store)
    {
        Config = config;
        _store = store;
    }

    public AgentConfig Config { get; }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _store.SaveAsync(Config, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkEnrolledAsync(CancellationToken cancellationToken = default)
    {
        Config.CompletedInviteId = Config.PendingInviteId;
        Config.PendingInviteId = null;
        Config.PendingInviteSecretProtected = string.Empty;
        await SaveAsync(cancellationToken);
    }
}
