using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Taildesk.Shared;

public sealed record SignedInviteEnvelope(int SchemaVersion, string Payload, string Signature);

public static class HostedInviteFile
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("OPTICON-LINK1");
    private const int NonceLength = 12;
    private const int TagLength = 16;

    public static byte[] CreateSigned(InvitePayload payload, Func<byte[], byte[]>? signer = null)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonDefaults.Options);
        var signature = signer is null ? InvitationSigning.Sign(payloadBytes) : signer(payloadBytes);
        var envelope = new SignedInviteEnvelope(1, Convert.ToBase64String(payloadBytes), Convert.ToBase64String(signature));
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
    }

    public static InvitePayload ReadSigned(ReadOnlySpan<byte> envelopeBytes, Func<byte[], byte[], bool>? verifier = null)
    {
        var envelope = JsonSerializer.Deserialize<SignedInviteEnvelope>(envelopeBytes, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The hosted invitation envelope is empty.");
        if (envelope.SchemaVersion != 1) throw new InvalidDataException("The hosted invitation envelope version is unsupported.");
        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The hosted invitation envelope is malformed.", exception);
        }
        if (!(verifier is null ? InvitationSigning.Verify(payloadBytes, signature) : verifier(payloadBytes, signature))) throw new InvalidDataException("The hosted invitation signature is invalid.");
        return JsonSerializer.Deserialize<InvitePayload>(payloadBytes, JsonDefaults.Options)
               ?? throw new InvalidDataException("The hosted invitation payload is empty.");
    }

    public static InvitePayload ReadWithEmbeddedValidationPolicy(ReadOnlySpan<byte> envelopeBytes)
    {
        var envelope = JsonSerializer.Deserialize<SignedInviteEnvelope>(envelopeBytes, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The hosted invitation envelope is empty.");
        if (envelope.SchemaVersion != 1)
            throw new InvalidDataException("The hosted invitation envelope version is unsupported.");
        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The hosted invitation envelope is malformed.", exception);
        }
        var payload = JsonSerializer.Deserialize<InvitePayload>(payloadBytes, JsonDefaults.Options)
                      ?? throw new InvalidDataException("The hosted invitation payload is empty.");
        payload.ClientInstallValidation = ClientInstallValidationPolicy.Normalize(payload.ClientInstallValidation);
        if (payload.ClientInstallValidation.IsEnabled(ClientInstallValidationStep.InvitationAuthenticity)
            && !InvitationSigning.Verify(payloadBytes, signature))
            throw new InvalidDataException("The hosted invitation signature is invalid.");
        return payload;
    }

    public static byte[] Encrypt(string fragmentKey, ReadOnlySpan<byte> plaintext)
    {
        if (string.IsNullOrWhiteSpace(fragmentKey)) throw new ArgumentException("An invitation fragment key is required.", nameof(fragmentKey));
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(fragmentKey));
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Magic);
            var result = new byte[Magic.Length + nonce.Length + tag.Length + ciphertext.Length];
            Magic.CopyTo(result, 0);
            nonce.CopyTo(result, Magic.Length);
            tag.CopyTo(result, Magic.Length + nonce.Length);
            ciphertext.CopyTo(result, Magic.Length + nonce.Length + tag.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] Decrypt(string fragmentKey, ReadOnlySpan<byte> encrypted)
    {
        if (string.IsNullOrWhiteSpace(fragmentKey)) throw new InvalidDataException("The invitation link is missing its private fragment key.");
        var minimum = Magic.Length + NonceLength + TagLength + 1;
        if (encrypted.Length < minimum || !encrypted[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("The hosted invitation is not a recognized encrypted Opticon invitation.");
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(fragmentKey));
        var plaintext = new byte[encrypted.Length - Magic.Length - NonceLength - TagLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(
                encrypted.Slice(Magic.Length, NonceLength),
                encrypted[(Magic.Length + NonceLength + TagLength)..],
                encrypted.Slice(Magic.Length + NonceLength, TagLength),
                plaintext,
                Magic);
            return plaintext;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The invitation link key is invalid or the hosted invitation was altered.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
