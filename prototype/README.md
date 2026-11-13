# Flow monitoring prototype (Phase 1 gate)

This directory contains early prototype artifacts for validating per-process network byte attribution on the target Ubuntu environment.

## Current contents
- `bpf/vmlinux.h` - generated from `/sys/kernel/btf/vmlinux` on the development host (CO-RE/BTF reference for BPF programs).
- `Flow.MonitorPrototype/` - .NET 10 CLI prototype scaffold for discovery/diagnostics and future eBPF userspace integration.

## Status
- **Attribution gate:** not yet passed.
- **BPF object build:** blocked in this environment until a BPF toolchain is available (`clang`, compatible LLVM tools, and any required libbpf development artifacts/workflow). Runtime BTF and `bpftool` are present.

## Intended next prototype steps
1. Add a minimal BPF C program (e.g. socket/cgroup/tc hook candidate) with explicit byte semantics.
2. Compile CO-RE BPF object and (optionally) generate a libbpf skeleton.
3. Load/attach from the CLI with required capabilities and stream events into userspace.
4. Correlate events to stable process identity (pid + start time + exe/uid/cgroup as available).
5. Report per-process up/down counters, unattributed traffic, and compare aggregates to `/proc/net/dev` / `ip -s link` under controlled traffic.
6. Record overhead and unsupported cases in `docs/progress.md` and `docs/attribution.md`.
