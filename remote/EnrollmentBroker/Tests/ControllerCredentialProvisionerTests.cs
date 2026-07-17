using StayActive.EnrollmentBroker.Provisioning;
using StayActive.EnrollmentBroker.Security;

namespace StayActive.EnrollmentBroker.Tests;

public sealed class ControllerCredentialProvisionerTests
{
    [Fact]
    public void Store_command_reads_standard_input_and_writes_only_the_fixed_target_without_output()
    {
        const string controllerKey = "hskey-api-provisioning-secret";
        var store = new FakeControllerCredentialStore();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();

        var handled = ControllerCredentialProvisioner.TryHandle(
            [ControllerCredentialProvisioner.StoreControllerKeyArgument],
            new StringReader(controllerKey + Environment.NewLine),
            store,
            standardOutput,
            standardError,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(0, exitCode);
        var write = Assert.Single(store.Writes);
        Assert.Equal(ControllerCredential.TargetName, write.TargetName);
        Assert.Equal(controllerKey, write.Secret);
        Assert.Equal(string.Empty, standardOutput.ToString());
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public void Invalid_standard_input_is_rejected_without_echoing_the_supplied_value()
    {
        const string suppliedValue = "not-a-headscale-api-key";
        var store = new FakeControllerCredentialStore();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();

        var handled = ControllerCredentialProvisioner.TryHandle(
            [ControllerCredentialProvisioner.StoreControllerKeyArgument],
            new StringReader(suppliedValue),
            store,
            standardOutput,
            standardError,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(65, exitCode);
        Assert.Empty(store.Writes);
        AssertNoSecret(suppliedValue, standardOutput, standardError);
    }

    [Fact]
    public void Malformed_provisioning_arguments_do_not_echo_a_controller_key()
    {
        const string suppliedValue = "hskey-api-argument-must-not-be-accepted";
        var store = new FakeControllerCredentialStore();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();

        var handled = ControllerCredentialProvisioner.TryHandle(
            [$"{ControllerCredentialProvisioner.StoreControllerKeyArgument}={suppliedValue}"],
            new StringReader(string.Empty),
            store,
            standardOutput,
            standardError,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(64, exitCode);
        Assert.Empty(store.Writes);
        AssertNoSecret(suppliedValue, standardOutput, standardError);
    }

    [Fact]
    public void Delete_command_uses_the_fixed_target_and_does_not_consume_standard_input()
    {
        const string unreadInput = "hskey-api-delete-input-is-not-read";
        var store = new FakeControllerCredentialStore();
        var standardInput = new StringReader(unreadInput);
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();

        var handled = ControllerCredentialProvisioner.TryHandle(
            [ControllerCredentialProvisioner.DeleteControllerKeyArgument],
            standardInput,
            store,
            standardOutput,
            standardError,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(0, exitCode);
        Assert.Equal([ControllerCredential.TargetName], store.DeletedTargets);
        Assert.Equal(unreadInput, standardInput.ReadToEnd());
        Assert.Equal(string.Empty, standardOutput.ToString());
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public void Storage_failures_do_not_reflect_a_secret_from_the_store_exception()
    {
        const string suppliedValue = "hskey-api-store-failure-secret";
        var store = new FakeControllerCredentialStore
        {
            WriteException = new InvalidOperationException(suppliedValue)
        };
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();

        var handled = ControllerCredentialProvisioner.TryHandle(
            [ControllerCredentialProvisioner.StoreControllerKeyArgument],
            new StringReader(suppliedValue),
            store,
            standardOutput,
            standardError,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(1, exitCode);
        AssertNoSecret(suppliedValue, standardOutput, standardError);
    }

    private static void AssertNoSecret(string secret, StringWriter standardOutput, StringWriter standardError)
    {
        Assert.DoesNotContain(secret, standardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, standardError.ToString(), StringComparison.Ordinal);
    }
}
