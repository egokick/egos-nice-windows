# Opticon Rapid Development Testing Mode

This document defines a reusable workflow for closing difficult target-device development loops without repeatedly building and publishing production releases. It is a design and operations plan; testing mode must not be enabled by partially implementing only its trust-bypass portions.

## Purpose

Rapid development testing mode lets an explicitly authorized target device:

1. redeem a testing invitation;
2. pull a designated Opticon source branch and commit;
3. build and install a test-only Opticon payload locally;
4. report that it is ready for an external OpenSSH test;
5. receive another source fix and repeat; and
6. retain this authorization until an administrator manually revokes it in the Opticon Admin Center.

This mode exists for attended development on trusted test machines. It is not a replacement for the normal signed S3/CloudFront release path.

## Core authorization rule

A testing invitation has no automatic expiration. Once redeemed by its intended device, its testing authorization remains `Active` until an administrator explicitly revokes it in the Admin Center.

The invitation link itself is still single-device and single-redemption:

- It is bound to the exact Tailnet, Tailnet device identity, Tailscale IPv4 address, role, architecture, repository, and rapid-development session.
- Redeeming it a second time or from a different device is refused.
- Redemption creates a durable server-side testing authorization record.
- The Admin Center is authoritative for the record's `Active` or `Revoked` state.
- Every privileged testing operation revalidates the authorization.

The persistent invitation authorization should issue short-lived renewable operation leases. These leases do not expire the invitation. They ensure that manual revocation becomes effective within a bounded time even when a target disconnects during testing. A disconnected target may keep its normal installed Opticon service running, but it may not start another test build, installation, or privileged diagnostic after its last lease expires.

## Security boundaries

Testing mode grants the ability to compile and run changing source as an administrator on the target. The Admin Center must present that fact clearly before creating the invitation.

The following invariants are mandatory:

- Normal invitations and production installations never accept test artifacts.
- The production signing private key is never copied to a target device.
- Testing trust is scoped to one session and one device.
- Testing state is stored in a protected machine-level location and is writable only by `SYSTEM` and local Administrators.
- The testing bootstrap verifies the repository identity, approved branch, requested commit, device identity, and active server authorization before each privileged operation.
- Source is built in a dedicated worktree, never in an unrelated user's working tree.
- Test binaries and logs are clearly marked with the session, round, and source commit.
- Tailscale, RustDesk, enrollment identity, device credentials, and production recovery copies remain available throughout an Agent or Guardian transaction.
- Agent and Guardian replacement remains transactional and rollback-capable.
- Revocation disables testing trust without disabling ordinary production Opticon management.

## Test artifact trust

Production Authenticode checks must not be globally weakened for testing mode.

Use a per-device, per-session test certificate:

1. The target creates a non-exportable local signing key after redeeming the invitation.
2. The target sends only the public certificate and proof of possession through the authenticated testing channel.
3. The Admin Center records the exact certificate fingerprint on the testing authorization.
4. Test builds are signed locally with that certificate.
5. The test bootstrap accepts that signer only when the server authorization is active, the device and session match, and the requested source commit is approved.
6. Production installers, production update APIs, and ordinary invitation flows continue accepting only the production signer.
7. Revocation removes the testing certificate authorization and deletes the local private key after any active transaction reaches a safe terminal state.

Tests should use an unmistakable version identity containing the normal numeric file version plus the rapid-development session, round, and Git commit in informational metadata.

## Actors

### Administrator

- Creates and revokes the testing invitation in the Admin Center.
- Approves the initial attended bootstrap/UAC prompt.
- Decides when the feature has passed sufficiently and testing access can be revoked.

### Fix agent

- Owns the implementation branch.
- Attempts the external OpenSSH test when the target reports readiness.
- Reads complete target diagnostics.
- Implements and verifies fixes in an isolated worktree.
- Pushes a fix commit whose body contains exact instructions for the target agent.
- Repeats until the acceptance criteria pass.

### Target agent

- Runs only on the invited device.
- Monitors the approved implementation branch.
- Builds the exact requested commit in an isolated worktree.
- Runs tests, signs the local test payload, and installs it transactionally.
- Captures build, installation, Agent, Guardian, Task Scheduler, and OpenSSH diagnostics.
- Pushes a readiness or failure report commit to the report branch.
- Never edits the implementation branch.

## Git coordination protocol

Use two branches so two agents never write to the same branch:

- Implementation branch: `codex/rapid-<session-id>`
- Target report branch: `rapid-report/<session-id>/<device-id>`

