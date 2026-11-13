# Flow monitoring strategy (initial)

> Status: **Phase 0 baseline**. No monitoring provider has been validated end-to-end yet. This document defines the intended approach, requirements, and investigation plan per `flow-project-plan.md`.

## 1. Environment snapshot (2026-11-13)

- **OS:** Ubuntu 26.04.1 LTS (resolute), kernel `7.0.0-43-generic`, x86_64.
- **Session:** GNOME on Wayland (`XDG_SESSION_TYPE=wayland`).
- **.NET:** SDKs 10.0.404 and 11.0.100 present.
- **eBPF/tooling present:** `bpftool v7.7.0` (libbpf 1.7), `libbpf1`, BCC packages, kernel BTF at `/sys/kernel/btf/vmlinux`.
- **Restriction noted:** `/proc/sys/kernel/unprivileged_bpf_disabled` is `2` (unprivileged BPF disabled/admin-only model), implying the daemon/provider will require appropriate privileges/capabilities (to be minimized and documented).

## 2. Requirements the provider must satisfy

1. Attribute network activity to **process identity** (not PID alone): include PID + process start/generation identifier, executable path when available, UID, and relevant cgroup/container/namespace metadata where practical.
2. Report **upload/download byte counters per process** at a **documented measurement point** with clear semantics.
3. Handle process exit and **PID reuse** correctly.
4. Separate and report **unattributed traffic** (never silently discard).
5. Distinguish attributed process totals from **interface totals**; avoid double-counting across virtual/bridge/VPN/physical paths.
6. Support IPv4/IPv6 and TCP/UDP to the extent the chosen hooks allow; document gaps.
7. Operate with low, predictable overhead; expose health/degraded states and collection gaps.

## 3. Candidate data sources

| Source | Role | Notes |
|---|---|---|
| **eBPF** | Primary per-process byte attribution (preferred investigation) | Hook choice must be validated on target kernel for accuracy, permissions, overhead, and semantics. BTF/CO-RE support expected on kernel 7.0. |
| **Netlink** | Interface discovery/state, addresses/routes, selected socket/link metadata | Useful complement; **not** assumed to provide complete per-process byte accounting by itself. |
| **/proc** | Process metadata/lifecycle, exe/cmdline context, namespaces/cgroups where available | Needed to build stable identities and application mapping; handle races/permission limits. |
| **Interface counters** | Independent aggregate comparison | Used to validate totals and highlight attribution gaps/double-count risks, not as per-app source of truth. |

## 4. eBPF investigation areas (to prototype)

- **Hook options (evaluate, do not pre-commit):** cgroup skb accounting hooks, socket-level hooks, tc ingress/egress, tracepoints/kprobes on networking paths, etc. Select based on: bytes counted (payload vs L3/L4 vs on-wire), directionality, container/namespace visibility, required attach capabilities, and verifier/kernel compatibility.
- **Identity correlation:** map socket/flow events to task/process using available helpers/metadata; investigate socket cookies and task context at hook points.
- **Direction & accounting:** ensure upload/download are classified consistently with measurement point; prevent duplicate counting when traffic traverses multiple attach points.
- **Namespaces/virtualization:** assess behavior with network namespaces, veth pairs, bridges, VPN/tunnel interfaces, Docker/Snap/Flatpak contexts.
- **Portability:** rely on BTF/CO-RE where possible for Ubuntu kernel updates; pin/build strategy to be decided.

## 5. Proof-of-concept (technical gate) — required before full product

A CLI prototype must demonstrate on the target environment:

1. Identification of processes generating network traffic.
2. Per-process upload/download byte counters at a **documented** observation point.
3. Correct handling of process exit and PID reuse.
4. Separation of unattributed traffic.
5. Comparison of aggregates with interface counters and controlled test traffic.
6. Measurement/reporting of prototype CPU and memory overhead.
7. Documented unsupported cases and attribution limitations.

**Current status:** not yet implemented/executed in this repository pass. Gate remains **open**.

## 6. Performance and reliability considerations

- Bounded queues/channels and deliberate backpressure; if events are dropped, expose loss in diagnostics (never claim complete accounting).
- Batched DB writes and async retention; collection must not be blocked by UI/export/cleanup.
- Health states to expose: Initializing, Monitoring, Paused, Degraded, Unavailable, with last-update timestamps.
- Recovery scenarios to validate later: interface/VPN churn, suspend/resume, daemon/GUI restart, provider failure, DB interruption.

## 7. Immediate next technical steps

1. **Install/confirm BPF build prerequisites** for chosen workflow (`clang`, LLVM tools, libbpf dev artifacts/headers as needed). Current host: BTF/bpftool/libbpf runtime/kernel headers present, `prototype/bpf/vmlinux.h` generated, but `clang` and `libbpf-dev` are missing.
2. Implement minimal BPF object + loader prototype with explicit capability requirements and safe failure/health reporting (scaffold exists in `prototype/`, attribution not yet implemented).
3. Run controlled traffic tests (curl/wget/iperf/local sockets/apps) and compare against `ip -s link`/interface counters.
4. Capture findings in `docs/attribution.md` and `docs/progress.md`; create ADRs for hook and toolchain decisions.

## 8. Prototype artifacts (current)

- `prototype/README.md` — scope/status of prototype workspace.
- `prototype/bpf/vmlinux.h` — generated from host BTF for CO-RE development.
- `prototype/bpf/flow_monitor.bpf.c.stub` — placeholder BPF source (not compiled/validated).
- `prototype/Flow.MonitorPrototype/` — .NET 10 diagnostic CLI (builds/runs). **Current capability:** environment/prereq reporting, `/proc/net/dev` aggregate counter snapshots/deltas, and process identity sampling; **not** validated per-process byte attribution.
