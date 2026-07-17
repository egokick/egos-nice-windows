namespace StayActive.EnrollmentBroker.Security;

/// <summary>
/// Small seam around Windows Credential Manager. The broker reads its fixed
/// controller credential through this interface so tests never need to create
/// or inspect an operating-system credential.
/// </summary>
public interface IControllerCredentialStore
{
    string ReadGenericCredential(string targetName);

    void WriteGenericCredential(string targetName, string secret);

    void DeleteGenericCredential(string targetName);
}
