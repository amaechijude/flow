# ADR 0002: Pending UI and IPC technology choices (verification required)

- **Date:** 2026-11-13
- **Status:** Partially superseded (IPC by ADR 0003) / UI pending verification
- **Deciders:** Flow implementation (to be confirmed with evidence)

## Context
The plan specifies Avalonia UI for `Flow.Desktop` and D-Bus for GUI↔daemon IPC on Ubuntu 26.04 (GNOME/Wayland, .NET 10). Current environment provides .NET 10/11 SDKs and a Wayland session, but upstream compatibility and maintained library APIs must be verified before pinning dependencies.

**Update (2026-11-15):** IPC library/topology v1 selected and validated on session bus; see **ADR 0003**. Avalonia/Wayland UI verification remains pending.

## Options to evaluate

### Desktop UI (Avalonia)
- **Candidate direction:** latest stable Avalonia release with verified .NET 10 and Linux Wayland support at implementation time.
- **Verification required:**
  - Rendering/input behavior on GNOME Wayland (Ubuntu 26.04), HiDPI/scaling, keyboard navigation/accessibility basics.
  - Packaging/runtime dependencies for a `.deb` distribution.
  - Charting/control approach (built-in/custom/third-party) aligned with performance and licensing needs.
- **Interim decision:** do not pin Avalonia packages until a minimal Wayland smoke test app is built and run on target.

### D-Bus (.NET)
- **Candidate direction:** a maintained .NET D-Bus library with clear Linux support (commonly **Tmds.DBus** in .NET/Linux projects) or equivalent; final choice depends on current maintenance, API fit, and .NET 10 compatibility.
- **Verification required:**
  - Contract modeling (methods/signals/properties), async patterns, and versioning strategy.
  - System vs session bus usage, service activation with systemd, and integration with Polkit/authorization flows where needed.
  - Compatibility with AOT/trimming goals if relevant (likely not required initially).
- **Interim decision:** define `Flow.Contracts` DTOs independently of transport library; implement a thin adapter once library is selected.

## Consequences
- Defers dependency lock-in until evidence exists, reducing rework risk.
- Requires small spike projects during Phase 0/6 prep (Avalonia Wayland smoke test, D-Bus echo/prototype) and follow-up ADRs recording final selections and versions.

## Next actions
1. Create and run an Avalonia "hello world" smoke test on the current GNOME Wayland session; capture results in `docs/progress.md`.
2. Prototype a minimal D-Bus service/client pair using candidate library; validate method call + signal + reconnect basics.
3. Replace this ADR with accepted decisions (or superseding ADRs) including pinned versions and rationale.
