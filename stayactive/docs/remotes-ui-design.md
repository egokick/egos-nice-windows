# Remotes UI design

## Purpose

`Remotes` is an opt-in, self-hosted remote-management surface. It must make the
target, requested capability, consent state, and active traffic routing obvious
at all times. The tray menu provides fast access; the Remotes window provides
the complete device, transfer, pairing, and audit experience.

## Tray menu

```text
StayActive tray menu
├─ Active
├─ …existing StayActive controls…
├─ Remotes ▸
│  ├─ Headscale: Connected as operator@example.test
│  ├─ Open Remotes…
│  ├─ Pair a computer…
│  ├─ ─────────────────────────
│  ├─ Using internet exit: None                         [when inactive]
│  │  or
│  ├─ Using internet exit: Austin-PC  [Turn off]
│  ├─ ─────────────────────────
│  ├─ Austin-PC · Alice · Austin office · Online ▸
│  │  ├─ Route all internet traffic through Austin-PC   [toggle]
│  │  ├─ View screen…
│  │  ├─ Send file…
│  │  ├─ Request a file…
│  │  ├─ Details…
│  │  └─ Revoke this computer…
│  ├─ London-Laptop · Bob · London · Offline ▸
│  │  └─ Details…
│  ├─ ─────────────────────────
│  └─ Remote settings…
├─ …existing VM controls…
└─ Exit
```

The first line is a health indicator, not a clickable network-control action.
The device list is refreshed asynchronously while the menu opens. Entries are
sorted online-first, then by display name. A list with no paired devices shows
an explanatory `Pair a computer…` item rather than an empty submenu.

## Routing confirmation

Selecting an exit node always opens a confirmation dialog before changing the
operating-system route:

```text
Route internet traffic through Austin-PC?

All non-overlay internet traffic from this computer will leave through
Austin-PC's public network connection. Austin-PC's owner can see connection
metadata and any traffic that is not end-to-end encrypted by the destination.

[ ] Keep access to this computer's local network

Cancel   Route through Austin-PC
```

After success, the tray icon tooltip and the Remotes window show a persistent
`Using Austin-PC as internet exit` state with a one-click `Turn off` action.

## Remotes window

`Open Remotes…` launches one modeless window. It has a header, a searchable
device table, a details pane, and an audit drawer.

```text
┌ Remotes ─────────────────────────────────────────────────────────────────┐
│ Headscale: Connected   This device: HQ-Laptop   [Pair] [Settings]         │
│ Search devices…                                                     Audit │
├────────────────────────────────┬─────────────────────────────────────────┤
│ DEVICES                        │ AUSTIN-PC                                │
│ ● Austin-PC                    │ Online · Alice · Austin office           │
│   Alice · Austin office        │ Last seen: now                            │
│   Exit node available          │                                         │
│                                │ [Route traffic through this computer]    │
│ ● HQ-Laptop (this device)      │ [View screen] [Send file] [Request file] │
│                                │                                         │
│ ○ London-Laptop                │ Capabilities                              │
│   Bob · London                 │ ✓ Exit node  ✓ Screen  ✓ File transfer   │
│                                │ ✓ Local authenticator approval            │
│                                │                                         │
│                                │ Permissions                               │
│                                │ Screen: ask every session                 │
│                                │ Files: target selects/approves            │
│                                │ Exit: owner-approved                      │
│                                │                                         │
│                                │ [View pairing details] [Revoke computer] │
└────────────────────────────────┴─────────────────────────────────────────┘
```

The `Username` displayed in the list is an owner-selected, opt-in display
name. It is not silently derived from the interactive Windows account. A
location is a coarse, owner-entered label (for example, `Austin office`), never
an inferred precise location from IP address or device sensors.

## Consent and transfer UX

- `View screen…` shows the selected connection method and asks the target for
  consent unless unattended viewing has been explicitly enabled for that
  computer. The target displays a persistent viewing indicator and a Stop
  button.
- `Send file…` opens a local file picker, displays the filename, size, SHA-256
  checksum, target, and delivery folder before confirmation, then shows
  progress and the final checksum result.
- `Request a file…` sends a request only. The target chooses the file and
  approves the transfer. No user can browse or retrieve arbitrary paths from a
  remote computer without a scoped, explicit sharing rule.
- The audit drawer records pairing, revocation, consent, routing, screen, and
  transfer metadata. It never stores file contents, Bluetooth material, or
  authentication secrets.

## Local authenticator approval

The device details pane includes `Local authenticator approval` as a
capability. It can require a short-lived, target-bound WebAuthn/FIDO approval
for sensitive actions. It does **not** offer remote Bluetooth adapter
virtualization or passkey relaying. If a remote session needs a passkey, the
UI directs the user to supported WebAuthn redirection or an authenticator
physically local to the target session.

## Failure and safety states

- `Headscale unavailable`: show a disabled device list and a retry action;
  never silently fall back to a hosted control plane.
- `No self-hosted relay available`: show the affected device as degraded; do
  not send traffic through a third-party relay.
- `Exit node unavailable`: leave existing routing fail-closed and display the
  reason.
- `Permission denied`: display the denied capability and the owner who must
  authorize it.
- `Untrusted or revoked`: remove action buttons immediately and preserve only
  audit details.

## Acceptance criteria for the UI phase

1. The tray menu remains responsive while device status refreshes.
2. Device names, owner display names, locations, online state, and capability
   state render accurately with deterministic ordering.
3. Every sensitive action has an explicit target and a confirmation/consent
   state before execution.
4. An active exit node is visible from both the tray and the full window.
5. The UI never implies support for remote Bluetooth or passkey relaying.
