# Simple Opticon Device Installation

## Status and scope

This document describes the implemented replacement for invitation-based install
on a managed Windows device. New invitations use the `binary-v1` contract and
enter this path directly.

The goal is deliberately narrow:

> Accept a current Opticon invitation and leave the device enrolled, reachable
> through Tailscale, controllable through RustDesk, and running the Opticon
> Agent after reboot.

Invite acceptance is not an upgrade, migration, repair, rollback, source-build,
or command-center installation workflow. If Opticon was installed before, the
installer replaces its owned state without interpreting it.

## What the current installer does

The current target-device path is a convergence and recovery system rather than
a simple installer. In broad order it:

1. Downloads and decrypts the hosted invitation.
2. Interprets an invitation-controlled matrix of validation switches.
3. Finds or installs an approved .NET SDK.
4. Downloads a source archive, verifies a signed file allowlist, and builds the
   release locally.
5. Attests the local build and starts a second elevated Setup process.
6. Activates and later commits or rolls back source-build provenance.
7. Preflights an existing installation, its protected directories, and seven
   fixed scheduled-task names.
8. Disables tasks, stops processes, pins directory handles, changes ACLs, and
   removes the previous Opticon files and machine state.
9. Runs another aggregate preflight covering invitation shape, disk space,
   payloads, SDK, provenance, elevation, profile, protected storage, OpenSSH,
   dependency versions, and pending reboot state.
10. Creates protected storage, lock files, a machine transaction journal, and
    an Agent transaction journal.
11. Detects, verifies, repairs, upgrades, or conditionally replaces a pinned
    Tailscale installation.
12. Inspects the existing Tailscale identity, decides whether it can be reused,
    records that decision, joins the tailnet, verifies node identity and tags,
    and optionally advertises an exit route.
13. Prepares firewall isolation; then detects, verifies, repairs, upgrades, or
    replaces RustDesk; configures its service, password, profile, and listener;
    and verifies the exact firewall result.
14. Resolves an interactive user profile.
15. Transactionally installs and verifies a stable update Guardian and its
    scheduled tasks.
16. Installs or repairs Windows OpenSSH and its recovery configuration, possibly
    arranging a reboot continuation.
17. Recovers any previous Agent transaction, snapshots the old Agent files,
    task XML, configuration, and receipt, stages a candidate, swaps directories,
    writes configuration, and registers the Agent task.
18. Imports exact Task Scheduler XML, exports it again, checks an exact XML
    contract, tries a command-line fallback, validates that result against the
    same contract, and conditionally removes a partial task only after proving
    its ownership.
19. Starts the Agent, verifies its private listener, installs optional controller
    files and tasks, waits for enrollment confirmation, commits journals and
    provenance, and cleans up.
20. On a failure, attempts layered Agent, machine-state, task, and provenance
    rollback, which can itself fail and obscure the original error.

This behavior is spread across the source bootstrap, Setup window, preflight,
legacy removal, the large `InstallCoordinator`, and several persistence and
provenance helpers. The main coordinator alone is approximately 259 KB of
source.

## What the reported errors show

The latest logs demonstrate failure modes created by installer policy rather
than by the minimum product requirements.

### Invitation preflight blocked useful work

Setup 1.2.18 rejected the invitation because its coordinator endpoint did not
match the current canonical form. It did this only after the source archive had
been authenticated, the release built locally, and elevation completed.

The device does need a usable coordinator endpoint, but it does not need to
enforce the command center's current canonical formatting policy. Invitation
creation should emit a usable endpoint. The device should attempt enrollment and
report a direct connection error if the endpoint is unusable.

### Replacement cleanup succeeded but was disproportionately complex

Setup 1.2.21 removed the previous Opticon generation, but only after fixed-task
inspection, process-path validation, handle-bound directory traversal, and ACL
fallback behavior. The warning about eight directories refusing ACL replacement
is evidence that legacy cleanup is a substantial subsystem of its own.

