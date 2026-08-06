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
sent to Fly; browser JavaScript uses the fragment key locally to create a tiny
starter. The starter downloads and verifies a reusable bundle, while Setup
decrypts and verifies the signed invitation. Hosted ciphertext expires after
14 days by default and is removed on manual expiration or successful enrollment.
The command center can extend an active invitation without changing its URL;
it rotates the one-use Headscale key and replaces the signed encrypted envelope.

Large Opticon ZIPs live on the persistent volume rather than in the container
image. Rebuild/deploy them from this directory with:

```powershell
.\scripts\Build-OpticonBundles.ps1
flyctl deploy --remote-only --config fly.toml
.\scripts\Publish-OpticonBundles.ps1
```

The publisher reads the existing DPAPI-protected Opticon gateway key from the
local admin configuration. Uploads use authenticated 4 MiB chunks and are
accepted only when filename, size, and SHA-256 match the deployed manifest.
The public manifest withholds each new Opticon release until its final verified
bundle exists on the persistent volume, so deploying metadata before uploading
cannot displace the last available release or advertise a partial package.

The Fly API token is read only from `C:\source\babelfish\.env` during an
operator-initiated deployment. It must never be copied into this directory,
the container image, Opticon configuration, or an invitation.