OPTICON COMMAND CENTER
======================

This legacy filename is retained only for documentation links.

Extract the complete release, verify the Windows publisher on
Install-Opticon.exe, and open that signed executable. Do not run a loose .ps1
file and do not use ExecutionPolicy Bypass.

Install-Opticon.exe verifies the offline-signed package manifest and the exact
hash, size, version, and product signature of every executable before elevation.
Hosted device invitations then download and verify the exact source release and
build it locally with any stable .NET 10 SDK (10.*.*).