Invite acceptance does not need to preserve or reason about old Opticon state.
It needs one deterministic reset of names and roots owned by Opticon.

### Exact task verification turned a successful fallback into a failure

The Agent task's XML import failed with `unable to switch the encoding`. The
fallback registration then created a task whose exported XML did not match the
installer's exact protected contract. Cleanup refused to remove that same-named
task because its ownership could not be proven. Rollback repeated the same
ownership proof and converted one task-registration problem into nested
`AggregateException` failures.

The Agent only needs to start as LocalSystem at boot, restart on failure, and
run now. It does not need XML import/export, exact XML equivalence, task
snapshots, or ownership inference. A long-running background Agent is also a
better fit for a Windows service than for Task Scheduler.

## Proposed product boundary

The invitation installer owns only these outcomes:

1. A verified, current invitation is consumed.
2. Tailscale is installed and joined using the invitation.
3. RustDesk is installed for unattended access and restricted to Tailscale.
4. A fresh Agent payload and configuration are installed.
5. One `OpticonAgent` Windows service is running and automatic.
6. The coordinator confirms enrollment.

Everything else is outside this workflow:

- The release is built and signed before publication, not on the target device.
- Command-center/controller installation is a separate installer.
- Guardian, transactional update, and OpenSSH recovery are not installed during
  invitation acceptance.
- Updates use a separate replace-and-restart operation after enrollment.
- Diagnostics and drift checks run after installation and do not block it.
- There is no preservation, migration, adoption, rollback, resume, or repair of
  an older Opticon generation.

## Published artifact design

Publish one signed, architecture-specific device bundle for each release. The
bundle contains:

- the self-contained Opticon Agent;
- a small elevated installer executable;
- pinned Tailscale and RustDesk installers, or immutable HTTPS URLs plus their
  SHA-256 hashes;
- one manifest containing the release version, architecture, file hashes, and
  dependency versions.

The manifest is signed by the existing Opticon release key. The hosted invite
identifies one manifest and bundle hash. There is no SDK discovery, SDK install,
NuGet restore, source extraction, local publish, build attestation, or source
provenance state on the target.

The web page still downloads one signed launcher. The launcher performs one UAC
elevation and runs the installation in that same visible process. It must not
handoff through multiple installer windows.

## Target-device algorithm

The implementation should be a straight-line workflow. Each step either
succeeds or stops with one error. There are no repair findings, warnings that
permit partial success, or rollback branches.

### 1. Verify the inputs

- Require Windows x64 or ARM64 and administrator elevation.
- Decrypt and verify the invitation signature.
- Require a non-empty invite ID, unexpired expiry, one-time secret, device name,
  Tailscale login URL/auth key, coordinator URL, Agent token, and RustDesk
  password.
- Download the declared device bundle.
- Verify the bundle hash and release signature.

These are the only preflight checks. Do not inspect disk space, reboot markers,
installed versions, old journals, old configuration, task XML, user profiles,
Guardian state, OpenSSH, or controller state.

Invitation schema compatibility is a server concern. The invitation service
must generate only the current schema. It should reject or regenerate stale
invitations before offering a launcher, rather than letting a device build and
elevate an installer that will reject them later.

### 2. Reset Opticon-owned state

Use a fixed list, not discovery:

- stop and delete the `OpticonAgent` service if present;
- end and delete all historical Opticon/Taildesk scheduled-task names;
- stop executables running from the exact Opticon install root;
- remove the exact Opticon install and machine-data roots;
- recreate the two roots with SYSTEM and Administrators access.

`not found` is success. Do not query task XML, compare versions, read old
configuration, snapshot anything, prove ownership from old metadata, or restore
anything on failure.

The only deletion guard is that the two roots are compile-time fixed absolute
paths and the root itself is not a reparse point. Never accept a deletion path
from the invitation or old state. If a fixed root cannot be removed, stop with a
single `Could not reset Opticon-owned files` error and the exact Windows error.

