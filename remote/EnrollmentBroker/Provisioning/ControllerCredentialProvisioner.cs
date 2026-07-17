using StayActive.EnrollmentBroker.Security;

namespace StayActive.EnrollmentBroker.Provisioning;

/// <summary>
/// Handles the one offline provisioning operation. The secret is accepted only
/// from standard input and is deliberately never echoed to standard output,
/// standard error, exceptions, or logs.
/// </summary>
public static class ControllerCredentialProvisioner
{
    public const string StoreControllerKeyArgument = "--store-controller-key";
    public const string DeleteControllerKeyArgument = "--delete-controller-key";
    private const int UsageExitCode = 64;
    private const int InvalidCredentialExitCode = 65;
    private const int StorageFailureExitCode = 1;

    /// <summary>
    /// Returns <see langword="true"/> only when command-line arguments select
    /// the provisioning mode (including a malformed provisioning invocation).
    /// </summary>
    public static bool TryHandle(
        string[] args,
        TextReader standardInput,
        IControllerCredentialStore credentialStore,
        TextWriter standardOutput,
        TextWriter standardError,
        out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        var requestsProvisioning = args.Any(argument =>
            argument.Equals(StoreControllerKeyArgument, StringComparison.Ordinal)
            || argument.StartsWith(StoreControllerKeyArgument + "=", StringComparison.Ordinal)
            || argument.Equals(DeleteControllerKeyArgument, StringComparison.Ordinal)
            || argument.StartsWith(DeleteControllerKeyArgument + "=", StringComparison.Ordinal));
        if (!requestsProvisioning)
        {
            exitCode = 0;
            return false;
        }

        if (args.Length != 1
            || (!args[0].Equals(StoreControllerKeyArgument, StringComparison.Ordinal)
                && !args[0].Equals(DeleteControllerKeyArgument, StringComparison.Ordinal)))
        {
            standardError.WriteLine("Usage: EnrollmentBroker [--store-controller-key < standard-input | --delete-controller-key]");
            exitCode = UsageExitCode;
            return true;
        }

        if (args[0].Equals(DeleteControllerKeyArgument, StringComparison.Ordinal))
        {
            try
            {
                credentialStore.DeleteGenericCredential(ControllerCredential.TargetName);
                exitCode = 0;
                return true;
            }
            catch (Exception)
            {
                standardError.WriteLine("Unable to delete the controller credential from Windows Credential Manager.");
                exitCode = StorageFailureExitCode;
                return true;
            }
        }

        try
        {
            var controllerKey = ControllerCredential.Validate(standardInput.ReadToEnd());
            credentialStore.WriteGenericCredential(ControllerCredential.TargetName, controllerKey);
            exitCode = 0;
            return true;
        }
        catch (ControllerCredentialException)
        {
            standardError.WriteLine("The controller credential supplied through standard input is invalid.");
            exitCode = InvalidCredentialExitCode;
            return true;
        }
        catch (Exception)
        {
            // Do not include exception text here: a third-party store must not
            // be able to reflect the supplied secret to the console.
            standardError.WriteLine("Unable to store the controller credential in Windows Credential Manager.");
            exitCode = StorageFailureExitCode;
            return true;
        }
    }
}
