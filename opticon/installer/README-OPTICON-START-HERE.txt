OPTICON COMMAND CENTER
======================

1. Extract the complete command-center ZIP into a new folder.
2. Confirm that Windows shows the expected Opticon publisher on
   Install-Opticon.exe. Do not continue through an Unknown Publisher warning.
3. Open Install-Opticon.exe. It verifies its own trusted, timestamped signature,
   the offline-signed package manifest, and every payload before asking Windows
   to install anything.
4. Open Opticon after installation, then create a hosted invitation only when
   its recipient is ready. The invitation is personalized, single use, and
   expires automatically.
5. The recipient opens the invitation page, downloads the exact signed bootstrap
   and hash-pinned source archive, and builds it with .NET SDK 10.0.302. Setup
   prompts clearly if the exact SDK/runtime is missing.

Never run a loose PowerShell installer, use ExecutionPolicy Bypass, or install a
package whose publisher cannot be validated. The only command-center entry point
in a release package is Install-Opticon.exe.

The private control service is:
    https://taildesk-egokick-control.fly.dev

No router port forwarding is required. See README-OPTICON.md and docs\SECURITY.md
for the trust boundaries and recovery procedure.
