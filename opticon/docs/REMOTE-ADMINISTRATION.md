# SSH access and fail-safe remote updates

Opticon protects three recovery lifelines on a managed device: Tailscale reachability, RustDesk direct remote desktop, and Guardian-owned just-in-time SSH. Tailscale transports both interactive channels, while RustDesk and SSH retain separate process, protocol, and authentication boundaries. A target Agent/runtime update must not install, remove, reconfigure, stop, or restart those lifelines or change device identity, credentials, Headscale tags, routes, exit-node state, or policy.

## Open SSH

**Open SSH** is available only on the primary command center and only for an online Agent reached through its `100.64.0.0/10` address.

1. The command center creates a new Ed25519 key pair in a current-user-only temporary directory.
2. The authenticated Agent call is additionally restricted to the configured primary coordinator source IP.
3. Signed Setup--or the one-time legacy maintenance bootstrap--preflights Windows OpenSSH before connectivity or installed Opticon state changes. If Opticon installs the capability, it immediately disables the stock `sshd` service and broad firewall rule; a pre-existing OpenSSH installation is not reconfigured.
4. The stable Guardian, installed outside the versioned Agent directory, owns an isolated demand-start `sshd` process on TCP `45832`, bound to the target Tailscale IPv4 address. Windows Firewall admits only the coordinator's exact Tailscale IPv4 address, and Headscale policy admits only `tag:taildesk-hub`.
5. A dedicated `OpticonRemoteAdmin` local administrator account has a random unknown password. Password and keyboard-interactive SSH authentication are disabled. The account is enabled only while at least one lease exists.
6. The Agent atomically records bounded leases, revocation tombstones, and a termination generation. Every five seconds, the Guardian rewrites authorized keys with source-address and UTC expiry restrictions; revocation or expiry restarts its kill-on-close job so an already-authenticated shell closes even when another lease remains. Still-valid clients may reconnect. The maximum lease is eight hours and the normal UI lease is one hour.
7. The Agent returns its host public key through the already authenticated Agent channel. The command center pins it in a per-session `known_hosts` file and launches Windows `ssh.exe` with strict host-key checking, no forwarding, and only the ephemeral identity.
8. Before returning a session, Opticon runs a challenge-bound signed Guardian probe and requires the exact remote account, a primary high-integrity token, and the enabled Administrators group. Commands consequently run with full UAC-level administrator access without relying on a remote consent-dialog click; a filtered or insufficient token is refused.
9. When `ssh.exe` exits, the command center records revocation and destroys the private key. The Guardian independently expires leases and contains `sshd` plus every authenticated child in a kill-on-close Job Object. Any lease removal restarts that job and closes all existing shells; when no valid lease remains, the listener, firewall rule, account, and authorized-key file are closed.

OpenSSH capability installation can require Windows Update access, so it is an explicit signed Setup/bootstrap prerequisite rather than a lease-time action. Failure occurs before Tailscale, RustDesk, enrollment, or installed Opticon state is changed.

## Command-line and agent automation

The command-center installer adds `%PROGRAMFILES%\Taildesk\Admin\Cli` to the signed-in operator's user `PATH`. Start a new terminal or agent process after installation, and run it as the same Windows user that configured Opticon: Agent credentials are protected with DPAPI CurrentUser and are never accepted from command-line arguments.

```powershell
$inventory = opticon devices --json | ConvertFrom-Json
$inventory.devices | Format-Table id,name,hostName,tailscaleIp
$deviceId = '<exact reviewed device ID>'
opticon status $deviceId --json
opticon ssh $deviceId --powershell 'Get-ComputerInfo | Select-Object WindowsProductName,WindowsVersion'
Get-Content .\remote-task.ps1 -Raw | opticon ssh $deviceId --powershell -
opticon ssh $deviceId --command 'whoami /all'
opticon update $deviceId --yes --json
```

