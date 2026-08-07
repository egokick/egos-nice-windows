# Opticon Fly control plane

This deployment provides Opticon's public Headscale coordination endpoint,
private DERP relay, STUN service, pinned dependency mirror, reusable signed
role bundles, and short-lived encrypted invitation pages. The laptop remains
the command center. Fly never receives an invitation's URL-fragment key or
plaintext RustDesk, agent, controller, or Headscale enrollment credentials.

- Control/API/DERP: `https://taildesk-egokick-control.fly.dev` on TCP 443
- STUN: UDP 3478 on the app's dedicated IPv4 address
- State: the `taildesk_data` Fly volume mounted at `/var/lib/headscale`
- Internet routing: disabled unless the administrator explicitly enables an enrolled Opticon exit node

`/opticon/i/<random-id>#<key>` is the recipient link. Only the random ID is
sent to Fly; browser JavaScript fetches the signed bootstrap through CloudFront
CORS and uses a blob URL to assign its invite-bearing local filename. The
bootstrap downloads and verifies a reusable bundle, while Setup
decrypts and verifies the signed invitation. Hosted ciphertext expires after
14 days by default and is removed on manual expiration or successful enrollment.
The command center can extend an active invitation without changing its URL;
it rotates the one-use Headscale key and replaces the signed encrypted envelope.

Large Opticon ZIPs and signed bootstraps live in a private S3 bucket and are
served through CloudFront. Fly retains the small public manifest on its
persistent volume plus legacy files as a migration fallback. Provision once,
deploy gateway changes when needed, then publish releases from this directory:

```powershell
..\infrastructure\aws\Provision-OpticonReleaseDistribution.ps1
.\scripts\Publish-OpticonBundles.ps1
```

The publisher uses the authenticated operator AWS CLI only: it chooses the next
unpublished patch version, signs both bundles and the bootstrap, uploads
immutable S3 objects with SHA-256 checksums, verifies every object with
CloudFront HEAD/range requests, and full-stream hashes the smaller bundle. It
then sends only the small manifest to an authenticated atomic gateway endpoint;
ordinary releases do not build an image or roll a Fly machine. The endpoint is
signed with the same DPAPI-protected HMAC credential already used by the local
command center. The old HMAC chunk route remains only as a migration fallback.

The Fly API token is needed only for operator-initiated gateway deployments. It
must never be copied into this directory, the container image, Opticon
configuration, or an invitation.
