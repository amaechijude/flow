# Flow progress log

## 2026-11-13 (initial environment inspection and planning pass)

### Completed
- Inspected the development environment (Ubuntu 26.04.1 LTS, kernel 7.0.0-43-generic, x86_64, GNOME/Wayland session).
- Confirmed .NET SDK availability: 10.0.404 and 11.0.100 installed under `/opt/dotnet/sdk`.
- Confirmed relevant Linux/eBPF tooling present: `bpftool v7.7.0` (libbpf 1.7), `libbpf1`, BCC packages, kernel BTF available at `/sys/kernel/btf/vmlinux`.
- Noted `kernel.unprivileged_bpf_disabled=2` (unprivileged BPF restricted), so privileged/capability-based collection is expected for a production daemon.
- Created initial documentation set required by the project plan:
  - `docs/architecture.md`
  - `docs/monitoring.md`
  - `docs/attribution.md`
  - `docs/security.md`
  - `docs/database.md`
  - `docs/implementation-plan.md`
  - `docs/decisions/0001-initial-technical-direction.md`

### Current work
- Phase 0 / first-task baseline: documenting feasibility, risks, and recommended path before full daemon/GUI implementation.
- No product features, daemon, or desktop UI implemented yet (per plan: prototype/validation gate comes first).

### Test results
- No automated tests executed yet (no solution/projects created in this pass).
- Environment checks performed manually: `uname -a`, `/etc/os-release`, `dotnet --list-sdks`, `dotnet --list-runtimes`, Wayland session variables, `bpftool`/BTF presence.

### Blockers / risks
- **Per-process byte attribution prototype not yet validated end-to-end.** This is the critical technical gate; implementation must not claim completion without repeatable evidence on target.
- `clang`/LLVM toolchain for compiling BPF C objects was not explicitly confirmed in this pass; packaging/build prerequisites for eBPF artifacts need verification.
- Avalonia Wayland behavior and maintained .NET D-Bus library choice require validation against current upstream docs/APIs before locking dependencies.
- Hook choice (e.g. cgroup skb vs socket/tc/kprobe-based accounting), namespace handling, VPN/virtual interface semantics, and overhead must be proven with controlled traffic.

### Next steps
1. **Unblock BPF object build:** install/verify `clang` + LLVM tools and libbpf development artifacts as needed for the chosen CO-RE workflow; document exact packages and record ADR.
2. Build a minimal eBPF CLI proof of concept in `prototype/` (or `src/`) that:
   - loads a BPF program with explicit privileges/capabilities,
   - attributes send/receive bytes to process identity (pid/start-time/comm/exe/cgroup/uid where possible),
   - reports unattributed traffic and compares aggregates to interface counters under controlled load.
3. Measure CPU/memory overhead and document unsupported cases (namespaces/VPN/virtual interfaces/short-lived processes).
4. Decide and record D-Bus library and Avalonia version pinning after compatibility checks.
5. Only after the monitoring gate passes: scaffold solution/projects, implement domain/persistence, then daemon, then desktop per plan.

## 2026-11-13 (prototype scaffolding and toolchain verification)

### Completed
- Verified additional prerequisites: kernel headers for `7.0.0-43-generic` present, BTF available, `libbpf.so.1` present; **`clang`/LLVM build tools not found on PATH** and `libbpf-dev` not installed (BPF C compilation currently blocked in this environment).
- Generated CO-RE reference header: `prototype/bpf/vmlinux.h` (from `/sys/kernel/btf/vmlinux` via `bpftool`).
- Scaffolded Phase 1 prototype workspace:
  - `prototype/README.md`
  - `prototype/bpf/flow_monitor.bpf.c.stub` (placeholder; not compiled/validated)
  - `prototype/Flow.MonitorPrototype/` (.NET 10 console diagnostic CLI)
- Implemented and verified a **baseline diagnostic** CLI (not per-process attribution):
  - prints environment/tooling/privilege indicators,
  - reads aggregate per-interface counters from `/proc/net/dev` and samples deltas,
  - samples process identity inputs (PID, UTC start time, UID, comm, exe where readable).
- Build/run verification:
  - `dotnet build prototype/Flow.MonitorPrototype/Flow.MonitorPrototype.csproj -v minimal` → **succeeded** (net10.0)
  - `dotnet run --project prototype/Flow.MonitorPrototype/Flow.MonitorPrototype.csproj -- sample` → **ran successfully**, showing live interface deltas and process samples (still no per-process byte attribution).

### Current work
- Phase 0/1 transition prep: prototype scaffolding exists, but the **eBPF attribution technical gate remains open** pending BPF toolchain availability and a validated hook/loader implementation.

### Test results
- Manual prototype execution succeeded on target host; output confirms baseline discovery/counter sampling only.
- No unit/integration/system tests added yet.

### Blockers / risks
- Cannot compile/load a custom BPF program yet without `clang` (and compatible BPF build workflow).
- Attribution semantics/hook selection still unproven; current prototype must not be interpreted as satisfying per-application/per-process accounting requirements.
- Process sample in this environment shows many kernel threads with unreadable exe paths (expected for restricted `/proc/[pid]/exe`); userspace correlation design must handle this.
