using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Taildesk.Shared;

public static class InviteContainer
{
    private const int FixedMetadataLength = sizeof(long) + 32;
    private const int SearchWindowBytes = 1024 * 1024;
    private static readonly byte[] FooterMagic = Encoding.ASCII.GetBytes("OPTICON-INVITE2");

    public static async Task CreateAsync(
        string launcherPath,
        string archivePath,
        string outputPath,
        CancellationToken cancellationToken = default,
        Func<byte[], byte[]>? signer = null)
    {
        var archiveLength = new FileInfo(archivePath).Length;
        byte[] archiveHash;
        await using (var archiveForHash = File.OpenRead(archivePath))
        {
            archiveHash = await SHA256.HashDataAsync(archiveForHash, cancellationToken);
        }
        var signedMetadata = BuildSignedMetadata(archiveLength, archiveHash);
        var signature = (signer ?? (data => InvitationSigning.Sign(data)))(signedMetadata);

        await using var destination = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using (var launcher = File.OpenRead(launcherPath)) await launcher.CopyToAsync(destination, cancellationToken);
        await using (var archive = File.OpenRead(archivePath)) await archive.CopyToAsync(destination, cancellationToken);
        await destination.WriteAsync(BitConverter.GetBytes(archiveLength), cancellationToken);
        await destination.WriteAsync(archiveHash, cancellationToken);
        await destination.WriteAsync(signature, cancellationToken);
        await destination.WriteAsync(BitConverter.GetBytes(signature.Length), cancellationToken);
        await destination.WriteAsync(FooterMagic, cancellationToken);
    }

    public static async Task ExtractAsync(
        string executablePath,
        string destination,
        CancellationToken cancellationToken = default,
        RSA? verifier = null)
    {
        await using var executable = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var magicOffset = await FindFooterMagicAsync(executable, cancellationToken);
        if (magicOffset < FixedMetadataLength + sizeof(int)) throw new InvalidDataException("This is not a complete Opticon invitation.");

        executable.Position = magicOffset - sizeof(int);
        var signatureLengthBytes = new byte[sizeof(int)];
        await executable.ReadExactlyAsync(signatureLengthBytes, cancellationToken);
        var signatureLength = BitConverter.ToInt32(signatureLengthBytes);
        if (signatureLength is < 256 or > 1024) throw new InvalidDataException("The Opticon invitation signature is damaged.");
        var metadataOffset = magicOffset - sizeof(int) - signatureLength - FixedMetadataLength;
        if (metadataOffset <= 0) throw new InvalidDataException("The Opticon invitation metadata is damaged.");

        executable.Position = metadataOffset;
        var lengthBytes = new byte[sizeof(long)];
        await executable.ReadExactlyAsync(lengthBytes, cancellationToken);
        var archiveLength = BitConverter.ToInt64(lengthBytes);
        var expectedHash = new byte[32];
        await executable.ReadExactlyAsync(expectedHash, cancellationToken);
        var signature = new byte[signatureLength];
        await executable.ReadExactlyAsync(signature, cancellationToken);
        var archiveOffset = metadataOffset - archiveLength;
        if (archiveLength <= 0 || archiveOffset <= 0) throw new InvalidDataException("The Opticon invitation payload is damaged.");

        var signedMetadata = BuildSignedMetadata(archiveLength, expectedHash);
        var signatureValid = verifier is null
            ? InvitationSigning.Verify(signedMetadata, signature)
            : verifier.VerifyData(signedMetadata, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        if (!signatureValid) throw new InvalidDataException("The Opticon invitation signature is invalid.");

        Directory.CreateDirectory(destination);
        var archivePath = Path.Combine(destination, "invitation.zip");
        executable.Position = archiveOffset;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var remaining = archiveLength;
            var buffer = new byte[1024 * 1024];
            while (remaining > 0)
            {
                var read = await executable.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) throw new EndOfStreamException("The Opticon invitation ended unexpectedly.");
                hasher.AppendData(buffer, 0, read);
                await archive.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
        if (!CryptographicOperations.FixedTimeEquals(hasher.GetHashAndReset(), expectedHash))
        {
            File.Delete(archivePath);
            throw new InvalidDataException("The Opticon invitation payload hash does not match its signed value.");
        }

        ZipFile.ExtractToDirectory(archivePath, destination, true);
        File.Delete(archivePath);
    }

    private static byte[] BuildSignedMetadata(long archiveLength, byte[] archiveHash)
    {
        var metadata = new byte[FixedMetadataLength];
        BitConverter.GetBytes(archiveLength).CopyTo(metadata, 0);
        archiveHash.CopyTo(metadata, sizeof(long));
        return metadata;
    }

    private static async Task<long> FindFooterMagicAsync(FileStream executable, CancellationToken cancellationToken)
    {
        var count = (int)Math.Min(executable.Length, SearchWindowBytes);
        var tail = new byte[count];
        executable.Position = executable.Length - count;
        await executable.ReadExactlyAsync(tail, cancellationToken);
        for (var index = tail.Length - FooterMagic.Length; index >= 0; index--)
        {
            if (tail.AsSpan(index, FooterMagic.Length).SequenceEqual(FooterMagic))
            {
                return executable.Length - count + index;
            }
        }
        throw new InvalidDataException("The signed Opticon invitation payload was not found.");
    }
}
