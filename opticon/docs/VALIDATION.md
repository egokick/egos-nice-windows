# Validation record

Source review date: 2026-08-03

## Passed in the source workspace

- Tree-sitter syntax parsing: 31 C# files and 2 PowerShell files
- XML parsing: WPF XAML, project files, and shared build properties
- WPF event-handler mapping checks
- Tailscale policy JSON parsing, role assertions, and embedded/file parity
- Solution-to-project reference checks
- GitHub Actions workflow YAML parsing
- Static review of enrollment/cancellation serialization, Tailscale-only addressing, role-tag isolation, file path/reparse controls, credential rotation, and device revocation

## Must run on Windows before release

The source workspace did not contain the .NET SDK, Windows, Tailscale, or RustDesk, so it could not produce or execute Windows binaries here. The supplied Windows build script and CI workflow perform the real .NET build and foundational self-tests. A release owner must also complete the end-to-end Windows checklist in `SECURITY.md`, including a Windows Home target, real remote control, reboot/sign-in behavior, transfers, role changes, removal, and exit routing.