The fix agent is the sole writer to the implementation branch. The target agent is the sole writer to the report branch. The target checks out the implementation commit in a detached, dedicated worktree and keeps its report worktree separate.

Each test round has a monotonically increasing integer. Both agents must include the rapid-development session ID and round in every commit body and report.

### Fix commit format

```text
<short description of the source fix>

Rapid-Session: <session-id>
Test-Round: <round>
Target-Device: <device-id>
Target-Action: pull and build this exact commit, install the test payload, then run the listed checks
Required-Checks: <commands or named checks>
Expected-Result: <observable success condition>
Relevant-Logs: <paths/endpoints the target agent must capture>
Rollback-Expectation: <what must remain or be restored on failure>
Report-Branch: rapid-report/<session-id>/<device-id>
```

### Target report commit format

The target should commit a machine-readable report file under:

```text
rapid-development/reports/<session-id>/<round>.json
```

The commit body must also contain a short summary:

```text
Target ready for external OpenSSH test

Rapid-Session: <session-id>
Test-Round: <round>
Target-Device: <device-id>
Tested-Commit: <full implementation SHA>
Build: passed|failed
Install: passed|failed|rolled-back
Agent-Version: <version>
Guardian-Version: <version>
Ready-For-SSH-Probe: yes|no
Report-Path: rapid-development/reports/<session-id>/<round>.json
```

The JSON report should include timestamps, exact source SHA, toolchain versions, test results, installed file versions and hashes, relevant service/task state, bounded log excerpts, failure types and native error codes, and whether rollback occurred. It must not contain invitation secrets, bearer tokens, passwords, private keys, or complete command lines containing secrets.

## End-to-end workflow

### 1. Create the testing session

The Admin Center creates a durable testing authorization containing:

- session ID and authorization state;
- exact target device and Tailnet identity;
- approved repository URL and implementation branch;
- target report branch;
- permitted architecture and role;
- registered test certificate fingerprint after redemption;
- current approved source commit and test round;
- current short-lived operation lease status;
- creation, redemption, last-contact, and revocation audit timestamps; and
- the administrator identity responsible for creation and revocation.

The Admin Center displays testing sessions separately from ordinary invitations. It must show a persistent warning while testing access is active.

### 2. Redeem once and bootstrap

The target opens the invitation link and completes an attended bootstrap. The bootstrap verifies the exact target identity, creates the protected testing service and local test certificate, registers the public certificate, clones the approved repository, and creates isolated build/report worktrees.

After redemption, the original link cannot enroll another machine. The existing target authorization remains active until manual revocation.

### 3. Build and install a round

For each approved implementation commit, the target agent:

1. obtains a fresh operation lease;
2. verifies the server still reports the session as active;
3. fetches without accepting a changed repository origin;
4. verifies the requested commit is reachable from the approved implementation branch;
5. checks out that exact commit in the session build worktree;
6. records a clean-source fingerprint and toolchain versions;
7. builds all changed Opticon components and their transitive dependencies;
8. runs the full self-test suite plus tests named in the fix commit;
9. signs the test payload with the session certificate;
10. transactionally installs only the components required for the round;
11. proves Agent, Guardian, Tailscale, RustDesk, and rollback state; and
12. writes and pushes the target report commit.

No source change means no rebuild. The input fingerprint must include source, project files, shared build properties/targets, dependency pins, installer scripts, and any generated-input declarations while excluding `bin`, `obj`, build artifacts, and logs.

### 4. Run the external OpenSSH probe

When the target report says `Ready-For-SSH-Probe: yes`, the fix agent verifies that the report references the latest requested commit and round, then tests through the normal Command Center path.

At minimum, the probe must verify:

- the authenticated Agent reports the exact test Agent and Guardian versions;
- the SSH supervisor task starts under `SYSTEM`;
- the listener binds only to the target's Tailscale address and dedicated port;
- the host key matches the authenticated grant;
- the private key has safe local permissions;
- the dedicated account receives a full, non-`SYSTEM`, high-integrity administrator token;
- the signed administrative attestation succeeds;
- a fixed challenge command executes and returns the expected response;
- lease revocation terminates the authenticated session;
- the listener, account state, firewall rule, key material, and session processes are cleaned up; and
- Tailscale, RustDesk, enrollment, and update rollback state remain healthy.

The fix agent records pass/fail evidence without committing credentials or private key material.

### 5. Iterate

If the probe fails, the fix agent:

