OPTICON COMMAND CENTER
======================

1. Extract this entire ZIP on the Windows laptop that will be the command center.
2. Right-click Install-Opticon.ps1 and choose "Run with PowerShell".
   If Windows blocks scripts, open an Administrator PowerShell here and run:
       powershell -ExecutionPolicy Bypass -File .\Install-Opticon.ps1
3. Close the elevated installer, then open Opticon from the signed-in user's
   desktop or Start Menu. Opticon also starts at Windows sign-in and lives in
   the notification area.
4. Create a one-click invitation in the Invitations view only when its recipient
   is ready. It is personalized, single use, and expires in 15 minutes.
5. Select an enrolled machine and click Remote into. Opticon handles the private
   Tailscale address and authentication; RustDesk runs only as the session engine.

This build is preconfigured for the private Headscale service at:
    https://taildesk-egokick-control.fly.dev

The coordinator is part of the Opticon tray process. Keep this Windows user
signed in and Opticon running while a device accepts an invitation. Locking the
screen is fine. No router port forwarding is required.

Windows Home is supported through RustDesk over the private Tailscale mesh.
See README.md for architecture, security boundaries, and Fly deployment.
