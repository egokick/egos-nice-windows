TAILDESK COMMAND CENTER
=======================

1. Extract this entire ZIP on the Windows laptop that will stay on.
2. Right-click Install-CommandCenter.ps1 and choose "Run with PowerShell".
   If Windows blocks scripts, open an Administrator PowerShell in this folder and run:
       powershell -ExecutionPolicy Bypass -File .\Install-CommandCenter.ps1
3. Keep the laptop signed into your self-hosted Headscale login server. Do not sign in to Tailscale's hosted control plane.
4. Close the elevated installer, then open the new Taildesk shortcut on the signed-in
   user's desktop. This avoids running the command center as the UAC administrator.
5. In Settings, follow docs\HEADSCALE-SETUP.md to enter your Headscale API
   address, Headscale user ID, and locally controlled API key. Install the
   included config\headscale-policy.hujson on your Headscale server.
6. Create an invitation in the Invitations view and send that ZIP to the target.

The coordinator runs in the Taildesk notification-area process. Keep this Windows user
signed in and Taildesk running while a target accepts an invitation. Locking the screen
is fine. The coordinator is not available before sign-in or after sign-out.

Windows Home is supported. Taildesk uses RustDesk, not Windows RDP hosting.
No router port forwarding is required.