Selectors are exact device ID, Tailnet device ID, Tailscale IPv4 address, name, host name, or DNS name; ambiguous selectors are refused. Agents should discover and retain the device ID from `devices --json`. `devices`, `status`, and `update` keep versioned JSON on standard output and progress or diagnostics on standard error. Automated SSH writes the target's raw output to the inherited streams and returns `ssh.exe`'s exit code. Prefer `--powershell -` for generated or multiline scripts so local quoting cannot alter them; command transport is deliberately bounded.

Exit code `0` means success, `1` is an Opticon operational failure, `2` is invalid usage, and `130` is cancellation; an automated SSH command can also return its remote `ssh.exe` exit code. During an attached interactive SSH session, Ctrl+C is delivered to the remote console. Before attachment, or while an automated command is running, Ctrl+C cancels provisioning or execution and triggers lease revocation and local key cleanup.

`opticon update ... --yes` uses the same signed transactional Agent/Guardian path as the UI, including verified RustDesk recovery, last-known-good retention, repeated health proof, and rollback on failure or missed commit. The CLI refuses the one-time higher-risk bootstrap required by legacy Agents; perform that transition only in the UI with an attended, verified RustDesk session.

## Target Opticon Agent updates

The **Update Opticon** action updates only the target Agent. It never upgrades Admin, an existing update Guardian, Tailscale, RustDesk, Windows OpenSSH, or controller tools. Controller tools can be updated separately when a user is present.

The release path is deliberately transactional:

1. The command center selects the newest role- and architecture-matched immutable bundle from the HTTPS release manifest.
2. The current Agent acquires the protected update-transaction lease, then downloads with bounded resumable retries while it remains online. A boot/manual Guardian cannot interleave with the Downloading -> Verifying -> Ready journal sequence.
3. Before staging, the Agent requires a healthy RustDesk listener and enough free disk space for the candidate plus a local rollback copy.
4. The Agent verifies the outer size and SHA-256, the RSA-PSS-signed inner release manifest, every declared Agent file hash, the pinned Authenticode publisher, the exact role, architecture, protocol, and binary-reported version. Unsafe archive paths, undeclared Agent files, downgrades, and incompatible guardians are rejected.
5. Activation waits for any prior Guardian invocation to become idle, writes an ACL-protected ActivationScheduled journal, then retries the stable SYSTEM Guardian task. A distinct signed SYSTEM watchdog checks nonterminal transactions every minute, closing the producer-crash gap between that durable write and the explicit task start. It uses a short, non-failing transaction-lock attempt and never performs terminal boot health; the no-argument ONSTART Guardian retains that responsibility. A definitive explicit task-start failure durably restores Ready with no activation deadlines.
6. The Guardian holds the same OS-released transaction lease, reconfirms the RustDesk and Tailscale lifelines, retains the installed Agent as last-known-good, swaps only the Agent directory, and starts the existing Taildesk Agent task. It refuses to save or enter a destructive transition if a different durable operation superseded the one it loaded.
7. The command center requires repeated authenticated health samples with the exact new version, Tailscale address, Agent API, RustDesk listener, and the selected device's Tailnet identity when one is registered. If SSH TCP 45832 was listening immediately before activation, the replacement must also report SSH ready and pass a fresh TCP 45832 probe.
8. Only then does the command center send an idempotent commit request. A lost commit HTTP response is treated as indeterminate: Opticon polls the Guardian's durable terminal state instead of reporting a false failure.
9. A crash, missing listener, version mismatch, lost coordinator, missed commit deadline, or power loss before durable commit causes the Guardian to restore and restart the last-known-good Agent. The previous release remains on the device after a successful commit for recovery.

Reboot behavior is conservative: downloading or staged releases are not activated automatically; any uncommitted activation is rolled back on Guardian startup; only a durably committed release remains active. Committed boot verification allows 6 minutes 30 seconds for the Agent's bounded five-minute Tailscale bind wait before considering rollback.

