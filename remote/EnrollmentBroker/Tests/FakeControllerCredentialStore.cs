using StayActive.EnrollmentBroker.Security;

namespace StayActive.EnrollmentBroker.Tests;

internal sealed class FakeControllerCredentialStore : IControllerCredentialStore
{
    private readonly Dictionary<string, string> _credentials = new(StringComparer.Ordinal);

    public FakeControllerCredentialStore(string? initialSecret = null)
    {
        if (initialSecret is not null)
        {
            _credentials.Add(ControllerCredential.TargetName, initialSecret);
        }
    }

    public List<string> ReadTargets { get; } = [];

    public List<(string TargetName, string Secret)> Writes { get; } = [];

    public List<string> DeletedTargets { get; } = [];

    public Exception? ReadException { get; set; }

    public Exception? WriteException { get; set; }

    public Exception? DeleteException { get; set; }

    public string ReadGenericCredential(string targetName)
    {
        ReadTargets.Add(targetName);
        if (ReadException is not null)
        {
            throw ReadException;
        }

        if (!_credentials.TryGetValue(targetName, out var secret))
        {
            throw new ControllerCredentialStoreException("The test credential was not found.");
        }

        return secret;
    }

    public void WriteGenericCredential(string targetName, string secret)
    {
        if (WriteException is not null)
        {
            throw WriteException;
        }

        Writes.Add((targetName, secret));
        _credentials[targetName] = secret;
    }

    public void DeleteGenericCredential(string targetName)
    {
        if (DeleteException is not null)
        {
            throw DeleteException;
        }

        DeletedTargets.Add(targetName);
        _credentials.Remove(targetName);
    }
}
