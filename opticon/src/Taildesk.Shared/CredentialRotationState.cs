namespace Taildesk.Shared;

public static class CredentialRotationState
{
    public static readonly TimeSpan PreviousTokenGracePeriod = TimeSpan.FromMinutes(30);

    public static bool CanAuthenticate(
        AgentConfig config,
        string token,
        bool rotationEndpoint,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(config);
        var tokenHash = SecurityHelpers.HashToken(token);
        if (SecurityHelpers.FixedTimeEquals(tokenHash, config.AgentTokenHash)) return true;
        return rotationEndpoint
               && config.PendingCredentialRotationId.HasValue
               && config.PreviousAgentTokenExpiresAt > now
               && !string.IsNullOrWhiteSpace(config.PreviousAgentTokenHash)
               && SecurityHelpers.FixedTimeEquals(tokenHash, config.PreviousAgentTokenHash);
    }

    public static bool IsExactAppliedRotation(
        AgentConfig config,
        Guid operationId,
        string newAgentToken,
        string newRustDeskPassword)
    {
        ArgumentNullException.ThrowIfNull(config);
        var tokenHash = SecurityHelpers.HashToken(newAgentToken);
        var passwordHash = SecurityHelpers.HashToken(newRustDeskPassword);
        if (!SecurityHelpers.FixedTimeEquals(tokenHash, config.AgentTokenHash)) return false;
        return config.PendingCredentialRotationId == operationId
            ? SecurityHelpers.FixedTimeEquals(passwordHash, config.PendingCredentialRotationPasswordHash)
            : config.LastCompletedCredentialRotationId == operationId
              && SecurityHelpers.FixedTimeEquals(passwordHash, config.LastCompletedCredentialRotationPasswordHash);
    }

    public static void Begin(
        AgentConfig config,
        Guid operationId,
        string newAgentToken,
        string newRustDeskPassword,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (operationId == Guid.Empty) throw new InvalidOperationException("Credential rotation requires a non-empty operation ID.");
        if (config.PendingCredentialRotationId.HasValue && config.PendingCredentialRotationId != operationId)
            throw new InvalidOperationException("A different credential rotation is awaiting confirmation.");

        config.PreviousAgentTokenHash = config.AgentTokenHash;
        config.PreviousAgentTokenExpiresAt = now.Add(PreviousTokenGracePeriod);
        config.AgentTokenHash = SecurityHelpers.HashToken(newAgentToken);
        config.PendingCredentialRotationId = operationId;
        config.PendingCredentialRotationPasswordHash = SecurityHelpers.HashToken(newRustDeskPassword);
    }

    public static void Commit(AgentConfig config, Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.LastCompletedCredentialRotationId == operationId) return;
        if (operationId == Guid.Empty || config.PendingCredentialRotationId != operationId)
            throw new InvalidOperationException("The credential rotation operation is not awaiting confirmation.");

        config.LastCompletedCredentialRotationId = operationId;
        config.LastCompletedCredentialRotationPasswordHash = config.PendingCredentialRotationPasswordHash;
        config.PendingCredentialRotationId = null;
        config.PendingCredentialRotationPasswordHash = string.Empty;
        config.PreviousAgentTokenHash = string.Empty;
        config.PreviousAgentTokenExpiresAt = null;
    }
}
