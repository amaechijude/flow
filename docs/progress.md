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
- Cannot compile/load a custom BPF program yet without `clang` (and compatible BPF build workflow) and likely `libbpf-dev` headers.
- Attribution semantics/hook selection still unproven; current prototype must not be interpreted as satisfying per-application/per-process accounting requirements.
- Process sample in this environment shows many kernel threads with unreadable exe paths (expected for restricted `/proc/[pid]/exe`); userspace correlation design must handle this.
- Avalonia Wayland behavior and maintained .NET D-Bus library/API choice remain unverified on target.

## 2026-11-13 (git initialization and continued prototype prep)

### Completed
- Initialized git repository and created `.gitignore` for .NET/build/local-db/prototype artifacts.
- Commits:
  - `9694f3d` — baseline plan/docs/prototype scaffold and generated `vmlinux.h`
  - `815f58b` — prerequisite check script, pending UI/IPC ADR, and draft eBPF program
- Added `scripts/check-prereqs.sh` to report toolchain/kernel/BTF/libbpf prerequisites (no package installation performed).
- Added `docs/decisions/0002-pending-ui-and-ipc-choices.md` documenting pending verification for Avalonia (Wayland) and .NET D-Bus library selection.
- Added `prototype/bpf/flow_net_monitor.bpf.c.draft` (hypothesis draft, e.g. cgroup_skb ingress/egress accounting sketch) — **not compiled or validated**; hook/byte semantics must be chosen from evidence.

### Current work
- Repository is now version-controlled; Phase 0/1 remains blocked on BPF build toolchain availability and validated attribution prototype.

### Test results
- `scripts/check-prereqs.sh` created (executable) for repeatable environment checks; prior prototype build/run still passing as reported above.

### Next steps
1. **Run privileged validation of TCP prototype:** execute `sudo ./prototype/bpf/flow_tcp_bytes_loader <interval> <rounds>` while generating controlled TCP traffic (e.g. `curl` downloads), compare TGID-attributed deltas to generating processes and to interface counter deltas (`/proc/net/dev`, `ip -s link`); record results, gaps, and overhead in `docs/attribution.md`/`docs/progress.md`.
2. **Harden prototype toward gate criteria:** enrich attribution to stable process identity (PID + start time/generation id, exe/cmd/uid/cgroup/namespace as available), handle process exit/PID reuse, add explicit **unattributed** accounting/diagnostics, and evaluate UDP/other-path coverage and hook/semantics tradeoffs (cgroup_skb vs fentry/tc/etc.) with evidence.
3. Spike Avalonia Wayland smoke test and D-Bus prototype; supersede ADR 0002 with accepted decisions and pinned versions.

## 2026-11-13 (BPF TCP prototype compiled and loader built)

### Completed
- BPF build prerequisites now available on host (clang/LLVM/llvm-strip/libbpf-dev confirmed via `scripts/check-prereqs.sh`).
- Implemented and compiled TCP byte-counter BPF prototype:
  - `prototype/bpf/flow_tcp_bytes.bpf.c` (`fexit/tcp_sendmsg`, `fexit/tcp_recvmsg` returning >0 bytes; per-(tgid,uid) cumulative maps)
  - compiled `flow_tcp_bytes.bpf.o` (CO-RE/BTF target, warnings only from `vmlinux.h`)
  - generated `prototype/bpf/flow_tcp_bytes.skel.h`
- Built userspace loader `prototype/bpf/flow_tcp_bytes_loader.c` (libbpf skeleton-based; prints periodic TGID/UID/TX/RX snapshots). Binary built locally but intentionally not committed (rebuildable).
- Added `prototype/bpf/README.md` documenting measurement semantics, limitations (TCP-only hooks, non-final attribution), rebuild steps, privileged run, and controlled-validation approach.

### Test results
- BPF object compilation: **succeeded**.
- Loader compilation: **succeeded** (`cc ... -lbpf -lelf -lz`).
- Runtime without sufficient privilege: **failed as expected** with `EPERM` / `failed to load BPF object` on this host (`unprivileged_bpf_disabled=2`), confirming privilege requirement; **no validated per-process attribution data collected yet**.

### Blockers / risks
- **Privileged runtime validation pending:** prototype must be executed with adequate privileges/capabilities to attach/load and observe real traffic.
- **Signature/attach compatibility:** initial `fexit/tcp_recvmsg` BTF signature did not match kernel 7.0 on this host (verifier load failure); RX hook switched to `kretprobe/tcp_recvmsg` for broader compatibility (needs revalidation under `sudo`).
- **Gate criteria still unmet:** current prototype is TCP send/recv return-byte accounting per TGID/UID only; does not yet provide stable process identity, PID-reuse-safe records, process-exit lifecycle handling, unattributed bucket, UDP/namespace/VPN coverage analysis, or controlled accuracy/overhead report.