Tailscale and RustDesk are dependencies, not Opticon state, and are handled by
their installers in the next steps. Do not include them in Opticon rollback.

### 3. Install and join Tailscale

- Run the pinned Tailscale MSI unconditionally in quiet, no-restart mode.
- Treat MSI success and `3010` as success; any other exit code is failure.
- Run `tailscale logout` and ignore only the documented not-logged-in result.
- Run one `tailscale up` with the invitation's login server, auth key, hostname,
  tags/role inputs, and optional exit-node advertisement.
- Run `tailscale ip -4` and require one `100.64.0.0/10` address.

Do not detect whether Tailscale is Opticon-managed, compare installed versions,
look up product codes, preserve an old identity, resume a previous enrollment,
or ask a second authorization question. Accepting UAC for the invitation is the
authorization to replace the device's current Tailscale identity.

The published Tailscale package must support same-version repair and major
upgrade through a stable MSI upgrade code. Package authoring, not target-side
registry heuristics, owns dependency replacement.

### 4. Install and configure RustDesk

- Run the pinned RustDesk MSI unconditionally in quiet, no-restart mode.
- Ensure its service exists.
- Set the invitation password.
- Apply the small fixed RustDesk configuration required for direct-IP access.
- Set the service to automatic and start it.
- Replace fixed Windows Firewall rules that allow RustDesk only from the
  Tailscale address range and block other inbound access.
- Require the RustDesk service to be running.

Do not inspect Opticon component bookkeeping, retain old RustDesk configuration,
compare versions, or build a containment/rollback branch. A failed RustDesk step
is an install failure with its command and exit code.

### 5. Install the Agent as one Windows service

- Copy the verified Agent payload directly to the fixed install root.
- Write a completely new `agent.json` from the current invitation and the
  Tailscale IPv4 address. Protect secrets with machine-scope DPAPI.
- Add the fixed Agent firewall rule on TCP 45831 from the coordinator's
  Tailscale address or the narrow policy range chosen by the product.
- Register `OpticonAgent` as an automatic LocalSystem Windows service whose
  binary is the fixed `Taildesk.Agent.exe` path.
- Configure ordinary Windows service recovery to restart after failure.
- Start the service.
- Poll `/healthz` for at most 30 seconds.

The Agent uses a native Windows-service entrypoint. Remove Agent
Task Scheduler registration entirely. There is no XML generation, XML import,
XML export, exact task contract, fallback task, task snapshot, candidate
directory, rollback directory, or Agent transaction journal.

### 6. Confirm enrollment and finish

- Let the Agent post the one-time invite secret and Tailscale identity to the
  coordinator.
- Wait up to 60 seconds for coordinator acceptance.
- On acceptance, clear the pending invite secret from Agent configuration and
  delete the local encrypted invitation/bootstrap directory.
- Display `Connected. This machine is ready.` and exit 0.

If confirmation times out, return one clear failure while leaving Tailscale,
RustDesk, and the Agent running. The Agent may continue retrying enrollment, but
the installer must not call that partial state a successful installation.

## Straight-line control flow

```text
verify invite and signed binary bundle
            |
            v
delete fixed Opticon service/tasks/roots
            |
            v
install Tailscale -> logout -> join -> get IP
            |
            v
install/configure RustDesk -> start service
            |
            v
copy Agent -> write fresh config -> create service
            |
            v
replace firewall rules -> start Agent -> health check
            |
            v
wait for enrollment confirmation -> delete invite -> success
```

There is one failure path: log the current step, executable/API, exit code, and
bounded stderr or Windows exception; then stop. Do not catch a failure merely to
attempt a restoration of an unknown previous generation.

## Required invariants

The simplification removes compatibility behavior, not the few security
properties that define the product boundary:

