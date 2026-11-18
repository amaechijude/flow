# Flow attribution model (initial)

> Status: **Phase 0 baseline / planning**, with event-time process identity implemented and one accepted controlled TCP-loopback run (ADR 0005, `docs/measurements/`). TCP+UDP hooks and per-interface keys are implemented in source (ADR 0006) but not yet validated with privileges. Physical interfaces, namespaces, VPNs, and sustained overhead evidence are still outstanding; do not treat the gate as closed.

## 1. Why attribution is non-trivial

- Linux **interface counters** report bytes per interface, not per process/application.
- **Netlink** helps discover interfaces/routes/sockets metadata but is not, by itself, a complete per-process byte accounting source.
- Byte counts differ by **observation point** (application payload, TCP/UDP payload, IP packets, link-layer frames). Totals from different layers/attach points are not directly interchangeable.

## 2. Identity model (required)

A PID is not stable (PIDs are reused). Flow must represent process identity with a stable composite, at minimum:

- PID
- Process start time or another reliable process-generation identifier
- Executable path when available
- UID
- Relevant cgroup/container metadata when available

Where useful and supported, investigate **socket cookies** or equivalent identifiers to correlate socket events with process/task identity. Application grouping must only merge processes when supported by reliable metadata (XDG desktop entries/IDs, executable paths, Snap/Flatpak metadata, icons) and must keep unknown/system processes inspectable.

### Implemented identity behavior (development increment, ADR 0005)

- The eBPF provider keys counters by `{start_ns, tgid, uid}` where `start_ns` is
  the **thread-group leader's** `task_struct.start_time` captured at event time.
  PID reuse creates a separate counter row/process generation instead of merging
  bytes, while all threads of one process stay in one row.
- `start_ns`, `cgroup_id`, and task `comm` are captured on first observation in
  the kernel, so short-lived processes retain event-time identity after exit.
