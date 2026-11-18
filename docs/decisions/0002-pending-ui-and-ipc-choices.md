# ADR 0002: Pending UI and IPC technology choices (verification required)

- **Date:** 2026-11-13
- **Status:** Partially superseded (IPC by ADR 0003; desktop/chart direction by ADR 0004) / full Wayland HiDPI/input/a11y verification still pending
- **Deciders:** Flow implementation (to be confirmed with evidence)

## Context
The plan specifies Avalonia UI for `Flow.Desktop` and D-Bus for GUI↔daemon IPC on Ubuntu 26.04 (GNOME/Wayland, .NET 10). Current environment provides .NET 10/11 SDKs and a Wayland session, but upstream compatibility and maintained library APIs must be verified before pinning dependencies.

**Update (2026-11-18):** IPC library/topology v1 selected and validated on session bus; contract is now v3 with coalesced status signals (see **ADR 0003**, **ADR 0004**). Avalonia app launch was smoke-tested on GNOME/Wayland, and chart/control direction is recorded in ADR 0004; HiDPI, input, and accessibility conformance remain pending.

## Options to evaluate

### Desktop UI (Avalonia)
- **Candidate direction:** latest stable Avalonia release with verified .NET 10 and Linux Wayland support at implementation time.
- **Verification required:**
  - Rendering/input behavior on GNOME Wayland (Ubuntu 26.04), HiDPI/scaling, keyboard navigation/accessibility basics.
  - Packaging/runtime dependencies for a `.deb` distribution.
  - Charting/control approach (built-in/custom/third-party) aligned with performance and licensing needs.
- **Interim decision:** do not pin Avalonia packages until a minimal Wayland smoke test app is built and run on target.

### D-Bus (.NET)
- **Superseded by ADR 0003:** `Tmds.DBus` 0.95.2, session-bus per-user v1 (`org.flow.Daemon` / `/org/flow/Daemon` / `org.flow.Daemon1`, contract v1, JSON-over-D-Bus request/response) validated on-target. Follow-ups (signals, reconnect/contract tests, system-bus policy/activation/auth) tracked in ADR 0003 and progress.

## Consequences
- IPC choice is now pinned for v1 per-user/session use with captured evidence; system-bus deployment remains deferred.
- Avalonia dependency/Wayland behavior still requires verification before lock-in.

## Next actions
1. Create and run an Avalonia smoke test on the current GNOME Wayland session; capture results in `docs/progress.md`.
2. Record remaining IPC hardening items (signals, reconnect/contract tests, policy/activation) and supersede UI portion with an accepted decision including pinned versions and rationale.