1. reads the report and protected target diagnostics;
2. distinguishes source defects from environmental or authorization failures;
3. changes code only in its isolated implementation worktree;
4. adds or strengthens a regression test;
5. runs the proportional local verification suite;
6. commits and pushes the next round using the fix commit format; and
7. waits for the target agent's matching report commit.

The target agent then builds only changed components, installs safely, and reports again. Neither agent declares success merely because a task started or an API returned 2xx; the externally observed acceptance criteria must pass.

## Completion criteria

Testing is complete only after all of the following succeed for the same source commit:

- clean target build and transactional installation;
- exact Agent and Guardian version/hash attestation;
- OpenSSH lease creation, connection, administrative attestation, command execution, and revocation;
- no unprotected private keys or credentials;
- no listener on a non-Tailscale address;
- successful rollback exercise or a previously verified rollback path unaffected by the change;
- one reboot/startup validation proving Guardian and SSH task recovery behavior;
- no regression in file transfer, RustDesk, Tailscale, enrollment, or updates; and
- a final target report commit marked passed.

For changes involving timing, startup, watchdogs, or rollback, run the successful scenario repeatedly rather than relying on a single pass.

## Manual revocation and cleanup

The administrator ends the session with **Revoke testing access** in the Admin Center. Revocation is server-authoritative and immediate for new operation leases.

The target then enters a bounded cleanup flow:

1. stop accepting new test work;
2. allow an already-entered transaction to commit or roll back safely;
3. stop test-only services and scheduled tasks;
4. revoke and delete the session certificate/private key;
5. remove protected testing credentials and repository authorization;
6. restore or update to the latest verified production Opticon release;
7. verify production Agent, Guardian, Tailscale, RustDesk, and SSH-disabled state;
8. optionally remove the isolated source/build worktrees while retaining redacted reports; and
9. acknowledge cleanup to the Admin Center.

If the target is offline during revocation, the server refuses lease renewal immediately. The target disables privileged testing operations when its current lease expires and performs cleanup when it reconnects.

The Admin Center should distinguish `Revoked` from `Cleanup acknowledged` so an offline device cannot be mistaken for a fully cleaned device.

## Failure handling

- A build failure produces a report commit; it does not alter installed components.
- A pre-install verification failure produces a report and leaves the installed system unchanged.
- A Guardian/Agent activation failure withholds commit and rolls back by omission.
- Loss of the Admin Center or target agent never authorizes a commit automatically.
- A report for an unexpected source SHA, device, session, or round is ignored.
- A push race or non-fast-forward report update pauses the loop for investigation.
- Missing or malformed diagnostics are themselves test failures and should result in improved diagnostic coverage before another speculative fix.
- Revocation always wins over queued or newly requested work.

## Operator checklist

Before starting:

- [ ] Confirm the device is disposable or backed up and is approved for privileged testing.
- [ ] Confirm Tailscale and RustDesk recovery access.
- [ ] Create the implementation and target report branches.
- [ ] Create the testing invitation and record the session ID.
- [ ] Complete one-time attended redemption and certificate registration.

For every round:

- [ ] Confirm authorization is still active.
- [ ] Confirm the target built the exact requested commit.
- [ ] Confirm tests and transactional installation completed.
- [ ] Read the target report before probing.
- [ ] Run the full external OpenSSH lifecycle probe.
- [ ] Add a regression test for each source defect.
- [ ] Put the next target instructions in the fix commit body.

At completion:

- [ ] Obtain a final passing target report.
- [ ] Run reboot/startup validation when applicable.
- [ ] Revoke testing access manually in the Admin Center.
- [ ] Confirm lease expiry and target cleanup acknowledgement.
- [ ] Confirm the target is back on a verified production release.
- [ ] Preserve redacted reports and remove test-only secrets/worktrees.

## Implementation order

Build testing mode in this order so no trust bypass exists without its control plane:

1. server-side authorization state, audit trail, and manual revocation;
2. short-lived operation leases tied to active authorization;
3. Admin Center creation/status/revocation UI;
4. one-time target-bound invitation redemption;
5. per-session test certificate registration and protected storage;
6. isolated source checkout, fingerprinting, incremental build, and report generation;
7. transactional test installation and production restoration;
8. Git branch/commit coordination;
9. automated external OpenSSH probe and diagnostic collection; and
10. revocation cleanup, offline behavior, and end-to-end security tests.

Do not ship a testing installer that merely disables Authenticode, identity, or release checks. The authorization, isolation, audit, rollback, and revocation controls are part of the feature—not follow-up hardening.
