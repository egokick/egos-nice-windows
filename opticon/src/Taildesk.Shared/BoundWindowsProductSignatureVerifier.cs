using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Taildesk.Shared;

/// <summary>
/// Verifies one Authenticode signature, binds the pinned leaf to the exact
/// signer Windows validated, and validates the exact RFC 3161 token attached
/// to that signer's encrypted hash. All identity decisions come from native
/// provider/message state; no path-based certificate lookup is used.
/// </summary>
internal static class BoundWindowsProductSignatureVerifier
{
    private const int Success = 0;
    private const int CertificateUntrustedRoot = unchecked((int)0x800B0109);
    private const uint X509AsnEncoding = 0x00000001;
    private const uint Pkcs7AsnEncoding = 0x00010000;
    private const uint MessageEncoding = X509AsnEncoding | Pkcs7AsnEncoding;
    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckChainExcludeRoot = 0x00000080;
    private const uint CacheOnlyUrlRetrieval = 0x00001000;
    private const uint VerifySpecificSignature = 0x00000001;
    private const uint GetSecondarySignatureCount = 0x00000002;
    private const uint QueryObjectBlob = 2;
    private const uint QueryContentPkcs7Signed = 8;
    private const uint QueryContentPkcs7SignedEmbed = 10;
    private const uint QueryContentFlagPkcs7Signed = 1U << (int)QueryContentPkcs7Signed;
    private const uint QueryContentFlagPkcs7SignedEmbed = 1U << (int)QueryContentPkcs7SignedEmbed;
    private const uint QueryFormatBinary = 1;
    private const uint QueryFormatFlagBinary = 1U << (int)QueryFormatBinary;
    private const uint MessageTypeParameter = 1;
    private const uint MessageSignerCountParameter = 5;
    private const uint MessageSignerInfoParameter = 6;
    private const uint SignedMessageType = 2;
    private const uint ControlVerifySignature = 1;
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";
    private const string TimestampingEku = "1.3.6.1.5.5.7.3.8";
    private const string WindowsRfc3161AttributeOid = "1.3.6.1.4.1.311.3.3.1";
    private const int MaximumCertificateTableBytes = 16 * 1024 * 1024;
    private const int MaximumNativeSignerInfoBytes = 4 * 1024 * 1024;
    private const int MaximumCertificateBytes = 1024 * 1024;
    private const int MaximumTimestampCertificates = 64;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static async Task VerifyPinnedAsync(
        string path,
        X509Certificate2 expectedSigner,
        bool requireWindowsTrustedChain,
        bool requireRfc3161Timestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expectedSigner);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Authenticode verification requires Windows.");

        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        using var lockedFile = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.RandomAccess);
        if (lockedFile.Length <= 0)
            throw new InvalidDataException("The signed executable is empty.");

        var pkcs7 = ReadSinglePkcs7Signature(lockedFile);
        RequireExactDerSequence(pkcs7, "The Authenticode PKCS#7 value is not one exact DER ContentInfo.");

        var addedReference = false;
        lockedFile.SafeFileHandle.DangerousAddRef(ref addedReference);
        try
        {
            using var verifiedSigner = VerifyWithWindows(
                fullPath,
                lockedFile.SafeFileHandle.DangerousGetHandle(),
                requireWindowsTrustedChain);
            if (!RawEquals(verifiedSigner, expectedSigner))
                throw new InvalidDataException(
                    "The exact Authenticode signer validated by Windows does not match the pinned Opticon product certificate.");
            RequireEku(verifiedSigner, CodeSigningEku, "code signing");
            if (requireWindowsTrustedChain
                && verifiedSigner.SubjectName.RawData.AsSpan().SequenceEqual(verifiedSigner.IssuerName.RawData))
                throw new InvalidDataException("The production Opticon product signer must not be self-signed.");

            cancellationToken.ThrowIfCancellationRequested();
            VerifyPrimaryMessage(pkcs7, expectedSigner, requireRfc3161Timestamp);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            if (addedReference)
                lockedFile.SafeFileHandle.DangerousRelease();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static X509Certificate2 VerifyWithWindows(
        string path,
        IntPtr heldFileHandle,
        bool requireTrustedChain)
    {
        if (heldFileHandle is 0 or -1)
            throw new InvalidDataException("The held executable handle is invalid.");

        var pathPointer = Marshal.StringToCoTaskMemUni(path);
        var fileInfoPointer = IntPtr.Zero;
        var signatureSettingsPointer = IntPtr.Zero;
        var trustData = default(WinTrustData);
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructureSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = pathPointer,
                FileHandle = heldFileHandle
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var settings = new WinTrustSignatureSettings
            {
                StructureSize = checked((uint)Marshal.SizeOf<WinTrustSignatureSettings>()),
                Index = 0,
                Flags = VerifySpecificSignature | GetSecondarySignatureCount
            };
            signatureSettingsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustSignatureSettings>());
            Marshal.StructureToPtr(settings, signatureSettingsPointer, false);

            trustData = new WinTrustData
            {
                StructureSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                UiChoice = UiNone,
                RevocationChecks = requireTrustedChain ? RevokeWholeChain : RevokeNone,
                UnionChoice = ChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = StateActionVerify,
                ProviderFlags = requireTrustedChain
                    ? RevocationCheckChainExcludeRoot
                    : CacheOnlyUrlRetrieval,
                SignatureSettings = signatureSettingsPointer
            };

            var action = GenericVerifyV2;
            var trustResult = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            if (trustResult != Success
                && (requireTrustedChain || trustResult != CertificateUntrustedRoot))
                throw new InvalidDataException(
                    $"Windows rejected the Authenticode signature (0x{unchecked((uint)trustResult):X8}).");

            settings = Marshal.PtrToStructure<WinTrustSignatureSettings>(signatureSettingsPointer);
            if (settings.SecondarySignatureCount != 0 || settings.VerifiedSignatureIndex != 0)
                throw new InvalidDataException(
                    "The executable has a secondary, nested, or non-primary Authenticode signature.");
            if (trustData.StateData == IntPtr.Zero)
                throw new InvalidDataException("Windows returned no Authenticode provider state.");

            var providerData = WTHelperProvDataFromStateData(trustData.StateData);
            if (providerData == IntPtr.Zero)
                throw new InvalidDataException("Windows returned no Authenticode provider data.");
            var providerSigner = WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
            if (providerSigner == IntPtr.Zero)
                throw new InvalidDataException("Windows returned no primary Authenticode signer.");
            var providerCertificate = WTHelperGetProvCertFromChain(providerSigner, 0);
            if (providerCertificate == IntPtr.Zero)
                throw new InvalidDataException("Windows returned no primary Authenticode leaf certificate.");
            var certificateHeader = Marshal.PtrToStructure<CryptProviderCertificateHeader>(providerCertificate);
            if (certificateHeader.StructureSize < Marshal.OffsetOf<CryptProviderCertificateHeader>(
                    nameof(CryptProviderCertificateHeader.CertificateContext)).ToInt64() + IntPtr.Size
                || certificateHeader.CertificateContext == IntPtr.Zero)
                throw new InvalidDataException("Windows returned malformed Authenticode leaf state.");
            return CopyCertificateContext(certificateHeader.CertificateContext);
        }
        finally
        {
            if (trustData.StateData != IntPtr.Zero)
            {
                trustData.StateAction = StateActionClose;
                var action = GenericVerifyV2;
                _ = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            }
            if (signatureSettingsPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(signatureSettingsPointer);
            if (fileInfoPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private static void VerifyPrimaryMessage(
        byte[] pkcs7,
        X509Certificate2 expectedSigner,
        bool requireRfc3161Timestamp)
    {
        using var message = new QueriedSignedMessage(pkcs7);
        RequireOneSignedMessage(message.Message, "Authenticode");
        var signerInfoPointer = ReadMessageParameter(
            message.Message, MessageSignerInfoParameter, 0, MaximumNativeSignerInfoBytes,
            "The Authenticode signer information is invalid.");
        try
        {
            var signer = Marshal.PtrToStructure<CmsgSignerInfo>(signerInfoPointer);
            if (signer.Version != 1)
                throw new InvalidDataException("The primary Authenticode SignerInfo version is unsupported.");
            RequireSha256(signer.HashAlgorithm, "The primary Authenticode digest must be SHA-256.");
            VerifyMessageSignatureWithExactCertificate(message.Message, expectedSigner);

            var encryptedHash = CopyBlob(
                signer.EncryptedHash, 1, MaximumCertificateTableBytes,
                "The primary Authenticode signature value is invalid.");

            if (!requireRfc3161Timestamp)
            {
                if (signer.UnauthenticatedAttributes.Count != 0)
                    throw new InvalidDataException(
                        "A developer Authenticode signature contains unsupported unauthenticated attributes.");
                return;
            }

            if (signer.UnauthenticatedAttributes.Count != 1
                || signer.UnauthenticatedAttributes.Attributes == IntPtr.Zero)
                throw new InvalidDataException(
                    "The production Authenticode signer must contain exactly one RFC 3161 timestamp attribute.");
            var attribute = Marshal.PtrToStructure<CryptAttribute>(
                signer.UnauthenticatedAttributes.Attributes);
            if (!ReadOid(attribute.ObjectId).Equals(WindowsRfc3161AttributeOid, StringComparison.Ordinal)
                || attribute.ValueCount != 1
                || attribute.Values == IntPtr.Zero)
                throw new InvalidDataException(
                    "The production Authenticode signer contains a legacy, nested, or malformed timestamp attribute.");
            var timestampBlob = Marshal.PtrToStructure<CryptDataBlob>(attribute.Values);
            var timestampToken = CopyBlob(
                timestampBlob, 1, MaximumNativeSignerInfoBytes,
                "The production RFC 3161 timestamp token is invalid.");
            RequireExactDerSequence(timestampToken,
                "The production RFC 3161 timestamp token is not one exact DER ContentInfo.");
            VerifyTimestampToken(timestampToken, encryptedHash, expectedSigner);
        }
        finally
        {
            Marshal.FreeHGlobal(signerInfoPointer);
        }
    }

    private static void VerifyMessageSignatureWithExactCertificate(
        IntPtr message,
        X509Certificate2 expectedSigner)
    {
        IntPtr certificateHandle;
        try
        {
            certificateHandle = expectedSigner.Handle;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The pinned product certificate is unavailable.", exception);
        }
        if (certificateHandle == IntPtr.Zero)
            throw new InvalidDataException("The pinned product certificate has no native context.");
        var context = Marshal.PtrToStructure<CertificateContextHeader>(certificateHandle);
        if (context.CertificateInfo == IntPtr.Zero
            || (context.EncodingType & X509AsnEncoding) == 0)
            throw new InvalidDataException("The pinned product certificate context is malformed.");
        if (!CryptMsgControl(message, 0, ControlVerifySignature, context.CertificateInfo))
            throw NativeFailure(
                "The exact pinned product certificate did not verify the primary Authenticode SignerInfo.");
        GC.KeepAlive(expectedSigner);
    }

    private static void VerifyTimestampToken(
        byte[] timestampToken,
        byte[] primaryEncryptedHash,
        X509Certificate2 expectedSigner)
    {
        RequireTimestampMessageShape(timestampToken);

        var timestampContext = IntPtr.Zero;
        var timestampSigner = IntPtr.Zero;
        var timestampStore = IntPtr.Zero;
        try
        {
            if (!CryptVerifyTimeStampSignature(
                    timestampToken, checked((uint)timestampToken.Length),
                    primaryEncryptedHash, checked((uint)primaryEncryptedHash.Length),
                    IntPtr.Zero,
                    out timestampContext, out timestampSigner, out timestampStore))
                throw NativeFailure(
                    "The RFC 3161 timestamp token does not cover the primary Authenticode signature value.");
            if (timestampContext == IntPtr.Zero || timestampSigner == IntPtr.Zero
                || timestampStore == IntPtr.Zero)
                throw new InvalidDataException("Windows returned incomplete RFC 3161 timestamp state.");

            var context = Marshal.PtrToStructure<CryptTimestampContext>(timestampContext);
            if (context.TimestampInfo == IntPtr.Zero)
                throw new InvalidDataException("Windows returned no decoded RFC 3161 timestamp information.");
            var info = Marshal.PtrToStructure<CryptTimestampInfo>(context.TimestampInfo);
            if (info.Version != 1 || string.IsNullOrWhiteSpace(ReadOid(info.PolicyId)))
                throw new InvalidDataException("The RFC 3161 timestamp information is malformed.");
            RequireSha256(info.HashAlgorithm,
                "The RFC 3161 message-imprint algorithm must be SHA-256.");
            if (info.HashedMessage.DataLength != 32 || info.HashedMessage.Data == IntPtr.Zero)
                throw new InvalidDataException("The RFC 3161 SHA-256 message imprint is malformed.");

            var generatedAt = ReadTimestamp(info.Time);
            var now = DateTimeOffset.UtcNow;
            if (generatedAt > now.AddMinutes(5)
                || generatedAt.UtcDateTime < expectedSigner.NotBefore.ToUniversalTime()
                || generatedAt.UtcDateTime > expectedSigner.NotAfter.ToUniversalTime())
                throw new InvalidDataException(
                    "The RFC 3161 generation time is outside the pinned product certificate's validity interval.");

            using var tsaCertificate = CopyCertificateContext(timestampSigner);
            RequireEku(tsaCertificate, TimestampingEku, "time stamping");
            if (tsaCertificate.SubjectName.RawData.AsSpan().SequenceEqual(tsaCertificate.IssuerName.RawData))
                throw new InvalidDataException("The production RFC 3161 signer must not be self-signed.");

            var supportingCertificates = CopyCertificatesFromStore(timestampStore);
            try
            {
                VerifyTimestampChain(tsaCertificate, supportingCertificates, generatedAt);
            }
            finally
            {
                foreach (var certificate in supportingCertificates)
                    certificate.Dispose();
            }
        }
        finally
        {
            if (timestampContext != IntPtr.Zero)
                CryptMemFree(timestampContext);
            if (timestampSigner != IntPtr.Zero)
                _ = CertFreeCertificateContext(timestampSigner);
            if (timestampStore != IntPtr.Zero)
                _ = CertCloseStore(timestampStore, 0);
        }
    }

    private static void RequireTimestampMessageShape(byte[] timestampToken)
    {
        using var message = new QueriedSignedMessage(timestampToken);
        RequireOneSignedMessage(message.Message, "RFC 3161 timestamp token");
        var signerInfoPointer = ReadMessageParameter(
            message.Message, MessageSignerInfoParameter, 0, MaximumNativeSignerInfoBytes,
            "The RFC 3161 token signer information is invalid.");
        try
        {
            var signer = Marshal.PtrToStructure<CmsgSignerInfo>(signerInfoPointer);
            RequireSha256(signer.HashAlgorithm,
                "The RFC 3161 token signer digest must be SHA-256.");
            if (signer.UnauthenticatedAttributes.Count != 0)
                throw new InvalidDataException(
                    "The RFC 3161 token signer contains a nested or legacy unauthenticated signature.");
        }
        finally
        {
            Marshal.FreeHGlobal(signerInfoPointer);
        }
    }

    private static void RequireOneSignedMessage(IntPtr message, string description)
    {
        if (ReadMessageUInt32(message, MessageTypeParameter, 0) != SignedMessageType)
            throw new InvalidDataException($"The {description} is not a signed CMS message.");
        if (ReadMessageUInt32(message, MessageSignerCountParameter, 0) != 1)
            throw new InvalidDataException($"The {description} must contain exactly one SignerInfo.");
    }

    private static uint ReadMessageUInt32(IntPtr message, uint parameter, uint index)
    {
        var size = checked((uint)sizeof(uint));
        if (!CryptMsgGetParamUInt32(message, parameter, index, out var value, ref size)
            || size != sizeof(uint))
            throw NativeFailure("Windows could not read signed-message metadata.");
        return value;
    }

    private static IntPtr ReadMessageParameter(
        IntPtr message,
        uint parameter,
        uint index,
        int maximumBytes,
        string failureMessage)
    {
        uint size = 0;
        if (!CryptMsgGetParamBuffer(message, parameter, index, IntPtr.Zero, ref size)
            || size == 0 || size > maximumBytes)
            throw NativeFailure(failureMessage);
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            var actualSize = size;
            if (!CryptMsgGetParamBuffer(message, parameter, index, buffer, ref actualSize)
                || actualSize == 0 || actualSize > size)
                throw NativeFailure(failureMessage);
            return buffer;
        }
        catch
        {
            Marshal.FreeHGlobal(buffer);
            throw;
        }
    }

    private static List<X509Certificate2> CopyCertificatesFromStore(IntPtr store)
    {
        var certificates = new List<X509Certificate2>();
        var current = IntPtr.Zero;
        try
        {
            while (true)
            {
                current = CertEnumCertificatesInStore(store, current);
                if (current == IntPtr.Zero)
                    return certificates;
                if (certificates.Count >= MaximumTimestampCertificates)
                    throw new InvalidDataException("The RFC 3161 token contains too many certificates.");
                certificates.Add(CopyCertificateContext(current));
            }
        }
        catch
        {
            if (current != IntPtr.Zero)
                _ = CertFreeCertificateContext(current);
            foreach (var certificate in certificates)
                certificate.Dispose();
            throw;
        }
    }

    private static void VerifyTimestampChain(
        X509Certificate2 timestampSigner,
        IReadOnlyCollection<X509Certificate2> supportingCertificates,
        DateTimeOffset generatedAt)
    {
        if (generatedAt.UtcDateTime < timestampSigner.NotBefore.ToUniversalTime()
            || generatedAt.UtcDateTime > timestampSigner.NotAfter.ToUniversalTime())
            throw new InvalidDataException(
                "The RFC 3161 signer was not valid at the timestamp generation time.");

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.System;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.VerificationTime = generatedAt.UtcDateTime;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(20);
        chain.ChainPolicy.DisableCertificateDownloads = false;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(TimestampingEku));
        foreach (var certificate in supportingCertificates)
            chain.ChainPolicy.ExtraStore.Add(certificate);
        if (!chain.Build(timestampSigner)
            || chain.ChainStatus.Any(status => status.Status != X509ChainStatusFlags.NoError))
            throw new InvalidDataException(
                "Windows did not build a trusted RFC 3161 time-stamping chain at the token generation time.");
    }

    private static X509Certificate2 CopyCertificateContext(IntPtr certificateContext)
    {
        if (certificateContext == IntPtr.Zero)
            throw new InvalidDataException("A native certificate context is missing.");
        var context = Marshal.PtrToStructure<CertificateContextHeader>(certificateContext);
        if ((context.EncodingType & X509AsnEncoding) == 0
            || context.Encoded == IntPtr.Zero
            || context.EncodedLength is 0 or > MaximumCertificateBytes)
            throw new InvalidDataException("A native certificate context is malformed.");
        var raw = new byte[checked((int)context.EncodedLength)];
        Marshal.Copy(context.Encoded, raw, 0, raw.Length);
        try
        {
            return new X509Certificate2(raw);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("Windows returned an invalid signing certificate.", exception);
        }
    }

    private static byte[] CopyBlob(
        CryptDataBlob blob,
        int minimumBytes,
        int maximumBytes,
        string failureMessage)
    {
        if (blob.Data == IntPtr.Zero
            || blob.DataLength < minimumBytes
            || blob.DataLength > maximumBytes)
            throw new InvalidDataException(failureMessage);
        var value = new byte[checked((int)blob.DataLength)];
        Marshal.Copy(blob.Data, value, 0, value.Length);
        return value;
    }

    private static void RequireSha256(CryptAlgorithmIdentifier algorithm, string failureMessage)
    {
        if (!ReadOid(algorithm.ObjectId).Equals(Sha256Oid, StringComparison.Ordinal))
            throw new InvalidDataException(failureMessage);
    }

    private static string ReadOid(IntPtr value)
    {
        if (value == IntPtr.Zero)
            return string.Empty;
        var text = Marshal.PtrToStringAnsi(value) ?? string.Empty;
        return text.Length <= 128 ? text : string.Empty;
    }

    private static DateTimeOffset ReadTimestamp(NativeFileTime value)
    {
        var raw = ((ulong)value.High << 32) | value.Low;
        if (raw > long.MaxValue)
            throw new InvalidDataException("The RFC 3161 generation time is invalid.");
        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc((long)raw));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("The RFC 3161 generation time is invalid.", exception);
        }
    }

    private static void RequireExactDerSequence(ReadOnlySpan<byte> value, string failureMessage)
    {
        if (ReadDerSequenceLength(value, failureMessage) != value.Length)
            throw new InvalidDataException(failureMessage);
    }

    private static int ReadDerSequenceLength(ReadOnlySpan<byte> value, string failureMessage)
    {
        if (value.Length < 2 || value[0] != 0x30)
            throw new InvalidDataException(failureMessage);
        var firstLength = value[1];
        var headerLength = 2;
        ulong contentLength;
        if ((firstLength & 0x80) == 0)
        {
            contentLength = firstLength;
        }
        else
        {
            var lengthBytes = firstLength & 0x7f;
            if (lengthBytes is 0 or > 4 || value.Length < 2 + lengthBytes
                || value[2] == 0)
                throw new InvalidDataException(failureMessage);
            headerLength += lengthBytes;
            contentLength = 0;
            for (var index = 0; index < lengthBytes; index++)
                contentLength = (contentLength << 8) | value[2 + index];
            if (contentLength < 128)
                throw new InvalidDataException(failureMessage);
        }
        var totalLength = checked(contentLength + (ulong)headerLength);
        if (totalLength > (ulong)value.Length || totalLength > int.MaxValue)
            throw new InvalidDataException(failureMessage);
        return (int)totalLength;
    }

    private static byte[] ReadSinglePkcs7Signature(FileStream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (stream.Length < 256)
            throw new InvalidDataException(
                "The executable is too small to contain an Authenticode signature.");
        stream.Position = 0;
        if (reader.ReadUInt16() != 0x5a4d)
            throw new InvalidDataException("The signed file is not a PE executable.");
        stream.Position = 0x3c;
        var peOffset = (long)reader.ReadUInt32();
        if (peOffset < 64 || peOffset > stream.Length - 24)
            throw new InvalidDataException("The PE header offset is invalid.");
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550)
            throw new InvalidDataException("The PE signature is invalid.");
        _ = reader.ReadUInt16();
        var sectionCount = reader.ReadUInt16();
        if (sectionCount is 0 or > 96)
            throw new InvalidDataException("The PE section count is invalid.");
        stream.Position = peOffset + 20;
        var optionalHeaderSize = reader.ReadUInt16();
        if (optionalHeaderSize is < 104 or > 4096)
            throw new InvalidDataException("The PE optional header size is invalid.");
        var optionalHeaderOffset = peOffset + 24;
        var optionalHeaderEnd = checked(optionalHeaderOffset + optionalHeaderSize);
        if (optionalHeaderEnd > stream.Length)
            throw new InvalidDataException("The PE optional header is truncated.");

        stream.Position = optionalHeaderOffset;
        var magic = reader.ReadUInt16();
        var dataDirectoryOffset = optionalHeaderOffset + (magic switch
        {
            0x10b => 96,
            0x20b => 112,
            _ => throw new InvalidDataException("The PE optional header format is unsupported.")
        });
        var numberOfDirectoriesOffset = optionalHeaderOffset + (magic == 0x10b ? 92 : 108);
        if (numberOfDirectoriesOffset + 4 > optionalHeaderEnd)
            throw new InvalidDataException("The PE data-directory count is missing.");
        stream.Position = numberOfDirectoriesOffset;
        if (reader.ReadUInt32() < 5)
            throw new InvalidDataException("The PE security directory is missing.");
        var securityEntryOffset = dataDirectoryOffset + 4 * 8L;
        if (securityEntryOffset + 8 > optionalHeaderEnd)
            throw new InvalidDataException("The PE security directory is truncated.");

        stream.Position = optionalHeaderOffset + 60;
        var sizeOfHeaders = (long)reader.ReadUInt32();
        var sectionTableOffset = optionalHeaderEnd;
        var sectionTableEnd = checked(sectionTableOffset + sectionCount * 40L);
        if (sizeOfHeaders <= 0 || sizeOfHeaders > stream.Length
            || sectionTableEnd > sizeOfHeaders || sectionTableEnd > stream.Length)
            throw new InvalidDataException("The PE headers or section table are invalid.");

        stream.Position = securityEntryOffset;
        var certificateOffset = (long)reader.ReadUInt32();
        var certificateTableSize = (long)reader.ReadUInt32();
        if (certificateOffset <= 0 || (certificateOffset & 7) != 0
            || certificateTableSize < 9 || certificateTableSize > MaximumCertificateTableBytes
            || checked(certificateOffset + certificateTableSize) != stream.Length
            || certificateOffset < sizeOfHeaders)
            throw new InvalidDataException(
                "The PE certificate table is invalid or is not one final aligned file region.");

        var maximumRawEnd = sizeOfHeaders;
        for (var index = 0; index < sectionCount; index++)
        {
            var section = sectionTableOffset + index * 40L;
            stream.Position = section + 16;
            var rawSize = (ulong)reader.ReadUInt32();
            var rawOffset = (ulong)reader.ReadUInt32();
            if (rawSize == 0)
                continue;
            if (rawOffset < (ulong)sizeOfHeaders)
                throw new InvalidDataException("A PE section overlaps the image headers.");
            var rawEnd = checked(rawOffset + rawSize);
            if (rawEnd > (ulong)certificateOffset)
                throw new InvalidDataException("A PE section overlaps the Authenticode certificate table.");
            maximumRawEnd = Math.Max(maximumRawEnd, checked((long)rawEnd));
        }
        if (certificateOffset < maximumRawEnd)
            throw new InvalidDataException("The Authenticode certificate table overlaps PE image data.");

        stream.Position = certificateOffset;
        var certificateLength = (ulong)reader.ReadUInt32();
        var revision = reader.ReadUInt16();
        var certificateType = reader.ReadUInt16();
        var alignedLength = checked((certificateLength + 7UL) & ~7UL);
        if (certificateLength < 9 || certificateLength > MaximumCertificateTableBytes
            || alignedLength != (ulong)certificateTableSize
            || revision != 0x0200 || certificateType != 0x0002)
            throw new InvalidDataException(
                "The PE file does not contain exactly one aligned PKCS#7 Authenticode record.");

        var payloadLength = checked((int)certificateLength - 8);
        var payload = reader.ReadBytes(payloadLength);
        if (payload.Length != payloadLength)
            throw new InvalidDataException("The PE Authenticode signature is truncated.");
        var paddingLength = checked((int)((ulong)certificateTableSize - certificateLength));
        var recordPadding = reader.ReadBytes(paddingLength);
        var derLength = ReadDerSequenceLength(payload,
            "The Authenticode PKCS#7 value is not one DER ContentInfo with alignment padding.");
        var internalPaddingLength = payload.Length - derLength;
        var requiredPaddingLength = (8 - ((8 + derLength) & 7)) & 7;
        if (internalPaddingLength + paddingLength != requiredPaddingLength
            || payload.AsSpan(derLength).ContainsAnyExcept((byte)0)
            || recordPadding.Any(value => value != 0)
            || stream.Position != stream.Length)
            throw new InvalidDataException("The PE Authenticode record has invalid trailing padding.");
        return payload[..derLength];
    }

    private static void RequireEku(X509Certificate2 certificate, string requiredOid, string purpose)
    {
        var extensions = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (extensions.Length == 0
            || !extensions.SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>())
                .Any(oid => oid.Value == requiredOid))
            throw new InvalidDataException($"The signature certificate lacks the {purpose} EKU.");
    }

    private static bool RawEquals(X509Certificate2 left, X509Certificate2 right) =>
        left.RawData.Length == right.RawData.Length
        && CryptographicOperations.FixedTimeEquals(left.RawData, right.RawData);

    private static InvalidDataException NativeFailure(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return new InvalidDataException(message, new Win32Exception(error));
    }

    private sealed class QueriedSignedMessage : IDisposable
    {
        private GCHandle _encodedPin;

        internal QueriedSignedMessage(byte[] encoded)
        {
            ArgumentNullException.ThrowIfNull(encoded);
            if (encoded.Length is 0 or > MaximumCertificateTableBytes)
                throw new InvalidDataException("The signed CMS message has an invalid size.");
            _encodedPin = GCHandle.Alloc(encoded, GCHandleType.Pinned);
            try
            {
                var blob = new CryptDataBlob
                {
                    DataLength = checked((uint)encoded.Length),
                    Data = _encodedPin.AddrOfPinnedObject()
                };
                if (!CryptQueryObject(
                        QueryObjectBlob,
                        ref blob,
                        QueryContentFlagPkcs7Signed | QueryContentFlagPkcs7SignedEmbed,
                        QueryFormatFlagBinary,
                        0,
                        out var encoding,
                        out var contentType,
                        out var formatType,
                        out var store,
                        out var message,
                        IntPtr.Zero))
                    throw NativeFailure("Windows could not decode the signed CMS message.");
                Store = store;
                Message = message;
                if ((encoding & MessageEncoding) != MessageEncoding
                    || contentType is not QueryContentPkcs7Signed and not QueryContentPkcs7SignedEmbed
                    || formatType != QueryFormatBinary
                    || Store == IntPtr.Zero || Message == IntPtr.Zero)
                    throw new InvalidDataException("Windows decoded an unsupported signed-message format.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal IntPtr Store { get; private set; }
        internal IntPtr Message { get; private set; }

        public void Dispose()
        {
            if (Message != IntPtr.Zero)
            {
                _ = CryptMsgClose(Message);
                Message = IntPtr.Zero;
            }
            if (Store != IntPtr.Zero)
            {
                _ = CertCloseStore(Store, 0);
                Store = IntPtr.Zero;
            }
            if (_encodedPin.IsAllocated)
                _encodedPin.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructureSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustSignatureSettings
    {
        public uint StructureSize;
        public uint Index;
        public uint Flags;
        public uint SecondarySignatureCount;
        public uint VerifiedSignatureIndex;
        public IntPtr CryptoPolicy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificateHeader
    {
        public uint StructureSize;
        public IntPtr CertificateContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContextHeader
    {
        public uint EncodingType;
        public IntPtr Encoded;
        public uint EncodedLength;
        public IntPtr CertificateInfo;
        public IntPtr CertificateStore;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptDataBlob
    {
        public uint DataLength;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAlgorithmIdentifier
    {
        public IntPtr ObjectId;
        public CryptDataBlob Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAttributes
    {
        public uint Count;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAttribute
    {
        public IntPtr ObjectId;
        public uint ValueCount;
        public IntPtr Values;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CmsgSignerInfo
    {
        public uint Version;
        public CryptDataBlob Issuer;
        public CryptDataBlob SerialNumber;
        public CryptAlgorithmIdentifier HashAlgorithm;
        public CryptAlgorithmIdentifier HashEncryptionAlgorithm;
        public CryptDataBlob EncryptedHash;
        public CryptAttributes AuthenticatedAttributes;
        public CryptAttributes UnauthenticatedAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptTimestampContext
    {
        public uint EncodedLength;
        public IntPtr Encoded;
        public IntPtr TimestampInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptTimestampInfo
    {
        public uint Version;
        public IntPtr PolicyId;
        public CryptAlgorithmIdentifier HashAlgorithm;
        public CryptDataBlob HashedMessage;
        public CryptDataBlob SerialNumber;
        public NativeFileTime Time;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(
        IntPtr providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(
        IntPtr providerSigner,
        uint certificateIndex);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptQueryObject(
        uint objectType,
        ref CryptDataBlob objectValue,
        uint expectedContentTypeFlags,
        uint expectedFormatTypeFlags,
        uint flags,
        out uint messageAndCertificateEncodingType,
        out uint contentType,
        out uint formatType,
        out IntPtr certificateStore,
        out IntPtr message,
        IntPtr context);

    [DllImport("crypt32.dll", EntryPoint = "CryptMsgGetParam", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptMsgGetParamUInt32(
        IntPtr message,
        uint parameterType,
        uint index,
        out uint data,
        ref uint dataSize);

    [DllImport("crypt32.dll", EntryPoint = "CryptMsgGetParam", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptMsgGetParamBuffer(
        IntPtr message,
        uint parameterType,
        uint index,
        IntPtr data,
        ref uint dataSize);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptMsgControl(
        IntPtr message,
        uint flags,
        uint controlType,
        IntPtr controlParameter);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptVerifyTimeStampSignature(
        byte[] timestampContentInfo,
        uint timestampContentInfoLength,
        byte[] data,
        uint dataLength,
        IntPtr additionalStore,
        out IntPtr timestampContext,
        out IntPtr timestampSigner,
        out IntPtr timestampStore);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern IntPtr CertEnumCertificatesInStore(
        IntPtr certificateStore,
        IntPtr previousCertificateContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertFreeCertificateContext(IntPtr certificateContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertCloseStore(IntPtr certificateStore, uint flags);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptMsgClose(IntPtr message);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    private static extern void CryptMemFree(IntPtr value);
}
