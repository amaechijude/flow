# Flow attribution model (initial)

> Status: **Phase 0 baseline / planning**, with an event-time identity hardening increment implemented in the prototype and parser (ADR 0005). Privileged accuracy, coverage, and overhead evidence is still outstanding; do not treat the gate as closed.

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
- **Measurement point (current prototype):** bytes taken from positive return values of `tcp_sendmsg` / `tcp_recvmsg` kernel functions (TCP-path bytes at those hooks). Not equivalent to IP/link/interface counters.
- **Tooling note:** loader `startticks` parsing bug produced `0` in an earlier run; fixed and subsequently confirmed to report non-zero startticks in rerun.

### Recent prototype enhancements (source + unit tests; privileged rerun pending)
- **Explicit unattributed accounting:** BPF increments an unattributed counter/map for hook events where `tgid==0` (instead of silently ignoring), and loader reports unattributed totals/deltas/events.
- **Event-time identity / PID-reuse safety (2026-11-18, ADR 0005):** BPF map key is now `{start_ns, tgid, uid}`; the value carries first-observed `cgroup_id` and `comm`. The loader verifies live `/proc` identity against event-time start ticks before using `exe`, caches enrichment per process generation, and emits an extended TSV `E` line with `start_ns`/`cgroup_id`. Counter rows for reused PIDs are now distinct.
- **Consumers and persisted identity:** `Flow.Linux.Monitoring.EbpfTcpSnapshotParser` reads the extended line (still accepting 11-field legacy lines), prefers event-time `start_ns` for `StartTimeUtc`, populates `ProcessIdentity.CgroupId` from the kernel cgroup id, and records an explicit attribution note when live identity could not be verified. `ProcessIdentity.StableKey` was narrowed to `pid:startUnixMs:uid` so enrichment does not split a generation.
- **Machine-readable loader mode:** `prototype/bpf/flow_tcp_bytes_loader` supports `tsv` output (`M` meta with clk_tck/boot time, `S` snapshots, `E` per-TGID deltas with identity fields, `U` unattributed deltas/events) to enable integration.
- **Flow.Linux provider integration:** `Flow.Linux.Monitoring.EbpfTcpMonitor` launches the privileged prototype loader in TSV mode and converts snapshot deltas into `NetworkUsageEvent`s with:
  - **Measurement semantics:** `ebpf-tcp` / `fexit/tcp_sendmsg + kretprobe/tcp_recvmsg`; bytes from positive return values of TCP send/recv functions (TCP-path bytes at hook point). UDP/non-TCP not counted. Interface-level per-event attribution not provided by this hook.
  - **Process identity:** `ProcessIdentity` built from TGID, UID, event-time start time, executable path when live-enrichment was verified, and kernel cgroup id when available.
  - **Attribution:** attributed per-process/TGID events for `E` deltas; explicit unattributed events for `U` deltas (`tgid==0` hook context). Application hint derived from exe/comm when available.
  - **Limitations:** requires privileged loader/BPF attach, subprocess-based integration (not in-process libbpf), snapshot-delta granularity, no map expiry for dead generations, cgroup id is opaque (not a path/container name), no per-interface mapping, and coverage/accuracy/overhead still subject to gate validation.

### Unit-test evidence for this increment (non-privileged)
- `dotnet test Flow.slnx` → **31/31 passed** (Domain 1, Application 4, Infrastructure 9, Linux 12, Daemon 1, System 4).
- Parser tests cover: extended identity fields and cgroup propagation, PID reuse producing distinct stable keys, stable key ignoring mutable exe enrichment, legacy 11-field loader compatibility, exited-process comm fallback + attribution notes, malformed/zero-delta handling, unattributed events, and metadata defaults.
- BPF program and loader were **compiled** with clang/bpftool/libbpf (`0` errors; only pre-existing vmlinux.h warnings). They were **not loaded/attached** in this increment because that requires root/CAP_BPF and no non-interactive privilege path is available here.

### Controlled validation harness (first run recorded; identity fix needs rerun)
- `scripts/validate-ebpf-attribution.py` performs a known-size loopback TCP transfer while the loader samples, then compares the expected process's attributed TX/RX against userspace payload counts and `lo` counters from `/proc/net/dev`; it also records background/unattributed bytes, loader CPU time, and peak RSS and writes a Markdown report under `docs/measurements/`.
- Must be run as root: `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16`.
- **First run (2026-11-18, report `docs/measurements/ebpf-controlled-20261118T042842Z.md`):** 16 MiB payload was measured as 16,777,216 TX and 16,777,216 RX for the expected process (100.0%), with 0 unattributed bytes. `lo` deltas were 16,793,734 per direction (IP/link headers). Loader CPU was ~0.01 s (0.242% of one core) and peak RSS 3,592 KiB; the CPU sample is coarse and is not accepted as overhead evidence.
- **Identity defect exposed by that run:** the same Python TGID appeared as two rows with different `start_ns` (main thread and server thread), and the server-thread row failed live `/proc` verification. Cause: `task_struct.start_time` is per-thread. The BPF program now reads `group_leader->start_time`, and the harness asserts one generation per expected TGID and verified identity. The recorded byte totals remain useful, but **the run is not accepted as gate evidence until the fixed program is rerun**.
- Background traffic from other processes was also captured (18 bytes each direction), which is expected for a system-wide hook; the harness now reports it separately from the expected process.

### Not yet satisfied (gate still open)
- **Privileged rerun after the thread-identity fix:** the modified BPF/loader compiles and the parser is unit-tested, but the group-leader start-time fix has not been rerun with privileges.
- **Controlled comparison:** the first run matched payload bytes exactly but exposed the thread-split identity defect; no accepted accuracy/overhead numbers are recorded until the fixed program is rerun.
- **Coverage:** UDP/QUIC, ICMP, non-TCP paths, traffic outside attach lifetime, and per-interface attribution are not counted by this provider.
- **Namespaces/containers/VPNs:** cgroup id is captured but not mapped to a path/container; network namespaces and tunnel/VPN layers are not yet handled or measured.
- **Overhead/edge cases:** only a single coarse CPU/RSS datapoint exists; longer runs, map growth/expiry, short-lived process churn, virtual-interface double-counting, and provider start/stop gaps are not yet measured.
- **Application grouping:** not implemented beyond exe/comm hints.


### Working hypothesis (subject to revision)
`fexit/tcp_sendmsg` + `kretprobe/tcp_recvmsg` is a viable **TCP-only** starting point for per-task byte observation on this kernel; Flow’s production provider must expand/replace this based on validated semantics for broader protocol coverage and robust attribution.

Updates to this document must track prototype results and recorded decisions in `docs/decisions/`.
