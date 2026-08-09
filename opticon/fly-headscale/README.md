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
sent to Fly; browser JavaScript downloads and SHA-256-verifies the exact signed
bootstrap through CloudFront, while displaying the complete bootstrap/source
hashes and pinned SDK version. The bootstrap self-verifies its signed schema-5
invitation, downloads the exact RSA-PSS-authenticated source archive, and builds
it locally with .NET SDK 10.0.302 and runtime 10.0.10. Hosted ciphertext expires after
14 days by default and is removed on manual expiration or successful enrollment.
The command center can extend an active invitation without changing its URL;
it rotates the one-use Headscale key and replaces the signed encrypted envelope.
If the browser cannot perform the required WebCrypto verification, the page
fails closed and asks the recipient to retry with current Edge or Chrome. No
unsigned command starter, execution-policy bypass, or legacy binary-bundle
invitation is generated. Schemas 2-4 remain status/cancellation history only.

Large Opticon ZIPs and signed bootstraps live in a private S3 bucket and are
served through CloudFront. Fly retains only the small public manifest on its
persistent volume. Before deploying, configure the two public production trust
identities; the gateway intentionally refuses to start without exact, distinct
source-release and product-signing pins (neither may be the invitation key):

```powershell
fly secrets set `
  OPTICON_SOURCE_RELEASE_KEY_ID=<40-HEX-OFFLINE-SOURCE-CERT-THUMBPRINT> `
  OPTICON_PRODUCT_SIGNER_THUMBPRINT=<40-HEX-PUBLIC-CODE-SIGNING-THUMBPRINT>
fly deploy
```

Provision the private distribution once, then publish from a clean committed
checkout with explicit production identities, an HTTPS RFC3161 service, and the
fixed Windows SDK signer:

```powershell
..\infrastructure\aws\Provision-OpticonReleaseDistribution.ps1
.\scripts\Publish-OpticonBundles.ps1 `
  -SourceReleaseCertificateThumbprint <40-HEX-OFFLINE-SOURCE-CERT-THUMBPRINT> `
  -ProductCertificateThumbprint <40-HEX-PUBLIC-CODE-SIGNING-THUMBPRINT> `
  -Rfc3161TimestampUrl https://timestamp.digicert.com `
  -SignToolPath 'C:\Program Files (x86)\Windows Kits\10\bin\<SDK>\x64\signtool.exe'
```

The publisher uses the authenticated operator AWS CLI only: it chooses the next
unpublished patch version, signs release/source manifests with the separate
offline RSA-PSS key, Authenticode-signs every executable with the publicly
trusted product certificate and a bound RFC3161 timestamp, uploads
immutable S3 objects with SHA-256 checksums, verifies every object with
CloudFront HEAD/range requests, and full-stream hashes every published object. It
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
