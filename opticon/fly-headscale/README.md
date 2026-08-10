# Opticon Fly control plane

This deployment provides Opticon's public Headscale coordination endpoint,
private DERP relay, STUN service, pinned dependency mirror, authenticated
source releases, and short-lived encrypted invitation pages. The laptop remains
the command center. Fly never receives an invitation's URL-fragment key or
plaintext RustDesk, agent, controller, or Headscale enrollment credentials.

- Control/API/DERP: `https://taildesk-egokick-control.fly.dev` on TCP 443
- STUN: UDP 3478 on the app's dedicated IPv4 address
- State: the `taildesk_data` Fly volume mounted at `/var/lib/headscale`
- Internet routing: disabled unless the administrator explicitly enables an enrolled Opticon exit node

`/opticon/i/<random-id>#<key>` is the recipient link. Only the random ID is
sent to Fly. Clicking the download button revalidates that invitation and its
exact published source record, then redirects to a private S3 GET URL that
expires after 30 minutes. The browser performs a normal streamed download and
never buffers the archive in JavaScript. The recipient extracts the archive and runs its fixed signed
`OpticonSourceLauncher.exe`; the invitation page serves those exact signed
bytes under an invitation-bound filename. The user only downloads and opens
that installer: it recovers the fragment locally, requests the 30-minute S3
source URL itself, and verifies the signed schema-6 invitation, its
own byte identity, the exact RSA-PSS-authenticated source archive, and then
builds locally with .NET SDK 10.0.302 and runtime 10.0.10. Hosted ciphertext expires after
14 days by default and is removed on manual expiration or successful enrollment.
The command center can extend an active invitation without changing its URL;
it rotates the one-use Headscale key and replaces the signed encrypted envelope.
No unsigned command starter, execution-policy bypass, or legacy binary-bundle
invitation is generated. Schemas 2-4 remain status/cancellation history only.

Each release has exactly one immutable S3 object,
`opticon-source-<version>.zip`. It contains the signed source manifest, locked
build inputs, and the fixed signed local launcher; no release-specific binary
bundle or bootstrap is published separately. Fly retains only the small public manifest on its
persistent volume. Before deploying, configure the two public production trust
identities and the dedicated S3 presigner credential. The IAM identity must
have only `s3:GetObject` on `arn:aws:s3:::opticon-053663732727/opticon/releases/*`;
the gateway intentionally refuses to start without exact, distinct source-release
and product-signing pins (neither may be the invitation key) or without a valid
presigner configuration:

```powershell
fly secrets set `
  OPTICON_SOURCE_RELEASE_KEY_ID=<40-HEX-OFFLINE-SOURCE-CERT-THUMBPRINT> `
  OPTICON_PRODUCT_SIGNER_THUMBPRINT=<40-HEX-PUBLIC-CODE-SIGNING-THUMBPRINT> `
  OPTICON_S3_ACCESS_KEY_ID=<DEDICATED-GET-ONLY-ACCESS-KEY> `
  OPTICON_S3_SECRET_ACCESS_KEY=<DEDICATED-GET-ONLY-SECRET>
fly deploy
```

The non-secret bucket and region are pinned in `fly.toml`. Presigner credentials
are stripped from the Headscale child environment and never appear in the page,
invitation, manifest, logs, or redirect path before the short-lived S3 URL is minted.

Provision the private distribution once, then publish from a clean committed
checkout with explicit production identities, an HTTPS RFC3161 service, and the
fixed Windows SDK signer:

```powershell
..\infrastructure\aws\Provision-OpticonReleaseDistribution.ps1
.\scripts\Publish-OpticonSourceRelease.ps1 `
  -SourceReleaseCertificateThumbprint <40-HEX-OFFLINE-SOURCE-CERT-THUMBPRINT> `
  -ProductCertificateThumbprint <40-HEX-PUBLIC-CODE-SIGNING-THUMBPRINT> `
  -Rfc3161TimestampUrl https://timestamp.digicert.com `
  -SignToolPath 'C:\Program Files (x86)\Windows Kits\10\bin\<SDK>\x64\signtool.exe'
```

The publisher uses the authenticated operator AWS CLI only: it chooses the next
unpublished source version, signs the source manifest with the separate offline
RSA-PSS key, Authenticode-signs the fixed launcher with the publicly trusted
product certificate and a bound RFC3161 timestamp, uploads the one immutable
source ZIP with a SHA-256 checksum, and verifies it with CloudFront HEAD/range
requests and a full-stream hash. It
then sends only the small manifest to an authenticated atomic gateway endpoint;
ordinary releases do not build an image or roll a Fly machine. The endpoint is
signed with the same DPAPI-protected HMAC credential already used by the local
command center. The old HMAC chunk route remains only as a migration fallback.

For an owner-controlled fleet that deliberately accepts the initial Windows
**Unknown Publisher** prompt, pass `-SigningProfile OwnerManaged` with separate
self-signed product and source-release certificates. Set the gateway secret
`OPTICON_SIGNING_PROFILE=OwnerManaged`. Hash, signer, source-manifest, timestamp,
and trust-domain checks remain mandatory; only the public Windows chain requirement
is relaxed.

The Fly API token is needed only for operator-initiated gateway deployments. It
must never be copied into this directory, the container image, Opticon
configuration, or an invitation.
