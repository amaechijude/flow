# Flow implementation plan (initial)

> Status: **Phase 0 baseline** aligned with `flow-project-plan.md`. Progress is gated by proof of per-process byte attribution on the target system.

## Target baseline
- Ubuntu 26.04.1 LTS, GNOME, Wayland, x86_64 (confirmed in current environment)
- .NET 10 LTS for product code (SDK 10.0.404 available); validate Avalonia/.NET 10 compatibility before pinning
- Independently runnable **Flow.Daemon** and **Flow.Desktop**

## Phase 0 — Environment and feasibility research (in progress)
**Goals:** evidence-based technical design and realistic path to per-process attribution.

- [x] Inspect environment (OS/kernel/arch/session/.NET/eBPF tooling snapshot)
- [x] Create required baseline docs (`architecture`, `monitoring`, `attribution`, `security`, `database`, `implementation-plan`, `progress`)
- [ ] Verify Avalonia Wayland behavior for target session and choose/pin UI version
- [ ] Evaluate maintained .NET D-Bus library options and IPC approach
- [x] Verify eBPF build toolchain/prereqs (clang/llvm/libbpf headers/CO-RE workflow) — clang/LLVM/llvm-strip/libbpf-dev/BTF/bpftool present; `vmlinux.h` generated; BPF object/skeleton/loader build succeeds
- [~] Prototype eBPF attribution path and complementary Netlink/proc enrichment — **in progress:** compiled TCP fexit BPF prototype + userspace loader built; privileged live-traffic validation and full gate criteria (stable identity, PID reuse, unattributed, UDP/ns/VPN coverage, controlled comparison, overhead) still pending
- [ ] Measure accuracy/overhead with controlled traffic and document limitations
- [ ] Record decisions in `docs/decisions/`

**Exit criteria:** documented design + repeatable prototype evidence (or explicit go/no-go revision).

## Phase 1 — Monitoring proof of concept (CLI, technical gate)
- [ ] Discover processes/interfaces
- [ ] Attribute controlled traffic with documented measurement point
- [ ] Handle process exit and PID reuse
- [ ] Track and report unattributed traffic
- [ ] Compare aggregates with interface counters
- [ ] Report CPU/memory overhead and unsupported cases

**Exit criteria:** repeatable evidence that approach works sufficiently on target (see plan section 3).

## Phase 2 — Domain and persistence foundation
- [ ] Scaffold solution/project structure (`src/`, `tests/`, central props/package mgmt)
- [ ] Implement core identities, measurement types, time-range rules
- [ ] SQLite schema/migrations/repositories
- [ ] Idempotent ingestion and transactional aggregation pipeline
- [ ] Unit and integration tests (time boundaries, PID reuse, aggregation, persistence)

## Phase 3 — Daemon
- [ ] Worker Service host and monitoring orchestration
- [ ] Integrate validated provider
- [ ] Process/application discovery and grouping
- [ ] Health states, diagnostics, lifecycle/systemd integration

## Phase 4 — Historical reporting
- [ ] Time-range queries and tiered rollups
- [ ] Retention policies and async cleanup
- [ ] Comparisons and export (CSV, optional JSON) with required metadata

## Phase 5 — Desktop GUI (Avalonia, MVVM)
- [ ] App shell, themes, navigation, accessible states
- [ ] Overview, Applications/Processes/Interfaces, Reports/History/Comparisons, Settings/About
- [ ] Distinct live vs historical presentation; visible attribution/health limitations

## Phase 6 — D-Bus integration
- [ ] Versioned contract in `Flow.Contracts`
- [ ] Daemon methods/signals with validation and authorization
- [ ] Desktop client with reconnect/compat handling
- [ ] Contract and integration tests

## Phase 7 — Hardening and release
- [ ] Recovery/system scenarios (network churn, VPN, suspend/resume, restarts, failures)
- [ ] Performance profiling and DB growth validation
- [ ] Security/privacy review and Wayland validation
- [ ] Debian packaging (`.deb`) with systemd/D-Bus/Polkit/native deps
- [ ] Installation/upgrade/troubleshooting/uninstall docs

## Immediate execution order (next actions)
1. Complete Phase 0 verification items (toolchain, D-Bus/Avalonia research, ADRs).
2. Build and run Phase 1 CLI prototype; update `docs/attribution.md` and `docs/progress.md` with evidence.
3. If gate passes, scaffold .NET solution and begin Phase 2 with tests-first for domain/time/identity rules.

## Risks to track
- Attribution accuracy/semantics across hooks, namespaces, VPNs, and virtual interfaces.
- Privilege/capability requirements vs least-privilege and packaging ergonomics.
- Overhead and event loss under load.
- Library API/compatibility drift (Avalonia, D-Bus bindings) — verify before implementation commitments.