- `ProcessIdentity.StartTimeUtc` prefers `boot_unix_ms + start_ns`, then
  `startticks / clk_tck`, then the current time (degraded, documented in the
  event's attribution note).
- `ProcessIdentity.StableKey` is `pid:startUnixMs:uid`. Executable path is
  metadata, not key material, so later enrichment cannot split one process
  generation.
- The loader enriches `exe` from `/proc` only when the live PID's start ticks
  match the event-time start time (±2 ticks). Otherwise the executable remains
  unknown and the kernel-captured `comm` is used; the event note says identity
  was not verified.

## 3. Measurement semantics (to be documented precisely)

Flow must explicitly define and persist/report:

- **What is counted:** bytes observed at the chosen provider hook/attach point (exact definition TBD by prototype).
- **Direction:** upload vs download classification rules for that hook.
- **Scope:** which interfaces/namespaces/traffic classes are included or excluded.
- **Granularity:** per-process, per-application (aggregated from confidently associated processes), per-interface, and unattributed buckets.

The UI and exports must state the measurement method and caveats.

## 4. Attribution coverage and confidence

- Every accounted unit of traffic presented in totals must belong to either an **attributed** entity or an explicit **unattributed** bucket.
- Unknown attribution must be distinguishable from **zero usage**.
- Expose attribution coverage/confidence where meaningful (e.g. percentage/indicators, diagnostics) so users understand completeness.
- Do not claim perfect attribution when kernel/provider limitations prevent it.

## 5. Virtual interfaces, VPNs, and double-counting risks

- Traffic may traverse multiple logical interfaces (veth/bridge/tun/tap/WireGuard/etc.) and a physical interface.
- Flow must **avoid double-counting** the same bytes across layers/paths in a way that inflates user-visible totals.
- VPN and virtual-interface accounting can differ from physical-interface accounting; the product must explain this and allow interface-scoped views/filters.
- Aggregate comparisons against independent interface counters are for validation/diagnostics, not proof of per-app correctness by themselves.

## 6. Process lifecycle and edge cases

Prototype and product must handle (to supported extent):

- Short-lived processes and process exit during/after traffic
- PID reuse
- IPv4 and IPv6
- TCP and UDP
- Network namespaces and containerized workloads where observable
- Provider start/stop gaps and event loss (must be surfaced)

## 7. Prototype evidence (2026-11-13, kernel 7.0.0-43-generic)

### Validated (initial, privileged run)
- **Hooks:** `fexit/tcp_sendmsg` (TX) and `kretprobe/tcp_recvmsg` (RX) loaded and attached successfully via libbpf skeleton with `sudo` (`unprivileged_bpf_disabled=2`).
- **Observed attribution:** cumulative and per-interval delta per-(TGID, UID) TCP byte counters reported during live sampling runs.
  - Initial run: attributed traffic observed for TGIDs 64022/64042.
  - Enriched run (`2s x 20`): attributed traffic observed for multiple applications including `msedge`, `code` (Snap path), and `opencode`, with changing deltas over time.
- **Process-exit visibility issue:** TGIDs observed with counters but unresolved `/proc` enrichment (`COMM=?`, `EXE=-`) at snapshot time (likely exited/short-lived), demonstrating that enrichment must be captured at event/first-seen time and retained independently of process liveness.
- **Measurement point (current prototype):** bytes taken from positive return values of `tcp_sendmsg`/`tcp_recvmsg` (TCP) and `udp_sendmsg`/`udp_recvmsg` (UDP) kernel functions. Not equivalent to IP/link/interface counters.
- **Tooling note:** loader `startticks` parsing bug produced `0` in an earlier run; fixed and subsequently confirmed to report non-zero startticks in rerun.

### Recent prototype enhancements (source + unit tests; privileged rerun pending)
- **Explicit unattributed accounting:** BPF increments an unattributed counter/map for hook events where `tgid==0` (instead of silently ignoring), and loader reports unattributed totals/deltas/events.
- **Event-time identity / PID-reuse safety (2026-11-18, ADR 0005):** BPF map key is now `{start_ns, tgid, uid}`; the value carries first-observed `cgroup_id` and `comm`. The loader verifies live `/proc` identity against event-time start ticks before using `exe`, caches enrichment per process generation, and emits an extended TSV `E` line with `start_ns`/`cgroup_id`. Counter rows for reused PIDs are now distinct.
- **TCP+UDP and per-interface keys (2026-11-18, ADR 0006):** the key is now `{start_ns, tgid, uid, ifindex, proto}`. `proto` is `1`=TCP, `2`=UDP; QUIC is counted as UDP payload (framing not identified). UDP send uses `fexit/udp_sendmsg`; TCP/UDP receive use entry kprobes that stash `sk` by `pid_tgid` for the return probes. `ifindex` is resolved from `skc_bound_dev_if`, then `sk_rx_dst_ifindex`, then `sk_dst_cache->dev->ifindex`; `0` means unknown and is reported as an unresolved-interface bucket, not guessed.
- **Consumers and persisted identity:** `Flow.Linux.Monitoring.EbpfSnapshotParser` reads the extended line (still accepting 11-field legacy lines and 13-field event-time lines), prefers event-time `start_ns` for `StartTimeUtc`, populates `ProcessIdentity.CgroupId`, sets `NetworkUsageEvent.InterfaceName` from the resolved interface (or `null` when unresolved), and records explicit attribution notes for unverified identity and unresolved interfaces. `ProcessIdentity.StableKey` was narrowed to `pid:startUnixMs:uid` so enrichment does not split a generation.
- **Machine-readable loader mode:** `prototype/bpf/flow_tcp_bytes_loader` supports `tsv` output (`M` meta with clk_tck/boot time, `S` snapshots, `E` per-row deltas with identity/proto/interface fields, `U` unattributed deltas/events) to enable integration.
- **Flow.Linux provider integration:** `Flow.Linux.Monitoring.EbpfMonitor` (renamed from `EbpfTcpMonitor`; loader/file names are historical) launches the privileged prototype loader in TSV mode and converts snapshot deltas into `NetworkUsageEvent`s with:
  - **Measurement semantics:** provider `ebpf` / `fexit/tcp_sendmsg+udp_sendmsg, kretprobe/tcp_recvmsg+udp_recvmsg`; bytes from positive return values of TCP/UDP send/recv functions (payload at the hook point). QUIC appears as UDP. ICMP and other non-TCP/UDP paths are not counted. `ebpf-tcp` remains accepted only as a configuration alias.
  - **Process identity:** `ProcessIdentity` built from TGID, UID, event-time start time, executable path when live-enrichment was verified, and kernel cgroup id when available.
  - **Attribution:** attributed per-process/TGID events for `E` deltas; explicit unattributed events for `U` deltas (`tgid==0` hook context). Interface is persisted when it could be resolved.
  - **Limitations:** requires privileged loader/BPF attach, subprocess-based integration (not in-process libbpf), snapshot-delta granularity, no map expiry for dead generations, cgroup id is opaque (not a path/container name), persisted minute buckets do **not** separate TCP from UDP (they aggregate both for the same process/interface), and UDP/interface coverage/accuracy/overhead still require privileged validation.

### Unit-test evidence for this increment (non-privileged)
- `dotnet test Flow.slnx` → **35/35 passed** (Domain 1, Application 4, Infrastructure 9, Linux 16, Daemon 1, System 4).
- Parser tests cover: extended identity fields and cgroup propagation, PID reuse producing distinct stable keys, stable key ignoring mutable exe enrichment, legacy 11-field loader compatibility, 13-field event-time compatibility, protocol/interface propagation, unresolved-interface notes, exited-process comm fallback + attribution notes, malformed/zero-delta handling, unattributed events, metadata defaults, and loader-failure stderr surfacing.
- BPF program, skeleton, and loader were **compiled** with clang/bpftool/libbpf (`0` errors; only pre-existing vmlinux.h warnings). They were **not loaded/attached** in this increment because that requires root/CAP_BPF and no non-interactive privilege path is available here.

### Controlled validation evidence (two TCP-only runs, 2026-11-18)
- `scripts/validate-ebpf-attribution.py` attaches the loader, runs a known-size loopback TCP transfer then a known-size loopback UDP transfer in the same process, and compares the expected process's attributed TX/RX per protocol against userspace payload counts. It also verifies that expected-process rows resolve to `lo`, records background/unattributed bytes, loader CPU time, and peak RSS, and writes a Markdown report under `docs/measurements/`. Run as root: `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16` (`--size-mib` is the payload per protocol).
- **Reports below predate the UDP/per-interface harness** and are TCP-only evidence; they do not contain interface/protocol checks.
- **Rejected first run** (`ebpf-controlled-20261118T042842Z.md`): 16 MiB payload measured as 16,777,216 TX/RX for the expected process with 0 unattributed bytes, but the same Python TGID appeared as two rows with different `start_ns` (main thread and server thread) and the server-thread row failed `/proc` verification. Cause: `task_struct.start_time` is per-thread. The BPF program now reads `group_leader->start_time`; the harness now asserts one generation and verified identity for the expected TGID.
- **Accepted rerun** (`ebpf-controlled-20261118T044634Z.md`, after the fix): expected TGID 47748 produced **one row** with `identity verified: yes`, executable `/usr/bin/python3.14`, cgroup `24677`, and attributed TX/RX of exactly **16,777,216** each (100.0% of payload). Background traffic was separated: VS Code (`code`, TGID 44356) contributed 2,777 TX / 10,108 RX and was also identity-verified. Unattributed TX/RX were 0. `lo` deltas were 16,796,684 per direction (IP/link headers and ACKs above payload). Loader CPU was 0.004 s (0.097% of one core) with peak RSS 3,672 KiB.
- **What this proves:** on this kernel, the TCP hooks account for the controlled payload exactly at the process level, the process-generation key survives multithreaded traffic, the live-identity guard works, and the explicit unattributed bucket stayed empty for this transfer.
- **What this does not prove:** UDP/QUIC, interface resolution, physical-interface behavior, namespaces/containers, VPNs/tunnels, virtual-interface double-counting, the combined TCP+UDP provider path, long-running/steady-state overhead, map growth/expiry, or accuracy for high-connection-count workloads. The CPU/RSS numbers are a single short-run datapoint, not a production overhead claim.

### Not yet satisfied (gate still open)
- **Scope of accuracy evidence:** the only accepted report is a 16 MiB loopback TCP run. The new TCP+UDP/per-interface hooks and harness are built but have not been run with privileges; physical interfaces and sustained/high-connection workloads are unmeasured.
- **Coverage:** ICMP and other non-TCP/UDP paths are not counted. UDP/QUIC is counted as UDP payload in source but its accuracy and attach behavior are unverified. Interface resolution is implemented from socket state; unconnected/uncached UDP sockets and some RX paths may yield `ifindex=0`, which is reported as unresolved rather than guessed.
- **Persistence semantics:** minute buckets aggregate TCP and UDP for the same process/interface; protocol is available in the loader stream but not persisted as a separate dimension.
- **Namespaces/containers/VPNs:** cgroup id is captured but not mapped to a path/container; network namespaces and tunnel/VPN layers are not yet handled or measured.
- **Overhead/edge cases:** only a single short CPU/RSS datapoint exists; longer runs, map growth/expiry, short-lived process churn, virtual-interface double-counting, and provider start/stop gaps are not yet measured.
- **Application grouping:** not implemented beyond exe/comm hints.


### Working hypothesis (subject to revision)
`fexit/tcp_sendmsg` + `kretprobe/tcp_recvmsg` was the validated TCP-only starting point; the current prototype adds `fexit/udp_sendmsg` and UDP receive plus per-interface keys. Its expanded semantics remain unvalidated until the UDP/interface harness is run with privileges.

Updates to this document must track prototype results and recorded decisions in `docs/decisions/`.
