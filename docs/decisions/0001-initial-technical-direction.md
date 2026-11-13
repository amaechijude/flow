# ADR 0001: Initial technical direction (Phase 0 baseline)

- **Date:** 2026-11-13
- **Status:** Accepted (baseline), pending validation by monitoring prototype
- **Deciders:** Flow implementation (initial pass)

## Context
Flow must be an Ubuntu 26.04 LTS (GNOME/Wayland) desktop network usage monitor with a privileged/background daemon and an unprivileged Avalonia GUI. The critical requirement is transparent per-application/per-process byte attribution with persistent history. The project plan mandates environment/feasibility work and a per-process accounting prototype before full product implementation.

Environment findings: Ubuntu 26.04.1 LTS, kernel 7.0.0-43-generic (BTF available), .NET SDKs 10.0.404/11.0.100 installed, bpftool/libbpf present, `unprivileged_bpf_disabled=2`.

## Decision
1. **Follow the plan’s phased approach:** complete documentation and a CLI monitoring proof-of-concept (technical gate) before building the full daemon/GUI.
2. **Target stack:** C#/.NET 10 LTS, Avalonia UI (MVVM), .NET Worker Service daemon, SQLite, D-Bus IPC, systemd, Polkit as needed, per plan.
3. **Monitoring strategy:** investigate an **eBPF-based** per-process attribution provider as primary path, complemented by **Netlink** and **/proc** for metadata; do **not** commit to a specific hook until prototype evidence confirms attribution quality, semantics, permissions, and overhead on kernel 7.0.
4. **Architecture boundaries:** keep domain/application/infrastructure/linux/contracts/daemon/desktop separation as proposed; allow simplification only with evidence. Keep native shim minimal and ABI-only if required.
5. **Correctness posture:** explicitly model measurement point semantics, unattributed traffic, attribution confidence, and health/collection gaps; never present interface totals as per-process attribution.

## Consequences
- **Positive:** reduces risk of building UI/features on an invalid measurement foundation; aligns implementation with verifiable milestones.
- **Negative/cost:** delays visible product features until prototype and core plumbing are proven.
- **Follow-ups required:**
  - ADR for chosen eBPF hook/byte semantics and CO-RE toolchain.
  - ADR for D-Bus library and contract versioning.
  - ADR for Avalonia version and Wayland validation results.
  - Evidence-backed updates to `docs/attribution.md` after prototype runs.

## Alternatives considered (deferred)
- **Interface counters/Netlink-only accounting:** rejected as primary solution because it cannot satisfy per-process attribution requirement (may be used for comparison/diagnostics).
- **Building GUI first:** rejected by plan and risk profile.
- **Claiming broad distro/DE support initially:** deferred; focus on Ubuntu 26.04 GNOME Wayland target first.