## Existing devices and rollout discipline

An older Agent has no SSH or update endpoint, and Opticon intentionally has no arbitrary remote-execution API. Its first upgrade therefore uses one explicit, signed maintenance action over the already-working RustDesk recovery session:

1. Select the legacy device and click **Update Opticon**. Opticon first selects the newest immutable bundle for that exact role and architecture.
2. The command center generates a fresh operation ID and shows the immutable HTTPS URL and SHA-256. If you cancel, it copies nothing and starts no maintenance.
3. Choose **Yes**. Opticon copies a size-, hash-, publisher-, device-ID-, Tailscale-address-, and operation-pinned PowerShell command, snapshots whether SSH TCP 45832 is listening, opens **Remote into**, and starts a bounded 30-minute authenticated watch for that exact operation. Keep this Opticon window open.
4. In the remote Windows session, open PowerShell, paste the command, and approve the single UAC prompt. The command downloads that exact archive, verifies its size and SHA-256, verifies the RSA-PSS-signed inner manifest, then requires exactly one root Setup declaration with the pinned publisher, size, and SHA-256 before elevation. It passes the fixed operation ID to `Taildesk.Setup.exe --maintenance`.
5. Setup strictly parses the operation, selected Tailnet device ID, and selected Tailscale IPv4. It re-verifies its exact signed-manifest declaration, ProductVersion, and pinned Authenticode signature, loads the existing enrolled `agent.json`, and does not re-enroll, rotate credentials, or touch Tailscale/RustDesk configuration.
6. Setup establishes Windows OpenSSH while the legacy Agent and RustDesk still work. If the Windows capability is not fully ready or a reboot is required, maintenance stops before writing an activation journal and can be run again safely.
7. Setup installs the signed stable Guardian only when it is absent and creates both its ONSTART recovery task and minute nonterminal watchdog before journaling activation. It never overwrites an existing Guardian. A same-version copy must exactly match the signed manifest-declared Guardian payload; a newer copy must be a signed single-file Guardian preserving the versioned fixed-mode contract. Otherwise maintenance refuses before changing the Agent.
8. Setup repackages only signed metadata and Agent payload files, re-verifies the candidate, waits for the shared update transaction to be idle, rechecks the installed Agent and both recovery lifelines, then schedules that exact operation. The maintenance flag exists only in the ACL-protected local journal and cannot be supplied through the Agent API.
9. For this transaction the Guardian skips only the legacy Agent's unavailable internal-health preflight. Installed-Agent signature/version checks, Tailscale/RustDesk/SSH lifelines, signed candidate verification, candidate health, rollback, and deadlines remain mandatory.
10. Setup requires three exact protected local health samples, but it never writes a commit request. It waits for an external terminal result.
11. The command center accepts only the copied operation and expected release. It requires three consecutive authenticated status samples with the exact Agent version, architecture, protocol, Tailscale IPv4, Tailnet device ID, live RustDesk report plus TCP 21118 probe, and--when SSH was snapshotted--SSH report plus TCP 45832 probe.
12. Only then does the command center send the idempotent commit and wait for the exact durable terminal state. If the command center closes, loses contact, sees a mismatch, misses the candidate window, or cannot finish sampling before the target deadline, it sends no commit and the Guardian rolls back by omission.

This maintenance path updates only the Opticon Agent. It does not hot-update Admin or an existing Guardian. After the one-time bootstrap, later Agent releases use the normal guarded API path.

Release practice for distant devices:

- Validate the signed bundle and forced rollback on a local Windows VM.
- Canary one nearby device and observe it through a reboot before touching distant devices.
- Update one device at a time; never start an unattended fleet-wide upgrade.
- Confirm RustDesk and, where available, SSH recovery before activation.
- Keep the prior signed release and the device's local last-known-good copy.
- Do not combine Agent, Tailscale, RustDesk, routing, credential, or policy changes in one remote transaction.