- Only a signed, unexpired, one-time invitation can install.
- Only a signed and hash-matched binary bundle can run.
- Installation requires one Windows administrator approval.
- Destructive cleanup is limited to compile-time fixed Opticon roots and names.
- Agent and RustDesk listeners are restricted to the private Tailscale network.
- Agent secrets are machine-protected and are never written to logs.
- Success means Tailscale connected, RustDesk service running, Agent healthy,
  and enrollment accepted. There is no warning-based success state.

The existing per-invitation `ClientInstallValidationPolicy` should not control
target security behavior. Delete that matrix from the device installer. Release
verification and the invariants above are always on.

## Explicitly deleted behavior

The replacement should remove these concepts from the invitation path instead
of leaving them disabled behind flags:

- local SDK installation and source builds;
- source-build attestation and provenance promotion/rollback;
- aggregate `SetupPreflight` and planned repairs;
- `LegacyOpticonRemoval` ownership proofs and ACL sealing;
- machine and Agent installation journals;
- previous-version and previous-configuration snapshots;
- exact Agent Task Scheduler XML and its fallback validation;
- Agent rollback and transaction recovery;
- Guardian installation and Guardian tasks;
- OpenSSH capability installation and reboot continuation;
- interactive-user profile discovery;
- controller payload, shortcut, route, and UI task installation;
- component ownership bookkeeping;
- deferred-repair warnings and partial-success exit codes.

Some of these features may remain valuable as separate post-enrollment products.
They should not be reachable from accepting an invitation.

## Failure and retry semantics

Retry means run the same current invitation installer again while the invitation
is still valid and unconsumed. The installer starts from step 1 and resets its
fixed state again. It does not read a journal or resume at a recorded phase.

If the coordinator already committed the invite but the final response was
lost, its enrollment endpoint should return the same accepted result for the
same invite ID, secret, and Tailscale node identity. That small server-side
idempotency rule is the only resume behavior required.

If the invitation is consumed by a different identity or expired, the command
center creates a new invitation. The device installer does not recover it.

## Logging and user experience

Use one window and seven user-facing messages:

1. `Verifying invitation...`
2. `Resetting Opticon...`
3. `Connecting private network...`
4. `Installing remote access...`
5. `Installing Opticon Agent...`
6. `Confirming enrollment...`
7. `Connected. This machine is ready.`

The protected log records start/end time and the command result for each step.
It must redact invite keys, auth keys, Agent tokens, RustDesk passwords, and URL
fragments. Avoid nested exception wrapping. The final error should name the one
failed step and preserve the original process or Windows error.

## Acceptance tests

Test the workflow on real Windows VMs, not only mocked component checks.

1. Clean supported Windows installation.
2. Machine containing each currently released Opticon version.
3. Machine containing a half-deleted Opticon root and every historical task
   name.
4. Machine with current Tailscale connected to another tailnet.
5. Machine with an older Tailscale and RustDesk version.
6. Machine rebooted after successful installation; all three services return.
7. Installer killed during each numbered step, then rerun from the beginning.
8. Invalid invite signature, expired invite, wrong bundle hash, dependency MSI
   failure, Tailscale join failure, RustDesk failure, Agent health failure, and
   coordinator timeout, each producing one direct error.

The pass condition is observable product state, not exact XML, journal phases,
provenance records, or preservation of an earlier generation.

## Uninstaller

The device bundle also installs `Uninstall-Opticon.exe` and registers it in
Windows Apps & Features. It relaunches from a temporary elevated location, then
removes the Agent service, all historical Opticon tasks and firewall rules,
fixed Opticon program/data roots, current-user Opticon data, RustDesk, and
Tailscale. Its own temporary executable is scheduled for deletion at reboot.

## Implemented release path

The deploy button builds and publishes two signed role-labelled x64 bundles.
Each contains only Setup, the self-contained Agent, the uninstaller, and the
signed inner manifest. A separately signed bootstrap is the invitation's one
download. The gateway release protocol is version 4, and its live schema-1
manifest requires exactly those two bundles and bootstrap for the current
release. The source-only publisher remains only as a legacy maintenance tool;
it is not reachable from new invitation creation or the deploy button.
